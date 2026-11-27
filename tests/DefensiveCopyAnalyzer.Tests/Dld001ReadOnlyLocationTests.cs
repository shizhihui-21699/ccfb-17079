using System.Threading.Tasks;
using Xunit;

namespace DefensiveCopyAnalyzer.Tests;

/// <summary>
/// DLD001：只读存储位置上的防御性拷贝。
/// 覆盖网银登录状态场景、in 参数、readonly 成员 this、ref readonly 局部变量，以及全部降噪场景。
/// </summary>
public class Dld001ReadOnlyLocationTests
{
    private const string MutableStructPreamble = @"
using System;
using System.Collections.Generic;

struct LoginState
{
    private int _failureCount;
    private DateTime _firstFailureAt;
    private bool _locked;

    public int FailureCount => _failureCount;

    public void RecordFailure()
    {
        _failureCount++;
        if (_firstFailureAt == default) _firstFailureAt = DateTime.UtcNow;
        if (_failureCount >= 5) _locked = true;
    }

    public bool IsLocked() => _locked;
}
";

    [Fact]
    public Task 银行场景_只读字段上的累加方法触发DLD001()
    {
        var source = MutableStructPreamble + @"
class AccountStatus
{
    private readonly LoginState _loginState;

    public void OnLoginFailed()
    {
        /*DLD001*/_loginState.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task 静态只读字段同样触发()
    {
        var source = MutableStructPreamble + @"
class Globals
{
    private static readonly LoginState Shared = new LoginState();

    public static void Fail()
    {
        /*DLD001*/Shared.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task in参数上的调用触发()
    {
        var source = MutableStructPreamble + @"
class Policy
{
    public static void Check(in LoginState state)
    {
        /*DLD001*/state.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task readonly局部ref上的调用触发()
    {
        var source = MutableStructPreamble + @"
class Holder
{
    private LoginState _state;
    public ref readonly LoginState StateRef => ref _state;

    public void Check()
    {
        ref readonly LoginState r = ref StateRef;
        /*DLD001*/r.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task readonly方法中的this调用触发()
    {
        var source = @"
struct Counter
{
    private int _value;

    public void Bump() { _value++; }

    public readonly int Snapshot()
    {
        /*DLD001*/Bump();
        return _value;
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task 普通可写字段和局部变量不报警()
    {
        var source = MutableStructPreamble + @"
class AccountStatus
{
    private LoginState _loginState;

    public void OnLoginFailed()
    {
        _loginState.RecordFailure();
        var local = new LoginState();
        local.RecordFailure();
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task readonly结构体的坐标和金额不报警()
    {
        var source = @"
readonly struct Coordinate
{
    public readonly double X;
    public readonly double Y;
    public Coordinate(double x, double y) { X = x; Y = y; }
    public double DistanceTo(Coordinate other)
        => System.Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
}

readonly struct Money
{
    private readonly decimal _amount;
    public Money(decimal amount) { _amount = amount; }
    public decimal Amount => _amount;
    public Money Add(Money other) => new Money(_amount + other._amount);
}

class Wallet
{
    private readonly Coordinate _home = new Coordinate(1, 2);
    private readonly Money _balance = new Money(100m);

    public void Report()
    {
        var d = _home.DistanceTo(new Coordinate(3, 4));
        var b = _balance.Add(new Money(5m));
        System.Console.WriteLine(d + b.Amount);
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task readonly成员和object重写方法不报警()
    {
        var source = @"
struct Tagged
{
    private int _id;
    public Tagged(int id) { _id = id; }

    public readonly override string ToString() => $""{_id}"";
    public readonly override int GetHashCode() => _id;
    public readonly override bool Equals(object obj) => obj is Tagged t && t._id == _id;
}

class Box
{
    private readonly Tagged _tagged = new Tagged(7);

    public void Show()
    {
        System.Console.WriteLine(_tagged.ToString());
        System.Console.WriteLine(_tagged.GetHashCode());
        System.Console.WriteLine(_tagged.Equals(new Tagged(7)));
    }
}
";
        return MarkedTest.RunAsync(source);
    }

    [Fact]
    public Task 枚举元组和Nullable不报警()
    {
        var source = @"
enum Status { Open, Closed }

struct Item
{
    public Status Status;
    public (int X, int Y) Point;
    public int? Maybe;
}

class Store
{
    private readonly Item _item;

    public void Read()
    {
        var s = _item.Status.ToString();
        var p = _item.Point.ToString();
        var m = _item.Maybe.GetValueOrDefault();
        System.Console.WriteLine(s + p + m);
    }
}
";
        return MarkedTest.RunAsync(source);
    }
}
