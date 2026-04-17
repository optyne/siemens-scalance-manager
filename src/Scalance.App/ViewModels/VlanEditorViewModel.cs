using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;

namespace Scalance.App.ViewModels;

public sealed partial class VlanEditorViewModel : ObservableObject
{
    private readonly DeviceOperationsService _ops;
    private readonly DeviceSelection _selection;
    private readonly OperationLog _log;

    [ObservableProperty] private string? selectedDeviceName;
    [ObservableProperty] private bool featureSupported;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private VlanRow? selectedVlan;
    [ObservableProperty] private int newVlanId;
    [ObservableProperty] private string newVlanName = "";

    public ObservableCollection<VlanRow> Vlans { get; } = new();

    public VlanEditorViewModel(DeviceOperationsService ops, DeviceSelection selection, OperationLog log)
    {
        _ops = ops;
        _selection = selection;
        _log = log;
        _selection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceSelection.Current))
                RefreshForSelection();
        };
        RefreshForSelection();
    }

    private void RefreshForSelection()
    {
        var d = _selection.Current;
        SelectedDeviceName = d?.Name;
        FeatureSupported = d is not null && CapabilityMatrix.Supports(d.Model, DeviceCapability.VlanManagement);
        Vlans.Clear();
        SelectedVlan = null;
        StatusMessage = d is null ? "未選取設備。"
            : FeatureSupported ? $"就緒 — {d.Name}。請按「載入」。"
            : "此設備不支援 VLAN 管理功能。";
        NotifyCommands();
    }

    private bool CanUse() => _selection.Current is not null && FeatureSupported && !IsBusy;
    private bool CanEdit() => CanUse() && SelectedVlan is not null;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task LoadAsync()
    {
        var d = _selection.Current;
        if (d is null) return;
        IsBusy = true;
        StatusMessage = "正在載入 VLAN 表...";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.GetVlansAsync();
            Vlans.Clear();
            SelectedVlan = null;
            if (r.Success && r.Value is not null)
            {
                foreach (var v in r.Value.OrderBy(x => x.Id))
                    Vlans.Add(VlanRow.From(v));
                StatusMessage = $"已載入 {Vlans.Count} 個 VLAN。";
            }
            else
            {
                StatusMessage = $"載入失敗：{r.Message}";
            }
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private void AddVlan()
    {
        if (NewVlanId <= 0 || NewVlanId > 4094)
        {
            StatusMessage = "VLAN ID 必須在 1 到 4094 之間。";
            return;
        }
        if (Vlans.Any(v => v.Id == NewVlanId))
        {
            StatusMessage = $"VLAN {NewVlanId} 已存在。";
            return;
        }
        var row = new VlanRow { Id = NewVlanId, Name = string.IsNullOrWhiteSpace(NewVlanName) ? $"VLAN{NewVlanId:0000}" : NewVlanName };
        Vlans.Add(row);
        SelectedVlan = row;
        NewVlanId = 0;
        NewVlanName = "";
        StatusMessage = $"已暫存 VLAN {row.Id}。請按「套用」推送至設備。";
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RemoveVlan()
    {
        if (SelectedVlan is null) return;
        Vlans.Remove(SelectedVlan);
        SelectedVlan = null;
        StatusMessage = "已本地移除 VLAN。請按「套用」推送至設備。";
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task ApplyAsync()
    {
        var d = _selection.Current;
        if (d is null) return;
        IsBusy = true; StatusMessage = "正在套用 VLAN 表...";
        try
        {
            var models = Vlans.Select(r => r.ToModel()).ToList();
            await using var driver = await _ops.OpenAsync(d);
            var r = await driver.SetVlansAsync(models);
            var isDryRun = driver is Scalance.Drivers.ScalanceCliDriverBase cli && cli.DryRun;
            DryRunPreview.LogIfDryRun(driver, _log, "VLAN");
            StatusMessage = !r.Success
                ? $"失敗：{r.Message}"
                : isDryRun
                    ? $"⚠ DryRun：已規劃 {models.Count} 個 VLAN 的指令，未實際送出。請取消勾選 DryRun 後重新套用。"
                    : $"已推送 {models.Count} 個 VLAN。";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnSelectedVlanChanged(VlanRow? value) => NotifyCommands();
    partial void OnStatusMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _log.Info($"VLAN: {value}");
    }

    private void NotifyCommands()
    {
        LoadCommand.NotifyCanExecuteChanged();
        AddVlanCommand.NotifyCanExecuteChanged();
        RemoveVlanCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class VlanRow : ObservableObject
{
    [ObservableProperty] private int id;
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string taggedPorts = "";
    [ObservableProperty] private string untaggedPorts = "";

    public static VlanRow From(Vlan v) => new()
    {
        Id = v.Id,
        Name = v.Name,
        TaggedPorts = string.Join(",", v.Ports.Where(p => p.Mode == VlanMemberMode.Tagged).Select(p => p.PortIndex)),
        UntaggedPorts = string.Join(",", v.Ports.Where(p => p.Mode == VlanMemberMode.Untagged).Select(p => p.PortIndex)),
    };

    public Vlan ToModel()
    {
        var vlan = new Vlan { Id = Id, Name = Name };
        foreach (var idx in ParsePortList(TaggedPorts))
            vlan.Ports.Add(new VlanPortMembership(idx, VlanMemberMode.Tagged));
        foreach (var idx in ParsePortList(UntaggedPorts))
            vlan.Ports.Add(new VlanPortMembership(idx, VlanMemberMode.Untagged));
        return vlan;
    }

    private static IEnumerable<int> ParsePortList(string? csv) =>
        (csv ?? "")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var n) ? n : -1)
            .Where(n => n > 0);
}
