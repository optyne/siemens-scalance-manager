using CommunityToolkit.Mvvm.ComponentModel;
using Scalance.App.Services;

namespace Scalance.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeviceOperationsService _ops;

    public DeviceListViewModel Devices { get; }
    public ConnectionStatusViewModel ConnectionStatus { get; }
    public NtpEditorViewModel Ntp { get; }
    public VlanEditorViewModel Vlan { get; }
    public VpnEditorViewModel Vpn { get; }
    public SubnetEditorViewModel Subnet { get; }
    public FirewallEditorViewModel Firewall { get; }
    public TopologyViewModel Topology { get; }
    public DiscoveryViewModel Discovery { get; }
    public BasicWizardViewModel BasicWizard { get; }
    public BulkOpsViewModel BulkOps { get; }
    public SyslogEditorViewModel Syslog { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public BackupViewModel Backup { get; }
    public DeviceSelection Selection { get; }
    public OperationLog Log { get; }

    /// <summary>
    /// Mirror of <see cref="DeviceOperationsService.DryRun"/>. True means
    /// inferred-syntax CLI writes are planned but not sent to the device.
    /// </summary>
    [ObservableProperty] private bool dryRun;

    public MainViewModel(
        DeviceListViewModel devices,
        ConnectionStatusViewModel connectionStatus,
        NtpEditorViewModel ntp,
        VlanEditorViewModel vlan,
        VpnEditorViewModel vpn,
        SubnetEditorViewModel subnet,
        FirewallEditorViewModel firewall,
        TopologyViewModel topology,
        DiscoveryViewModel discovery,
        BasicWizardViewModel basicWizard,
        BulkOpsViewModel bulkOps,
        SyslogEditorViewModel syslog,
        DiagnosticsViewModel diagnostics,
        BackupViewModel backup,
        DeviceSelection selection,
        DeviceOperationsService ops,
        OperationLog log)
    {
        _ops = ops;
        Devices = devices;
        ConnectionStatus = connectionStatus;
        Ntp = ntp;
        Vlan = vlan;
        Vpn = vpn;
        Subnet = subnet;
        Firewall = firewall;
        Topology = topology;
        Discovery = discovery;
        BasicWizard = basicWizard;
        BulkOps = bulkOps;
        Syslog = syslog;
        Diagnostics = diagnostics;
        Backup = backup;
        Selection = selection;
        Log = log;
        dryRun = ops.DryRun;
        _ = devices.LoadAsync();
    }

    partial void OnDryRunChanged(bool value)
    {
        _ops.DryRun = value;
        Log.Warn(value
            ? "DryRun 已啟用 — 推定語法的寫入（VLAN / Subnet / VPN）只會規劃不會送出。"
            : "DryRun 已停用 — 寫入會送到設備。未驗證的 CLI 路徑請謹慎使用。");
    }
}
