using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class RemoteProgramPickerWindow : Window
{
    private sealed record Entry(string Path, bool Directory)
    {
        public string Label => (Directory ? "📁 " : "▶ ")
            + (System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar))
                is { Length: > 0 } name ? name : Path);
    }
    private readonly MainViewModel _main;
    private readonly RemoteCenterTarget _target;
    private readonly bool _pickDirectory;
    private readonly ObservableCollection<Entry> _entries = [];
    public string? SelectedPath { get; private set; }

    private RemoteProgramPickerWindow(MainViewModel main, RemoteCenterTarget target, string initial,
        bool pickDirectory = false)
    {
        InitializeComponent();
        _main = main;
        _target = target;
        _pickDirectory = pickDirectory;
        if (pickDirectory)
        {
            Title = "选择执行端上的目录";
            SelectButton.Content = "选择目录";
        }
        Entries.ItemsSource = _entries;
        PathBox.Text = Path.IsPathFullyQualified(initial) ? Path.GetDirectoryName(initial) : "";
    }

    public static string? ShowPicker(MainViewModel main, RemoteCenterTarget target, Window owner, string initial)
    {
        var window = new RemoteProgramPickerWindow(main, target, initial) { Owner = owner };
        window.ShowDialog();
        return window.SelectedPath;
    }

    public static string? ShowFolderPicker(MainViewModel main, RemoteCenterTarget target, Window owner)
    {
        var window = new RemoteProgramPickerWindow(main, target, "", pickDirectory: true) { Owner = owner };
        window.ShowDialog();
        return window.SelectedPath;
    }

    private async void ViewLoaded(object sender, RoutedEventArgs e) => await LoadAsync(PathBox.Text);
    private async Task LoadAsync(string path)
    {
        Feedback.Text = "正在读取执行端目录…";
        var reply = await _main.RequestRemoteCenterAsync(_target, "startup.programs.list", path);
        if (reply.Status != "ok" || string.IsNullOrEmpty(reply.PayloadJson))
        { Feedback.Text = reply.Message; return; }
        try
        {
            using var json = JsonDocument.Parse(reply.PayloadJson);
            var root = json.RootElement;
            PathBox.Text = root.GetProperty("path").GetString() ?? "";
            _entries.Clear();
            foreach (var dir in root.GetProperty("directories").EnumerateArray())
                if (dir.GetString() is { } item) _entries.Add(new Entry(item, true));
            foreach (var program in root.GetProperty("programs").EnumerateArray())
                if (!_pickDirectory && program.GetString() is { } item) _entries.Add(new Entry(item, false));
            Feedback.Text = _entries.Count == 0 ? "此目录没有子目录或 exe 程序" : "";
        }
        catch (Exception ex) { Feedback.Text = "目录响应无效：" + ex.Message; }
    }
    private async void ParentClick(object sender, RoutedEventArgs e)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(PathBox.Text));
        await LoadAsync(parent ?? "");
    }
    private async void EntriesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Entries.SelectedItem is not Entry entry) return;
        if (entry.Directory) await LoadAsync(entry.Path);
        else { SelectedPath = entry.Path; DialogResult = true; }
    }
    private async void PathBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await LoadAsync(PathBox.Text);
        e.Handled = true;
    }
    private void SelectClick(object sender, RoutedEventArgs e)
    {
        if (_pickDirectory)
        {
            var path = Entries.SelectedItem is Entry { Directory: true } folder ? folder.Path : PathBox.Text;
            if (!Path.IsPathFullyQualified(path)) { Feedback.Text = "请选择执行端上的目录"; return; }
            SelectedPath = path;
            DialogResult = true;
            return;
        }
        if (Entries.SelectedItem is not Entry { Directory: false } entry)
        { Feedback.Text = "请先选中执行端上的 exe 程序"; return; }
        SelectedPath = entry.Path;
        DialogResult = true;
    }
    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
