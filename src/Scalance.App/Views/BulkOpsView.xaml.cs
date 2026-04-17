using System.Windows.Controls;
using Scalance.App.ViewModels;

namespace Scalance.App.Views;

public partial class BulkOpsView : UserControl
{
    public BulkOpsView() { InitializeComponent(); }

    private void NewPassword_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is BulkOpsViewModel vm && sender is PasswordBox pb)
            vm.NewPassword = pb.Password;
    }

    private void ConfirmPassword_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is BulkOpsViewModel vm && sender is PasswordBox pb)
            vm.ConfirmPassword = pb.Password;
    }
}
