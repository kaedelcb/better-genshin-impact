using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

internal static class FormalWpfTheme
{
    internal static void Apply(Window window, [CallerFilePath] string testSource = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(testSource)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MultiplayerHoeingAssistant"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var project = Path.Combine(dir!.FullName, "MultiplayerHoeingAssistant");
        foreach (var (file, property) in new[] { ("App.xaml", "Application.Resources"), ("Views/MainWindow.xaml", "Window.Resources") })
        {
            var source = XDocument.Load(Path.Combine(project, file)).Root!;
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resource = new XElement(ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
                source.Element(ns + property)!.Elements().Select(e => new XElement(e)));
            window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(resource.ToString()));
        }
        window.Background = new SolidColorBrush(Color.FromRgb(36, 33, 63));
        window.Foreground = Brushes.White;
    }

}
