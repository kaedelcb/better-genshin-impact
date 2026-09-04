using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>主窗口内容区页面枚举（三态导航：主页 / 设置 / 嘟嘟可）。</summary>
public enum AppPage
{
    /// <summary>成员列表主页（耕地机）。</summary>
    Home,
    /// <summary>设置页。</summary>
    Settings,
    /// <summary>嘟嘟可 · 日志与监控系统。</summary>
    Dodoco
}

/// <summary>
/// 手写 INPC 基类（助手项目无 CommunityToolkit.Mvvm，新 ViewModel 统一继承此基类）。
/// 现有 MainViewModel 保持原样不动，新代码用本基类避免重复样板。
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
