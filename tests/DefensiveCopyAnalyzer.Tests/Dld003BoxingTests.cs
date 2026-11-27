using System.Threading.Tasks;
using Xunit;

namespace DefensiveCopyAnalyzer.Tests;

/// <summary>
/// DLD003：接口装箱，以及泛型约束下按编译器规则区分装箱/防御性拷贝。
/// </summary>
public class Dld003BoxingTests
{
    private const string Preamble = @"
using System;

interface ILoginState
{
    void RecordFailure();
}

struct LoginState : ILoginState
{
    private int _failureCount;
    public void RecordFailure() => _failureCount++;
}

struct ExplicitImpl : ILoginState
{
    private int _hits;
    void ILoginState.RecordFailure() => _hits++;
}
";

    [Fact]
    public Task 显式转换为接口时报告装箱()
    {
        var source = Preamble + @"
class Demo
{
    static void Run(LoginState state)
    {
        (/*DLD003*/(ILoginState)state).RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task 接口变量赋值后的调用报告装箱()
    {
        var source = Preamble + @"
class Demo
{
    static void Run(LoginState state)
    {
        ILoginState boxed = /*DLD003*/state;
        boxed.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task 显式接口实现经接口调度报告装箱()
    {
        var source = Preamble + @"
class Demo
{
    static void Run(ExplicitImpl impl)
    {
        ILoginState boxed = /*DLD003*/impl;
        boxed.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task 无约束泛型的接口调用每次装箱()
    {
        var source = Preamble + @"
class Policy
{
    public static void Bump<T>(T state) where T : ILoginState
    {
        /*DLD003*/state.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task class约束泛型的接口调用装箱()
    {
        var source = Preamble + @"
class Policy
{
    public static void Bump<T>(T state) where T : class, ILoginState
    {
        /*DLD003*/state.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task struct约束泛型不装箱且可写参数不报警()
    {
        var source = Preamble + @"
class Policy
{
    public static void Bump<T>(T state) where T : struct, ILoginState
    {
        state.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task struct约束泛型配合in参数按防御性拷贝报告DLD001()
    {
        var source = Preamble + @"
class Policy
{
    public static void Bump<T>(in T state) where T : struct, ILoginState
    {
        /*DLD001*/state.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task unmanaged约束不装箱()
    {
        var source = @"
struct Count
{
    public int Value;
}

class Policy
{
    public static int Get<T>(T value) where T : unmanaged
    {
        return value.GetHashCode();
    }
}
";
        return MarkedTest.RunAsync(source);
    }
}
