using System.Threading.Tasks;
using Xunit;

namespace DefensiveCopyAnalyzer.Tests;

/// <summary>
/// 三个 Code Fix 的端到端验证：修复输出文本符合预期、可编译，且同编号诊断消除。
/// </summary>
public class CodeFixTests
{
    private const string Preamble = @"
using System;
using System.Collections.Generic;
";

    private const string MakeReadonlyKey = "改为 readonly 结构体（成员均为只读，可安全标注）";
    private const string RemoveReadonlyKey = "移除字段的 readonly 修饰符（使调用直接作用于原实例）";
    private const string RefReturnKey = "改为按引用返回（ref return），消除临时副本";

    [Fact]
    public Task 修复1_全只读成员的可变结构体可转为readonly_struct()
    {
        // Log 未标注 readonly（因此触发 DLD001），但其方法体不写入实例状态，
        // 可安全整体升级为 readonly struct。
        var source = Preamble + @"
struct Stats
{
    private readonly int _count;
    public Stats(int count) { _count = count; }
    public void Log() { Console.WriteLine(_count); }
}

class Service
{
    private readonly Stats _stats = new Stats(3);
    public void Run()
    {
        _stats.Log();
    }
}
";
        var fixedSource = Preamble + @"
readonly struct Stats
{
    private readonly int _count;
    public Stats(int count) { _count = count; }
    public void Log() { Console.WriteLine(_count); }
}

class Service
{
    private readonly Stats _stats = new Stats(3);
    public void Run()
    {
        _stats.Log();
    }
}
";
        return CodeFixHarness.ApplyAndVerifyAsync(source, fixedSource, "DLD001", MakeReadonlyKey);
    }

    [Fact]
    public Task 修复2_移除readonly字段修饰符后诊断消除()
    {
        var source = Preamble + @"
struct LoginState
{
    private int _failureCount;
    public void RecordFailure() => _failureCount++;
}

class AccountStatus
{
    private readonly LoginState _loginState;

    public void OnLoginFailed()
    {
        _loginState.RecordFailure();
    }
}
";
        var fixedSource = Preamble + @"
struct LoginState
{
    private int _failureCount;
    public void RecordFailure() => _failureCount++;
}

class AccountStatus
{
    private LoginState _loginState;

    public void OnLoginFailed()
    {
        _loginState.RecordFailure();
    }
}
";
        return CodeFixHarness.ApplyAndVerifyAsync(source, fixedSource, "DLD001", RemoveReadonlyKey);
    }

    [Fact]
    public Task 修复3_表达式体属性改为ref返回()
    {
        var source = Preamble + @"
struct LoginState
{
    private int _failureCount;
    public void RecordFailure() => _failureCount++;
}

class AccountStore
{
    private LoginState _current;
    public LoginState Current => _current;
}

class Demo
{
    static void Run(AccountStore store)
    {
        store.Current.RecordFailure();
    }
}
";
        var fixedSource = Preamble + @"
struct LoginState
{
    private int _failureCount;
    public void RecordFailure() => _failureCount++;
}

class AccountStore
{
    private LoginState _current;
    public ref LoginState Current => ref _current;
}

class Demo
{
    static void Run(AccountStore store)
    {
        store.Current.RecordFailure();
    }
}
";
        return CodeFixHarness.ApplyAndVerifyAsync(source, fixedSource, "DLD002", RefReturnKey);
    }

    [Fact]
    public async Task 存在写入成员时不提供readonly_struct修复()
    {
        // RecordFailure 写入字段，CanSafelyBecomeReadOnly 必须拒绝，
        // 此时 DLD001 上只应存在“移除字段 readonly”这一修复。
        var source = Preamble + @"
struct LoginState
{
    private int _failureCount;
    public void RecordFailure() => _failureCount++;
}

class AccountStatus
{
    private readonly LoginState _loginState;
    public void OnLoginFailed()
    {
        _loginState.RecordFailure();
    }
}
";
        var document = CodeFixHarness.CreateDocument(source);
        var actions = await CodeFixHarness.CollectFixesAsync(document, "DLD001");

        Assert.DoesNotContain(actions, a => a.EquivalenceKey == MakeReadonlyKey);
        Assert.Contains(actions, a => a.EquivalenceKey == RemoveReadonlyKey);
    }
}
