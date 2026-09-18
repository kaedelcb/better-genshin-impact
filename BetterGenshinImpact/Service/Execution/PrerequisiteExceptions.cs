namespace BetterGenshinImpact.Service.Execution;

/// <summary>R4.6 E2-9：执行权取得后 UID 复验不符（受保护执行阶段拒绝，账号未验证前不产生资源副作用）。</summary>
public sealed class AccountMismatchException : System.Exception
{
    public AccountMismatchException(string message) : base(message) { }
}

/// <summary>R4.6 D8：前置/收尾动作受控失败（携带注册表受控错误码，真实结果不虚报）。</summary>
public sealed class PrerequisiteFailedException : System.Exception
{
    public string Code { get; }

    public PrerequisiteFailedException(string code, string message) : base(message)
    {
        Code = code;
    }
}
