using CommunityToolkit.Mvvm.ComponentModel;
using Scalance.Core.Models;

namespace Scalance.App.Services;

public sealed partial class DeviceSelection : ObservableObject
{
    [ObservableProperty] private Device? current;
}
