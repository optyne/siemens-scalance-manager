using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Capabilities;
using Scalance.Core.Models;

namespace Scalance.App.ViewModels;

public sealed partial class FirewallEditorViewModel : ObservableObject
{
    private readonly DeviceOperationsService _ops;
    private readonly DeviceSelection _selection;
    private readonly OperationLog _log;

    [ObservableProperty] private string? selectedDeviceName;
    [ObservableProperty] private bool featureSupported;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private FirewallRuleRow? selectedRule;

    public ObservableCollection<FirewallRuleRow> Rules { get; } = new();
    public ObservableCollection<PredefinedServiceRow> PredefinedServices { get; } = new();

    public FirewallEditorViewModel(DeviceOperationsService ops, DeviceSelection selection, OperationLog log)
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
        FeatureSupported = d is not null && CapabilityMatrix.Supports(d.Model, DeviceCapability.Firewall);
        Rules.Clear();
        PredefinedServices.Clear();
        SelectedRule = null;
        StatusMessage = d is null ? "未選取設備。"
            : FeatureSupported ? $"就緒 — {d.Name}。請按「載入」。"
            : $"{d.Model} 不支援防火牆功能。";
        NotifyCommands();
    }

    private bool CanUse() => _selection.Current is not null && FeatureSupported && !IsBusy;
    private bool CanRemove() => CanUse() && SelectedRule is not null;

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task LoadAsync()
    {
        var d = _selection.Current;
        if (d is null) return;
        IsBusy = true;
        StatusMessage = "正在載入防火牆規則…";
        try
        {
            await using var driver = await _ops.OpenAsync(d);
            var rulesResult = await driver.ReadFirewallRulesAsync();
            Rules.Clear();
            SelectedRule = null;
            if (rulesResult.Success && rulesResult.Value is not null)
                foreach (var r in rulesResult.Value.OrderBy(x => x.Index))
                    Rules.Add(FirewallRuleRow.FromModel(r));

            var predefResult = await driver.ReadPredefinedRulesAsync();
            PredefinedServices.Clear();
            if (predefResult.Success && predefResult.Value is not null)
                foreach (var s in predefResult.Value.OrderBy(x => x.ServiceName))
                    PredefinedServices.Add(PredefinedServiceRow.FromModel(s));

            StatusMessage = rulesResult.Success
                ? $"已載入 {Rules.Count} 條自訂規則、{PredefinedServices.Count} 項預設服務。"
                : $"載入失敗：{rulesResult.Message}";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private void AddRule()
    {
        var nextIndex = Rules.Count > 0 ? Rules.Max(r => r.Index) + 1 : 1;
        var row = new FirewallRuleRow
        {
            Index = nextIndex,
            Enabled = true,
            Action = FirewallAction.Accept,
            FromInterface = "vlan1",
            ToInterface = "vlan2",
            SourceCidr = "",
            DestinationCidr = "",
            Service = "All",
            LogEnabled = false
        };
        Rules.Add(row);
        SelectedRule = row;
        StatusMessage = $"已新增規則 #{row.Index}，請編輯後按「套用」推送至設備。";
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveRule()
    {
        if (SelectedRule is null) return;
        Rules.Remove(SelectedRule);
        SelectedRule = null;
        StatusMessage = "已移除選取規則。請按「套用」推送至設備。";
    }

    [RelayCommand(CanExecute = nameof(CanUse))]
    private async Task ApplyAsync()
    {
        var d = _selection.Current;
        if (d is null) return;
        IsBusy = true;
        StatusMessage = "正在套用防火牆規則…";
        try
        {
            await using var driver = await _ops.OpenAsync(d);

            foreach (var row in Rules)
            {
                var r = await driver.WriteFirewallRuleAsync(row.ToModel());
                if (!r.Success) { StatusMessage = $"寫入規則 #{row.Index} 失敗：{r.Message}"; return; }
            }

            foreach (var svc in PredefinedServices)
            {
                var r = await driver.WritePredefinedRuleAsync(svc.ToModel());
                if (!r.Success) { StatusMessage = $"寫入預設服務「{svc.ServiceName}」失敗：{r.Message}"; return; }
            }

            StatusMessage = $"已套用 {Rules.Count} 條自訂規則、{PredefinedServices.Count} 項預設服務。";
        }
        catch (Exception ex) { StatusMessage = $"錯誤：{ex.Message}"; }
        finally { IsBusy = false; }
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnSelectedRuleChanged(FirewallRuleRow? value) => NotifyCommands();
    partial void OnStatusMessageChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _log.Info($"防火牆: {value}");
    }

    private void NotifyCommands()
    {
        LoadCommand.NotifyCanExecuteChanged();
        AddRuleCommand.NotifyCanExecuteChanged();
        RemoveRuleCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class FirewallRuleRow : ObservableObject
{
    [ObservableProperty] private int index;
    [ObservableProperty] private bool enabled = true;
    [ObservableProperty] private FirewallAction action = FirewallAction.Accept;
    [ObservableProperty] private string fromInterface = "vlan1";
    [ObservableProperty] private string toInterface = "vlan2";
    [ObservableProperty] private string sourceCidr = "";
    [ObservableProperty] private string destinationCidr = "";
    [ObservableProperty] private string service = "All";
    [ObservableProperty] private bool logEnabled;

    public static FirewallRuleRow FromModel(FirewallRule r) => new()
    {
        Index = r.Index,
        Enabled = r.Enabled,
        Action = r.Action,
        FromInterface = r.From,
        ToInterface = r.To,
        SourceCidr = r.SourceCidr,
        DestinationCidr = r.DestinationCidr,
        Service = r.Service,
        LogEnabled = r.Log
    };

    public FirewallRule ToModel() => new()
    {
        Index = Index,
        Enabled = Enabled,
        Action = Action,
        From = FromInterface,
        To = ToInterface,
        SourceCidr = SourceCidr,
        DestinationCidr = DestinationCidr,
        Service = Service,
        Log = LogEnabled
    };
}

public sealed partial class PredefinedServiceRow : ObservableObject
{
    [ObservableProperty] private string serviceName = "";
    [ObservableProperty] private bool localAccess;
    [ObservableProperty] private bool externalAccess;

    public static PredefinedServiceRow FromModel(PredefinedFirewallService s) => new()
    {
        ServiceName = s.ServiceName,
        LocalAccess = s.LocalAccess,
        ExternalAccess = s.ExternalAccess
    };

    public PredefinedFirewallService ToModel() => new()
    {
        ServiceName = ServiceName,
        LocalAccess = LocalAccess,
        ExternalAccess = ExternalAccess
    };
}
