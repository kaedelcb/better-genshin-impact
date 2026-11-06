using System.Windows;

namespace MultiplayerHoeingAssistant.Views;

internal static class RegisterResetConfirmation
{
    public static bool Show(Window? owner) => MessageBox.Show(owner,
        "确定将 R1–R10 全部归零吗？名称和自动重置规则会保留，尚未保存的设置不会应用。",
        "全部寄存器归零", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
