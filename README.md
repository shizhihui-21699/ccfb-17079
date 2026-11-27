# DefensiveCopyAnalyzer —— 结构体隐式拷贝静态分析器

针对网银登录服务中“可变结构体挂在只读字段上，编译器生成防御性拷贝导致失败计数/锁定标记永不更新、密码可无限爆破”一类缺陷，基于 **Roslyn** 实现的 C# 诊断分析器与代码修复。

## 一、问题原理

C# 对结构体（值类型）实例成员调用的接收者有严格的存储分类：

| 接收者形态 | 存储分类 | 调用非 readonly 成员的后果 |
| --- | --- | --- |
| 可写局部变量、`ref` 参数、普通字段、数组元素 | 可写变量 | 直接作用于原实例 |
| `readonly` 字段（含 `static readonly`）、`in`/`readonly ref` 参数、`readonly` 成员中的 `this`、`ref readonly` 局部变量/返回值 | **只读变量** | 编译器先复制整个结构体，调用作用于**防御性拷贝（defensive copy）** |
| 方法/属性/索引器按值返回的结果、`?.` 链、强制转换结果 | **临时值** | 调用作用于按值产生的**临时副本**，表达式结束即丢弃 |
| 转成接口类型的结构体、无约束/`class` 约束泛型上的接口或虚调用 | **装箱** | IL 层 `box` 指令在堆上生成独立**装箱副本** |

对只读位置上的**非 readonly 成员**发起调用时，编译器为保证 `this` 不被修改，会把实例拷贝到一个临时局部变量，方法内对字段的全部写入都落在该副本上——这正是失败次数始终为 0、锁定标记永不置位的原因。

## 二、诊断规则

### DLD001 —— 只读存储位置上的防御性拷贝
对 `readonly` 字段、`in`/`readonly ref` 参数、`readonly` 成员中的 `this`、`ref readonly` 局部变量/返回值等**只读位置**上的可变结构体调用非 readonly 实例成员。

```csharp
private readonly LoginFailureState _loginState;
_loginState.RecordFailure();   // DLD001：改动落在防御性拷贝
```

### DLD002 —— 按值返回临时值上的副本
对方法、属性、索引器（含 `List<T>.this[]`）**按值返回**的结构体临时值调用非 readonly 成员，或经 `?.` 解包 `Nullable<T>` 后调用。

```csharp
_history[0].RecordFailure();        // DLD002：List<T> 索引器按值返回
store.CurrentByValue().RecordFailure();
maybe?.RecordFailure();
```

### DLD003 —— 接口装箱 / 泛型装箱
- 结构体到接口类型的装箱转换（`(IFoo)s`、`IFoo x = s`）后经接口槽位调度；
- 对**无值类型约束**或 **`class` 约束**的泛型类型参数 `T` 调用接口成员/虚方法，JIT 无法使用 `constrained.` 前缀，值类型实参每次调用都 `box`。

```csharp
ICanRecordFailure boxed = state;   // DLD003：装箱
boxed.RecordFailure();

static void Bump<T>(T s) where T : ICanRecordFailure => s.RecordFailure(); // DLD003
```

**泛型约束判定（按编译器规则）：**

| 约束 | IL 调用方式 | 是否装箱 |
| --- | --- | --- |
| `where T : struct, IFoo` / `where T : unmanaged` | `constrained.` + 直接调用 | 否（仅在只读位置调用非 readonly 成员时退化为 DLD001） |
| `where T : class, IFoo` | 接口/虚调度 | 是 |
| 无值类型约束（仅 `IFoo` 等） | 值类型实参需装箱 | 是 |

## 三、降噪规则（避免误报）

以下情形**不报告**，确保坐标、金额这类纯值结构体零噪声：

- `readonly struct` 的全部成员；
- 已标注 `readonly` 的实例方法/属性/索引器（只读成员不触发拷贝）；
- 枚举、元组、`Nullable<T>` 包装、编译器内置基元类型；
- 重写自 `System.Object` 的 `ToString`/`Equals`/`GetHashCode`（编译器对重写版本不生成防御性拷贝，非重写版本只装箱不修改调用方实例）；
- 对可写位置（普通字段、局部变量、`ref` 返回、数组元素）的正常调用；
- 装箱目标为 `object` 而非接口（不直接造成“修改丢失”，避免噪声）。

## 四、代码修复（Code Fix）

分析器对可安全修复的诊断提供三个代码操作：

1. **改为 `readonly struct`**：仅当结构体所有实例字段均为 `readonly`、无可写属性/索引器/事件时提供（按编译器规则保证可编译）。适用于本就不该可变、却漏标 `readonly` 的纯值类型。
2. **移除字段的 `readonly` 修饰符**：让调用直接作用于原实例。适用于 `LoginFailureState` 这类确实需要原地累加状态的场景。
3. **改为按引用返回（`ref return`）**：把“直接返回实例字段”的属性/方法/索引器从按值返回改为 `ref` 返回，消除临时副本。字段只读时不提供（只能给 `ref readonly`，调用方仍无法修改）。

接口装箱（DLD003）不提供自动改写，因为修复涉及 API 形态调整（避免接口持有结构体、为泛型补 `struct` 约束等），由开发人员按规则文档决策。

## 五、项目结构

```
src/DefensiveCopyAnalyzer/             诊断分析器（netstandard2.0，仅依赖 Roslyn 编译器层，无 Workspaces）
  DiagnosticDescriptors.cs             三条诊断描述符
  SymbolRules.cs                       可变结构体/非readonly成员/泛型约束/装箱判定
  ReceiverClassifier.cs                接收者存储分类（可写/只读/临时值）
  DefensiveCopyDiagnosticAnalyzer.cs   语法 + IOperation 双层诊断
src/DefensiveCopyAnalyzer.CodeFixes/   代码修复（引用 Workspaces，与分析器分程序集以满足 RS1038）
  DefensiveCopyCodeFixProvider.cs      三个代码修复
tests/DefensiveCopyAnalyzer.Tests/     28 个 xUnit 单元/修复测试
samples/BankingLogin/                  网银登录漏洞复现与修复范式（含运行时证据）
```

> 诊断器与代码修复拆分到两个程序集：命令行编译期不会加载 `Microsoft.CodeAnalysis.Workspaces`，分析器程序集引用它会触发 RS1038 并导致行为不可预测。两个程序集都以 `OutputItemType="Analyzer"` 引用即可在 IDE 中同时获得诊断与修复。

## 六、使用方式

以分析器引用方式加入目标项目（诊断器与代码修复两个程序集都要引用）：

```xml
<ItemGroup>
  <ProjectReference Include="..\src\DefensiveCopyAnalyzer\DefensiveCopyAnalyzer.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  <ProjectReference Include="..\src\DefensiveCopyAnalyzer.CodeFixes\DefensiveCopyAnalyzer.CodeFixes.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
</ItemGroup>
```

也可将项目打包（`dotnet pack`）后以 `PackageReference` 引入。诊断默认以警告（Warning）级别启用，编号 `DLD001`/`DLD002`/`DLD003`，可在 `.editorconfig` 中调整严重级别：

```ini
[*.cs]
dotnet_diagnostic.DLD001.severity = error
dotnet_diagnostic.DLD002.severity = error
dotnet_diagnostic.DLD003.severity = warning
```

## 七、验证

```bash
dotnet test        # 28 个测试全部通过
dotnet run --project samples/BankingLogin   # 漏洞代码计数=0/未锁定，修复范式计数=100/锁定
```
