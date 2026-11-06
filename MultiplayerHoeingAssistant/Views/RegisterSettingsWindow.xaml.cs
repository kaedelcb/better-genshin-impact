using System.Windows;
using System.ComponentModel;
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
        MaxHeight = SystemParameters.WorkArea.Height;
        if (Window.GetWindow(styleSource) is { } host) Resources.MergedDictionaries.Add(host.Resources);
        foreach (var key in new[] { "GoldComboBox", "GoldComboItem", "GoldInput", "BtnGold", "BtnGhost", "Gold", "GoldLine", "White", "Dim", "CardShadow", "GoldScrollViewer" })
            if (styleSource.TryFindResource(key) is { } resource) Resources[key] = resource;
        DataContext = new RegisterSettingsViewModel(entry);
        _bank.PropertyChanged += BankChanged;
        _bank.Cells[entry.Number - 1].PropertyChanged += CellChanged;
        RefreshState();
        Closed += (_, _) =>
        {
            _bank.PropertyChanged -= BankChanged;
            _bank.Cells[entry.Number - 1].PropertyChanged -= CellChanged;
        };
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
    }

    private void BankChanged(object? sender, PropertyChangedEventArgs e) => RefreshState();
    private void CellChanged(object? sender, PropertyChangedEventArgs e) => RefreshState();
    private void RefreshState()
    {
        if (DataContext is not RegisterSettingsViewModel vm) return;
        var cell = _bank.Cells[vm.Number - 1];
        vm.UpdateValue(cell.Available ? cell.Entry.Value : null);
        ResetButton.IsEnabled = ResetAllButton.IsEnabled = !_saving && _bank.CanReset;
        SaveButton.IsEnabled = CancelButton.IsEnabled = !_saving && !_bank.IsBusy;
    }

    private void ResetClick(object sender, RoutedEventArgs e) => Reset(false);
    private void ResetAllClick(object sender, RoutedEventArgs e) => Reset(true);
    private async void Reset(bool all)
    {
        if (_saving || DataContext is not RegisterSettingsViewModel vm || !_bank.CanReset) return;
        _saving = true; vm.Error = ""; vm.OperationStatus = ""; RefreshState();
        try
        {
            await _bank.ResetAsync(all ? null : vm.Number);
            vm.OperationStatus = _bank.ResetStatus;
        }
        catch (Exception ex) { vm.Error = ex.Message; }
        finally { _saving = false; RefreshState(); }
    }

    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_saving || DataContext is not RegisterSettingsViewModel vm) return;
        _saving = true;
        RefreshState();
        vm.Error = "";
        try
        {
            await _bank.SaveAsync(vm.ToChange());
            _saving = false;
            DialogResult = true;
        }
        catch (Exception ex) { vm.Error = ex.Message; }
        finally { _saving = false; RefreshState(); }
    }
}
