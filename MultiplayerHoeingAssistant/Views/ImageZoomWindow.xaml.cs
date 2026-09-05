using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 桌面监控"点击放大"窗口：以原始像素展示一帧截图，滚轮以视口中心为锚点缩放，
/// 双击在"适应窗口 / 100%"间切换，可直接保存当前帧 JPEG。
/// 纯视图层窗口（无 ViewModel）：帧数据经构造传入快照，不随后续刷新变化。
/// </summary>
public partial class ImageZoomWindow : Window
{
    private readonly byte[]? _jpeg;
    private readonly string _suggestedName;
    private double _scale = 1.0;
    /// <summary>当前是否为"适应窗口"模式（双击切换用）。</summary>
    private bool _fitMode = true;

    public ImageZoomWindow(ImageSource image, byte[]? jpeg, string infoText, string suggestedName)
    {
        InitializeComponent();
        FullImage.Source = image;
        _jpeg = jpeg;
        _suggestedName = suggestedName;
        InfoText.Text = infoText;
        SaveButton.IsEnabled = jpeg != null;
        // 首帧默认适应窗口（截图通常比窗口大）
        Loaded += (_, _) => ApplyFit();
    }

    // ========== 缩放 ==========

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        ApplyScale(_scale * factor, keepViewportCenter: true);
        _fitMode = false;
        e.Handled = true;
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (_fitMode) ApplyScale(1.0, keepViewportCenter: false);
        else ApplyFit();
        _fitMode = !_fitMode;
        e.Handled = true;
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        ApplyFit();
        _fitMode = true;
    }

    private void ActualSize_Click(object sender, RoutedEventArgs e)
    {
        ApplyScale(1.0, keepViewportCenter: false);
        _fitMode = false;
    }

    /// <summary>适应窗口：按视口与图像的较小比缩放（小图不放大超过 100%）。</summary>
    private void ApplyFit()
    {
        if (FullImage.Source is not { } img) return;
        var vw = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth;
        var vh = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;
        if (vw <= 0 || vh <= 0 || img.Width <= 0 || img.Height <= 0) return;
        ApplyScale(Math.Min(1.0, Math.Min(vw / img.Width, vh / img.Height)), keepViewportCenter: false);
    }

    /// <summary>应用缩放；keepViewportCenter=true 时保持视口中心内容不动（滚轮缩放体验）。</summary>
    private void ApplyScale(double newScale, bool keepViewportCenter)
    {
        newScale = Math.Clamp(newScale, 0.02, 10.0);
        // 记录视口中心在内容坐标系中的位置，缩放后还原，实现"以视口中心为锚点"
        var centerX = (Scroller.HorizontalOffset + Scroller.ViewportWidth / 2) / _scale;
        var centerY = (Scroller.VerticalOffset + Scroller.ViewportHeight / 2) / _scale;
        _scale = newScale;
        ZoomScale.ScaleX = _scale;
        ZoomScale.ScaleY = _scale;
        ZoomText.Text = $"{_scale * 100:F0}%";
        if (!keepViewportCenter) return;
        // 强制布局后滚动条范围才更新
        UpdateLayout();
        Scroller.ScrollToHorizontalOffset(centerX * _scale - Scroller.ViewportWidth / 2);
        Scroller.ScrollToVerticalOffset(centerY * _scale - Scroller.ViewportHeight / 2);
    }

    // ========== 保存 ==========

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_jpeg == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存图片",
            FileName = _suggestedName,
            Filter = "JPEG 图片|*.jpg"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, _jpeg);
            InfoText.Text = $"已保存到 {dlg.FileName}";
        }
        catch (Exception ex)
        {
            InfoText.Text = $"保存失败: {ex.Message}";
        }
    }
}
