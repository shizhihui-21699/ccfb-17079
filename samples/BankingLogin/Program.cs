using System;
using System.Collections.Generic;

namespace BankingLogin;

/// <summary>登录失败计数状态：典型的可变结构体（失败次数、首次失败时间、锁定标记打包在一起）。</summary>
struct LoginFailureState
{
    private int _failureCount;
    private DateTime _firstFailureAt;
    private bool _locked;

    public int FailureCount => _failureCount;
    public bool IsLocked => _locked;

    public void RecordFailure()
    {
        _failureCount++;
        if (_firstFailureAt == default)
        {
            _firstFailureAt = DateTime.UtcNow;
        }
        if (_failureCount >= 5)
        {
            _locked = true;
        }
    }

    public readonly TimeSpan ElapsedSinceFirstFailure()
        => DateTime.UtcNow - _firstFailureAt;
}

/// <summary>账户状态对象：把可变结构体作为只读字段挂上去——漏洞源头。</summary>
class AccountStatus
{
    // 漏洞点 1：readonly 字段 + 可变结构体。
    private readonly LoginFailureState _loginState;

    public void OnLoginFailed()
    {
        // 编译器在此生成防御性拷贝，RecordFailure 改的是临时副本，
        // 真实字段的失败次数永远停在 0，账户永远不会被锁定。
        _loginState.RecordFailure();
    }

    public LoginFailureState Snapshot => _loginState;
}

/// <summary>带接口的结构体，演示接口装箱产生的另一份拷贝。</summary>
interface ICanRecordFailure
{
    void RecordFailure();
}

struct InterfaceLoginState : ICanRecordFailure
{
    private int _hits;
    public void RecordFailure() => _hits++;
    public int Hits => _hits;
}

/// <summary>泛型服务，演示约束不同导致的装箱/不装箱差异。</summary>
static class GenericPolicy
{
    // 无值类型约束：值类型实参每次调用都装箱，修改落在装箱副本。
    public static void BumpUnconstrained<T>(T state) where T : ICanRecordFailure
        => state.RecordFailure();

    // struct 约束：constrained 调用，不装箱。
    public static void BumpConstrained<T>(T state) where T : struct, ICanRecordFailure
        => state.RecordFailure();
}

/// <summary>坐标：纯值只读结构体，任何用法都不应报警。</summary>
readonly struct Coordinate
{
    public readonly double X;
    public readonly double Y;
    public Coordinate(double x, double y) { X = x; Y = y; }
    public readonly double DistanceTo(Coordinate other)
        => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
}

/// <summary>金额：纯值只读结构体，任何用法都不应报警。</summary>
readonly struct Money
{
    private readonly decimal _amount;
    public Money(decimal amount) { _amount = amount; }
    public decimal Amount => _amount;
    public readonly Money Add(Money other) => new(_amount + other._amount);
}

class Program
{
    // 漏洞点 2：通过列表索引器取出结构体再调用，List<T>.this[] 按值返回临时副本。
    private static readonly List<LoginFailureState> _history = new();

    static void Main()
    {
        var account = new AccountStatus();

        // 模拟无限次密码爆破：由于防御性拷贝，锁定逻辑完全失效。
        for (var i = 0; i < 100; i++)
        {
            account.OnLoginFailed();
        }
        Console.WriteLine("===== 漏洞代码（DLD001）运行时证据 =====");
        Console.WriteLine($"已模拟 100 次失败登录：失败次数={account.Snapshot.FailureCount}，锁定={account.Snapshot.IsLocked}（本应为 100/True）。");

        // 漏洞点 2 的现场。
        _history.Add(new LoginFailureState());
        _history[0].RecordFailure();

        // 漏洞点 3：接口装箱。
        InterfaceLoginState boxedState = new();
        ICanRecordFailure boxed = boxedState;
        boxed.RecordFailure();
        Console.WriteLine($"装箱副本上的命中数：{boxedState.Hits}（原实例仍为 0）。");

        // 泛型两种约束的对比。
        GenericPolicy.BumpUnconstrained(new InterfaceLoginState());
        GenericPolicy.BumpConstrained(new InterfaceLoginState());

        // 纯值只读结构体：不应产生任何告警。
        var home = new Coordinate(31.23, 121.47);
        var distance = home.DistanceTo(new Coordinate(31.24, 121.48));
        var balance = new Money(1000m).Add(new Money(250m));
        Console.WriteLine($"坐标距离={distance:F4}，金额={balance.Amount}。");

        Console.WriteLine();
        Console.WriteLine("===== 修复范式（零 DLD 告警）=====");
        FixedPatterns.Demonstrate();
    }
}
