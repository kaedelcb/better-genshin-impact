namespace MultiplayerHoeingAssistant.Services;

/// <summary>写入前检查到旧修订；可以明确拒绝，原文件未覆盖。</summary>
public sealed class StartupRevisionConflictException(string message) : InvalidOperationException(message);

/// <summary>执行端正在运行或仍有本地编辑，尚未开始远程写入。</summary>
public sealed class StartupEditBusyException(string message) : InvalidOperationException(message);
