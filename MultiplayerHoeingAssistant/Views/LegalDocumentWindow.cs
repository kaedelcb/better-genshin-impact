using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusBGI.Legal;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>Read-only offline viewer. No external editor or Markdown association is required.</summary>
public sealed class LegalDocumentWindow : Window
{
    private readonly TextBox _text;
    private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);

    private LegalDocumentWindow(Window? owner, string initialDocument)
    {
        Owner = owner;
        Title = "关于与开源许可 · Nexus BGI";
        Width = 800; Height = 620; MinWidth = 360; MinHeight = 340;
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(26, 26, 46));
        Foreground = Brushes.WhiteSmoke;
        Helpers.DpiAwarenessController.Initialize(this);

        _documents["GPL v3 全文"] = "LICENSE";
        _documents["项目与源码说明"] = "README.md";
        _documents["第三方组件与资源声明"] = "THIRD-PARTY-NOTICES.md";
        var licenseRoot = Path.Combine(AppContext.BaseDirectory, "licenses");
        try
        {
            if (Directory.Exists(licenseRoot))
                foreach (var path in Directory.EnumerateFiles(licenseRoot).OrderBy(Path.GetFileName, StringComparer.Ordinal))
                    if (Path.GetExtension(path) is ".txt" or ".md")
                        _documents["第三方许可证 · " + Path.GetFileName(path)] = "licenses/" + Path.GetFileName(path);
        }
        catch (IOException) { /* The main notices remain available; missing files are reported below. */ }
        catch (UnauthorizedAccessException) { }

        var panel = new DockPanel { Margin = new Thickness(20) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = "关于与开源许可", FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = OpenSourceInfo.VersionDescription, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
        var selector = new ComboBox { ItemsSource = _documents.Keys, MinHeight = 30, MaxDropDownHeight = 280 };
        header.Children.Add(selector);
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var footer = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var source = new Button { Content = OpenSourceInfo.HasRevision ? "对应版本源码" : "源码仓库", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 10, 0) };
        source.Click += (_, _) => OpenSource(this);
        var close = new Button { Content = "关闭", Padding = new Thickness(20, 6, 20, 6), IsCancel = true };
        close.Click += (_, _) => Close();
        footer.Children.Add(source); footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        _text = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = new SolidColorBrush(Color.FromRgb(22, 33, 62)), Foreground = Brushes.WhiteSmoke,
            FontSize = 13, Padding = new Thickness(14), BorderThickness = new Thickness(0)
        };
        panel.Children.Add(_text); Content = panel;
        selector.SelectionChanged += (_, _) => LoadDocument(_documents[(string)selector.SelectedItem]);
        selector.SelectedItem = _documents.FirstOrDefault(p => p.Value == initialDocument).Key ?? _documents.Keys.First();
    }

    private void LoadDocument(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        try { _text.Text = File.ReadAllText(path).TrimEnd('\0'); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _text.Text = $"无法读取随包文件：{relativePath}\n\n请确认发布包携带完整许可资料。\n{ex.Message}"; }
        _text.ScrollToHome();
    }

    public static void ShowNotices(Window? owner = null, string initialDocument = "LICENSE")
        => new LegalDocumentWindow(owner, initialDocument).ShowDialog();

    public static void OpenSource(Window? owner = null)
    {
        try { Process.Start(new ProcessStartInfo(OpenSourceInfo.SourceUrl) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { MessageBox.Show(owner, "无法打开浏览器。源码地址：\n" + OpenSourceInfo.SourceUrl + "\n\n" + ex.Message, "源码入口"); }
    }
}
