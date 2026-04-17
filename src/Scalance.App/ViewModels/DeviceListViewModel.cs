using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Scalance.App.Services;
using Scalance.App.Views;
using Scalance.Core.Models;
using Scalance.Data;

namespace Scalance.App.ViewModels;

public sealed partial class DeviceListViewModel : ObservableObject
{
    private readonly DeviceRepository _repo;
    private readonly DeviceOperationsService _ops;
    private readonly IServiceProvider _services;
    private readonly DeviceSelection _selection;
    private readonly OperationLog _log;

    public ObservableCollection<DeviceRowViewModel> Items { get; } = new();

    [ObservableProperty] private DeviceRowViewModel? selected;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool isBusy;

    public DeviceListViewModel(DeviceRepository repo, DeviceOperationsService ops, IServiceProvider services, DeviceSelection selection, OperationLog log)
    {
        _repo = repo;
        _ops = ops;
        _services = services;
        _selection = selection;
        _log = log;
    }

    public async Task LoadAsync()
    {
        Items.Clear();
        foreach (var d in await _repo.ListAsync())
            Items.Add(new DeviceRowViewModel(d));
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var vm = _services.GetRequiredService<DeviceEditorViewModel>();
        vm.Load(new Device());
        var dialog = new DeviceEditorWindow { DataContext = vm, Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            await _repo.UpsertAsync(vm.ToDevice());
            await LoadAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task EditAsync()
    {
        if (Selected is null) return;
        var vm = _services.GetRequiredService<DeviceEditorViewModel>();
        await vm.LoadWithCredentialAsync(Selected.Model);
        var dialog = new DeviceEditorWindow { DataContext = vm, Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true)
        {
            await _repo.UpsertAsync(vm.ToDevice());
            await LoadAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        var r = MessageBox.Show($"確定要刪除設備「{Selected.Name}」嗎？", "確認",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        await _repo.DeleteAsync(Selected.Model.Id);
        await LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TestAsync()
    {
        if (Selected is null) return;
        IsBusy = true;
        StatusMessage = $"正在測試 {Selected.Name}...";
        try
        {
            var r = await _ops.TestAndFetchStatusAsync(Selected.Model);
            if (r.Success && r.Value is not null)
            {
                Selected.Model.LastSeenAt = DateTimeOffset.UtcNow;
                Selected.Model.LastKnownFirmware = r.Value.Firmware;
                await _repo.UpsertAsync(Selected.Model);
                StatusMessage = $"正常 — {r.Value.SystemName} / 運行時間 {r.Value.Uptime:d\\.hh\\:mm\\:ss} / {r.Value.Ports.Count} 個埠";
                _log.Info($"Device {Selected.Name}: {StatusMessage}");
                Selected.RefreshDisplay();
            }
            else
            {
                StatusMessage = $"失敗：{r.Message}";
                _log.Error($"Device {Selected.Name} test failed: {r.Message}");
            }
        }
        finally { IsBusy = false; }
    }

    private bool HasSelection() => Selected is not null;

    partial void OnSelectedChanged(DeviceRowViewModel? value)
    {
        _selection.Current = value?.Model;
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class DeviceRowViewModel : ObservableObject
{
    public Device Model { get; }

    public DeviceRowViewModel(Device model) { Model = model; }

    public string Name => Model.Name;
    public string Host => Model.Host;
    public string ModelName => Model.Model.ToString();
    public string LastSeen => Model.LastSeenAt?.LocalDateTime.ToString("g") ?? "—";
    public string Firmware => Model.LastKnownFirmware ?? "—";

    public void RefreshDisplay()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Host));
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(LastSeen));
        OnPropertyChanged(nameof(Firmware));
    }
}
