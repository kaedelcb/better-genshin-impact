using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fischless.HotkeyCapture;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;

namespace BetterGenshinImpact.Model;

/// <summary>
/// 在页面展示快捷键配置的对象
/// </summary>
public partial class HotKeySettingModel : ObservableObject
{
    [ObservableProperty] private HotKey _hotKey;

    /// <summary>
    /// 键鼠监听、全局热键
    /// </summary>
    [ObservableProperty] private HotKeyTypeEnum _hotKeyType;

    [ObservableProperty] private string _hotKeyTypeName;

    [ObservableProperty]
    private ObservableCollection<HotKeySettingModel> _children = [];

    public string FunctionName { get; set; }

    public bool IsExpanded => true;

    /// <summary>
    /// 界面上显示是文件夹而不是快捷键
    /// </summary>
    [ObservableProperty]
    private bool _isDirectory;

    public string ConfigPropertyName { get; set; }

    public Action<object?, KeyPressedEventArgs>? OnKeyPressAction { get; set; }
    public Action<object?, KeyPressedEventArgs>? OnKeyDownAction { get; set; }
    public Action<object?, KeyPressedEventArgs>? OnKeyUpAction { get; set; }

    public bool IsHold { get; set; }

    [ObservableProperty] private bool _switchHotkeyTypeEnabled;

    /// <summary>
    /// 全局热键配置
    /// </summary>
    public HotkeyHook? GlobalRegisterHook { get; set; }

    /// <summary>
    /// 键盘监听配置
    /// </summary>
    public KeyboardHook? KeyboardMonitorHook { get; set; }

    /// <summary>
    /// 鼠标监听配置
    /// </summary>
    public MouseHook? MouseMonitorHook { get; set; }

    public HotKeySettingModel(string functionName)
    {
        FunctionName = functionName;
        IsDirectory = true;
    }

    public HotKeySettingModel(string functionName, string configPropertyName, string hotkey, string hotKeyTypeCode, Action<object?, KeyPressedEventArgs>? onKeyPressAction, bool isHold = false)
    {
        FunctionName = functionName;
        ConfigPropertyName = configPropertyName;
        HotKey = HotKey.FromString(hotkey);
        HotKeyType = (HotKeyTypeEnum)Enum.Parse(typeof(HotKeyTypeEnum), hotKeyTypeCode);
        HotKeyTypeName = HotKeyType.ToChineseName();
        OnKeyPressAction = onKeyPressAction;
        IsHold = isHold;
        SwitchHotkeyTypeEnabled = !isHold;
    }

    /// <summary>注册失败后的延迟重试次数上限。全局热键注册失败最常见的原因是旧 BGI 进程刚被 Kill
    /// 尚未完全退出（远程重启回退路径 KillBgi+RestartBgi），热键仍被旧进程占用属瞬态冲突；
    /// 若直接放弃，本次启动该快捷键（如 F11 停止）将整轮失效，且原实现只写 Debug 日志无法排查。</summary>
    private const int MaxRegisterRetries = 3;

    private static readonly TimeSpan RegisterRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>快速重试耗尽后的慢速重试轮数上限与间隔。覆盖"外部锄地助手 Kill 旧进程后立刻拉起新进程、
    /// 旧实例迟迟未退出仍占用全局热键"的长尾场景，30s 一轮共约 10 分钟窗口。
    /// 慢速重试期间快捷键配置保持原样，绝不清空（清空会经 PropertyChanged 写盘导致 F11 等永久丢失）。</summary>
    private const int MaxLongRegisterRetries = 20;

    private static readonly TimeSpan LongRegisterRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>重试代序号：新的注册/注销动作会使挂起的重试失效，避免用户改过快捷键后旧重试误注册。</summary>
    private int _registerRetryGeneration;

    public void RegisterHotKey()
    {
        // 显式注册（启动 / 用户在界面修改）使任何挂起的重试失效
        Interlocked.Increment(ref _registerRetryGeneration);
        RegisterHotKeyCore(0);
    }

    private void RegisterHotKeyCore(int attempt)
    {
        if (HotKey.IsEmpty)
        {
            return;
        }

        try
        {
            if (HotKeyType == HotKeyTypeEnum.GlobalRegister)
            {
                Hotkey hotkey = new(HotKey.ToString());
                GlobalRegisterHook?.Dispose();
                GlobalRegisterHook = new HotkeyHook();
                if (OnKeyPressAction != null)
                {
                    GlobalRegisterHook.KeyPressed -= OnKeyPressed;
                    GlobalRegisterHook.KeyPressed += OnKeyPressed;
                }
                GlobalRegisterHook.RegisterHotKey(hotkey.ModifierKey, hotkey.Key);
            }
            else
            {
                MouseMonitorHook?.Dispose();
                KeyboardMonitorHook?.Dispose();
                if (HotKey.MouseButton is MouseButton.XButton1 or MouseButton.XButton2)
                {
                    MouseMonitorHook = new MouseHook
                    {
                        IsHold = IsHold,
                        ConfigPropertyName = ConfigPropertyName
                    };

                    if (OnKeyPressAction != null)
                    {
                        MouseMonitorHook.MousePressed -= OnKeyPressed;
                        MouseMonitorHook.MousePressed += OnKeyPressed;
                    }
                    if (OnKeyDownAction != null)
                    {
                        MouseMonitorHook.MouseDownEvent -= OnKeyDown;
                        MouseMonitorHook.MouseDownEvent += OnKeyDown;
                    }
                    if (OnKeyUpAction != null)
                    {
                        MouseMonitorHook.MouseUpEvent -= OnKeyUp;
                        MouseMonitorHook.MouseUpEvent += OnKeyUp;
                    }
                    MouseMonitorHook.RegisterHotKey((MouseButtons)Enum.Parse(typeof(MouseButtons), HotKey.MouseButton.ToString()));
                }
                else
                {
                    // 如果是组合键，不支持
                    if (HotKey.Modifiers != ModifierKeys.None)
                    {
                        HotKey = HotKey.None;
                        return;
                    }
                    KeyboardMonitorHook = new KeyboardHook
                    {
                        IsHold = IsHold,
                        ConfigPropertyName = ConfigPropertyName
                    };
                    if (OnKeyPressAction != null)
                    {
                        KeyboardMonitorHook.KeyPressedEvent -= OnKeyPressed;
                        KeyboardMonitorHook.KeyPressedEvent += OnKeyPressed;
                    }
                    if (OnKeyDownAction != null)
                    {
                        KeyboardMonitorHook.KeyDownEvent -= OnKeyDown;
                        KeyboardMonitorHook.KeyDownEvent += OnKeyDown;
                    }
                    if (OnKeyUpAction != null)
                    {
                        KeyboardMonitorHook.KeyUpEvent -= OnKeyUp;
                        KeyboardMonitorHook.KeyUpEvent += OnKeyUp;
                    }

                    KeyboardMonitorHook.RegisterHotKey((Keys)Enum.Parse(typeof(Keys), HotKey.Key.ToString()));
                }
            }
        }
        catch (Exception e)
        {
            // 可见日志 + 延迟重试：瞬态冲突（旧进程未退净占用热键）应自愈，
            // 而不是静默放弃导致该快捷键整轮失效（如 F11 停止不了 BGI）
            App.GetLogger<HotKeySettingModel>().LogWarning(e,
                "快捷键注册失败：{FunctionName} [{HotKey}]（第 {Attempt} 次）", FunctionName, HotKey, attempt + 1);
            if (attempt < MaxRegisterRetries && !HotKey.IsEmpty)
            {
                ScheduleRegisterRetry(attempt + 1, RegisterRetryDelay);
            }
            else if (attempt < MaxRegisterRetries + MaxLongRegisterRetries && !HotKey.IsEmpty)
            {
                // 快速重试耗尽：转入慢速重试，覆盖旧进程被 Kill 后迟迟未退出仍占用热键的长尾场景。
                // 绝不能在此清空 HotKey——该赋值会经 PropertyChanged 把配置写成空串并持久化，
                // 导致用户快捷键（如 F11）永久丢失
                if (attempt == MaxRegisterRetries)
                {
                    App.GetLogger<HotKeySettingModel>().LogWarning(
                        "快捷键 {FunctionName} [{HotKey}] 快速重试仍失败（可能被尚未退出的旧 BGI 实例占用），" +
                        "将转为每 {Seconds} 秒继续尝试注册，快捷键配置保持不变",
                        FunctionName, HotKey, (int)LongRegisterRetryDelay.TotalSeconds);
                }
                ScheduleRegisterRetry(attempt + 1, LongRegisterRetryDelay);
            }
            else
            {
                // 慢速重试也耗尽：保留用户配置不写盘，仅记录可见错误日志。
                // 占用方退出后重启 BGI，或重新设置一次该快捷键即可恢复
                App.GetLogger<HotKeySettingModel>().LogError(e,
                    "快捷键注册最终失败：{FunctionName} [{HotKey}] 仍被占用。已保留快捷键配置，本次启动该快捷键不可用",
                    FunctionName, HotKey);
            }
        }
    }

    /// <summary>
    /// 延迟后在 UI 线程重试注册（HotkeyHook 内部创建 NativeWindow，必须在带消息泵的 UI 线程上注册）。
    /// </summary>
    private void ScheduleRegisterRetry(int nextAttempt, TimeSpan delay)
    {
        var generation = Interlocked.Increment(ref _registerRetryGeneration);
        var expectedHotKey = HotKey;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay);
                // 期间用户改了/清了快捷键，或又有新的注册/注销动作：放弃本次重试
                if (generation != _registerRetryGeneration || HotKey != expectedHotKey || HotKey.IsEmpty)
                {
                    return;
                }
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted)
                {
                    return;
                }
                await dispatcher.InvokeAsync(() => RegisterHotKeyCore(nextAttempt));
            }
            catch
            {
                // 重试链自身失败不影响主流程（下一次注册动作会重新进入重试链）
            }
        });
    }

    private void OnKeyPressed(object? sender, KeyPressedEventArgs e)
    {
        if (ShouldBlockGlobalRegister())
        {
            return;
        }

        OnKeyPressAction?.Invoke(sender, e);
    }

    private void OnKeyDown(object? sender, KeyPressedEventArgs e)
    {
        if (ShouldBlockGlobalRegister())
        {
            return;
        }

        OnKeyDownAction?.Invoke(sender, e);
    }

    private void OnKeyUp(object? sender, KeyPressedEventArgs e)
    {
        if (ShouldBlockGlobalRegister())
        {
            ResetBlockedKeyUpState();
            return;
        }

        OnKeyUpAction?.Invoke(sender, e);
    }

    private bool ShouldBlockGlobalRegister()
    {
        return HotKeyType == HotKeyTypeEnum.GlobalRegister && ChatUiHotkeyGuard.ShouldBlockHotkey(ConfigPropertyName);
    }

    private void ResetBlockedKeyUpState()
    {
        if (string.Equals(ConfigPropertyName, nameof(HotKeyConfig.OneKeyFightHotkey), StringComparison.Ordinal))
        {
            OneKeyFightTask.Instance.KeyUp();
        }
    }

    public void UnRegisterHotKey()
    {
        // 使挂起的注册重试失效，避免注销后又被旧重试重新注册
        Interlocked.Increment(ref _registerRetryGeneration);
        GlobalRegisterHook?.Dispose();
        MouseMonitorHook?.Dispose();
        KeyboardMonitorHook?.Dispose();
    }

    [RelayCommand]
    public void OnSwitchHotKeyType()
    {
        HotKeyType = HotKeyType == HotKeyTypeEnum.GlobalRegister ? HotKeyTypeEnum.KeyboardMonitor : HotKeyTypeEnum.GlobalRegister;
        HotKeyTypeName = HotKeyType.ToChineseName();
    }
}
