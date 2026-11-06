using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class RegisterSettingsWindow : Window
{
    private readonly RegisterBankViewModel _bank;
    private bool _saving;
    public RegisterSettingsWindow(RegisterBankViewModel bank, RegisterEntry entry, FrameworkElement styleSource)
    {
        _bank = bank;
        InitializeComponent();
        if (Window.GetWindow(styleSource) is { } host) Resources.MergedDictionaries.Add(host.Resources);
        foreach (var key in new[] { "GoldComboBox", "GoldComboItem", "GoldInput", "BtnGold", "BtnGhost", "Gold", "GoldLine", "White", "Dim", "CardShadow", "GoldScrollViewer" })
            if (styleSource.TryFindResource(key) is { } resource) Resources[key] = resource;
        DataContext = new RegisterSettingsViewModel(entry);
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_saving || DataContext is not RegisterSettingsViewModel vm) return;
        _saving = true;
        SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        vm.Error = "";
        try
        {
            await _bank.SaveAsync(vm.ToChange());
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex) { vm.Error = ex.Message; }
        finally { _saving = false; SaveButton.IsEnabled = CancelButton.IsEnabled = true; }
    }
}
