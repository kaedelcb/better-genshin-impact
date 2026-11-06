using System.Windows;
using System.Windows.Controls;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class RegisterStripView : UserControl
{
    public RegisterStripView() => InitializeComponent();

    private void CellClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RegisterBankViewModel bank || sender is not Button { DataContext: RegisterCellViewModel cell }
            || !cell.Available) return;
        new RegisterSettingsWindow(bank, cell.Entry, this) { Owner = Window.GetWindow(this) }.ShowDialog();
        e.Handled = true;
    }
}
