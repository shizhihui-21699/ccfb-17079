using System;
using System.Collections.Generic;

namespace BankingLogin;

/// <summary>
/// 修复范式：与 Program.cs 中的漏洞写法一一对应。
/// 本文件预期不产生任何 DLD 告警。
/// </summary>
internal static class FixedPatterns
{
    // 范式 1（对应 DLD001）：去掉字段的 readonly，调用直接作用于原实例。
    private sealed class AccountStatusFixed
    {
        private LoginFailureState _loginState;

        public void OnLoginFailed() => _loginState.RecordFailure();

        public LoginFailureState Snapshot => _loginState;
    }

    // 范式 1 变体：保留封装的前提下，用 ref 返回暴露可变引用。
    private sealed class AccountStatusRefReturn
    {
        private LoginFailureState _loginState;

        public ref LoginFailureState LoginState => ref _loginState;
    }

    // 范式 2（对应 DLD002）：集合中的结构体需要原地修改时，
    // 用局部变量取出、修改后写回，或改用 ref 索引器/数组。
    public static void UpdateListEntry(List<LoginFailureState> history, int index)
    {
        var copy = history[index];
        copy.RecordFailure();
        history[index] = copy;
    }

    // 范式 3（对应 DLD003）：避免经接口持有结构体；
    // 泛型场景加 struct 约束，JIT 走 constrained 调用不装箱。
    public static void BumpNoBox<T>(T state) where T : struct, ICanRecordFailure
        => state.RecordFailure();

    public static void Demonstrate()
    {
        var fixedAccount = new AccountStatusFixed();
        for (var i = 0; i < 100; i++)
        {
            fixedAccount.OnLoginFailed();
        }
        Console.WriteLine($"[修复范式1] 100 次失败后计数={fixedAccount.Snapshot.FailureCount}，锁定={fixedAccount.Snapshot.IsLocked}。");

        var refAccount = new AccountStatusRefReturn();
        refAccount.LoginState.RecordFailure();
        refAccount.LoginState.RecordFailure();
        Console.WriteLine($"[修复范式1-ref] 计数={refAccount.LoginState.FailureCount}。");

        var history = new List<LoginFailureState> { new() };
        UpdateListEntry(history, 0);
        Console.WriteLine($"[修复范式2] 列表元素写回后计数={history[0].FailureCount}。");

        BumpNoBox(new InterfaceLoginState());
        Console.WriteLine("[修复范式3] struct 约束泛型调用无装箱。");
    }
}
