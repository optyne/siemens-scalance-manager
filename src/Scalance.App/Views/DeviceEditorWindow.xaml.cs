using System.Windows;
using Scalance.App.ViewModels;

namespace Scalance.App.Views;

public partial class DeviceEditorWindow : Window
{
    public DeviceEditorWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DeviceEditorViewModel vm && !string.IsNullOrEmpty(vm.SshPassword))
                SshPasswordBox.Password = vm.SshPassword;
        };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeviceEditorViewModel vm)
        {
            if (!string.IsNullOrEmpty(SshPasswordBox.Password))
                vm.SshPassword = SshPasswordBox.Password;
            await vm.SaveCredentialIfProvidedAsync();
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
