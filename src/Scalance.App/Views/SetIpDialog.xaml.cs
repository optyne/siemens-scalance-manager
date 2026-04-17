using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Scalance.Protocols.Dcp;

namespace Scalance.App.Views;

public partial class SetIpDialog : Window
{
    private readonly SetIpDialogModel _model;

    public SetIpDialog(DcpIdentifyResponse target)
    {
        InitializeComponent();
        _model = new SetIpDialogModel
        {
            Header = $"在 {target.NameOfStation ?? "(未命名)"} 上設定 IP",
            TargetMac = target.SourceMac,
            IpText = target.IpAddress?.ToString() ?? "",
            MaskText = target.SubnetMask?.ToString() ?? "255.255.255.0",
            GatewayText = target.Gateway?.ToString() ?? "",
            SavePermanent = true,
        };
        DataContext = _model;
    }

    public string IpText => _model.IpText;
    public string MaskText => _model.MaskText;
    public string GatewayText => _model.GatewayText;
    public bool SavePermanent => _model.SavePermanent;

    public void SetSuggestedValues(string ip, string mask, string gateway)
    {
        _model.IpText = ip;
        _model.MaskText = mask;
        _model.GatewayText = gateway;
    }

    private void OnOk(object sender, RoutedEventArgs e)     { DialogResult = true;  Close(); }
    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}

public sealed partial class SetIpDialogModel : ObservableObject
{
    [ObservableProperty] private string header = "";
    [ObservableProperty] private string targetMac = "";
    [ObservableProperty] private string ipText = "";
    [ObservableProperty] private string maskText = "";
    [ObservableProperty] private string gatewayText = "";
    [ObservableProperty] private bool savePermanent = true;
}
