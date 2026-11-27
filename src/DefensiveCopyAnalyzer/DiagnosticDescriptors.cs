using Microsoft.CodeAnalysis;

namespace DefensiveCopyAnalyzer;

/// <summary>
/// 本分析器三条诊断规则的描述符。
/// </summary>
internal static class DiagnosticDescriptors
{
    public const string Dld001Id = "DLD001";
    public const string Dld002Id = "DLD002";
    public const string Dld003Id = "DLD003";

    private const string Category = "Correctness";

    /// <summary>DLD001：只读存储位置上的防御性拷贝。</summary>
    public static readonly DiagnosticDescriptor Dld001 = new(
        id: Dld001Id,
        title: "只读位置上的可变结构体成员调用会生成防御性拷贝",
        messageFormat: "在{0}上对可变结构体“{1}”调用非 readonly 成员“{2}”，编译器生成防御性拷贝，状态修改不会写回原实例",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "对 readonly 字段、in/readonly ref 参数、readonly 成员中的 this、ref readonly 局部变量等只读存储位置上的" +
                     "非 readonly 结构体实例成员进行调用时，C# 编译器会先复制整个结构体，调用作用于临时副本。" +
                     "典型后果是登录失败计数、锁定标记等累加状态永远无法更新，表现为可无限次重试。");

    /// <summary>DLD002：按值返回的临时值上的副本。</summary>
    public static readonly DiagnosticDescriptor Dld002 = new(
        id: Dld002Id,
        title: "对按值返回的结构体临时值调用可变成员，修改落在临时副本上",
        messageFormat: "“{0}”按值返回结构体“{1}”，对其临时副本调用非 readonly 成员“{2}”，状态修改在表达式结束后丢弃",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "方法、属性或索引器按值返回结构体时，调用方拿到的是一份独立临时副本（List<T> 索引器即属此类）。" +
                     "对该临时值调用非 readonly 成员、访问非 readonly  getter 时，所有状态变更都不会写回原集合或原对象。");

    /// <summary>DLD003：接口装箱产生的副本。</summary>
    public static readonly DiagnosticDescriptor Dld003 = new(
        id: Dld003Id,
        title: "结构体经接口调度发生装箱，修改作用于装箱副本",
        messageFormat: "{0}：通过接口“{1}”调度结构体“{2}”的“{3}”时发生装箱（IL: box），运行时得到独立的装箱副本，状态修改不会反映到原实例",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "将结构体转换为接口类型会触发装箱，在堆上产生独立副本；通过接口变量或显式接口实现发起的调用作用于该副本。" +
                     "对无引用类型约束（或带 class 约束）的泛型类型参数调用接口成员/虚方法时，JIT 无法使用 constrained 调用，同样每次装箱。" +
                     "带 struct/unmanaged 约束的泛型参数采用 constrained 调用，不装箱；此时仅在只读存储位置上调用非 readonly 成员才产生防御性拷贝（DLD001）。");
}
