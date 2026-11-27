using System.Threading.Tasks;
using Xunit;

namespace DefensiveCopyAnalyzer.Tests;

/// <summary>
/// DLD002：对按值返回的结构体临时值调用非 readonly 成员。
/// </summary>
public class Dld002TemporaryTests
{
    private const string Preamble = @"
using System;
using System.Collections.Generic;

struct LoginState
{
    private int _failureCount;

    public void RecordFailure() => _failureCount++;
    public readonly int Count => _failureCount;
}

class AccountStore
{
    private readonly List<LoginState> _states = new List<LoginState>();
    private LoginState[] _array = new LoginState[4];

    public LoginState CurrentByValue() => _states[0];
    public ref LoginState CurrentByRef() => ref _states[0];
    public ref readonly LoginState CurrentByRefReadonly() => ref _states[0];

    public LoginState ValueProperty => _states[0];
    public ref LoginState RefProperty => ref _states[0];

    public LoginState this[int index] => _states[index];
    public ref LoginState RefIndexer(int index) => ref _states[index];

    public LoginState? Maybe => _states.Count == 0 ? null : _states[0];
}
";

    [Fact]
    public Task List索引器取出的结构体调用触发DLD002()
    {
        var source = Preamble + @"
class Demo
{
    static void Run(List<LoginState> list)
    {
        /*DLD002*/list[0].RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task 自定义按值索引器触发()
    {
        var source = Preamble + @"
class Demo
{
    static void Run(AccountStore store)
    {
        /*DLD002*/store[1].RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task 按值方法结果和按值属性触发()
    {
        var source = Preamble + @"
class Demo
{
    static void Run(AccountStore store)
    {
        /*DLD002*/store.CurrentByValue().RecordFailure();
        /*DLD002*/store.ValueProperty.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task null条件访问链触发()
    {
        var source = Preamble + @"
class Demo
{
    static void Run(AccountStore store)
    {
        /*DLD002*/store.Maybe?.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task 按ref返回与数组元素不报警()
    {
        var source = Preamble + @"
class Demo
{
    static void Run(AccountStore store, LoginState[] array)
    {
        store.CurrentByRef().RecordFailure();
        store.RefProperty.RecordFailure();
        store.RefIndexer(0).RecordFailure();
        array[0].RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task refreadonly返回上的可变调用按DLD001报告()
    {
        var source = Preamble + @"
class Demo
{
    static void Run(AccountStore store)
    {
        /*DLD001*/store.CurrentByRefReadonly().RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task readonly属性getter在临时值上不报警()
    {
        var source = Preamble + @"
class Demo
{
    static void Run(AccountStore store)
    {
        var c1 = store.CurrentByValue().Count;
        var c2 = store.ValueProperty.Count;
        System.Console.WriteLine(c1 + c2);
    }
}
";
        return MarkedTest.RunAsync(source);
    }
}
