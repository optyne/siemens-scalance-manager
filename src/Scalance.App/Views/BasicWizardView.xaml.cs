using System.Windows.Controls;
using Scalance.App.ViewModels;

namespace Scalance.App.Views;

public partial class BasicWizardView : UserControl
{
    public BasicWizardView() { InitializeComponent(); }

    private void NewPassword_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is BasicWizardViewModel vm && sender is PasswordBox pb)
            vm.NewPassword = pb.Password;
    }

    private void ConfirmPassword_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is BasicWizardViewModel vm && sender is PasswordBox pb)
            vm.ConfirmPassword = pb.Password;
    }
}
