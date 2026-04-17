using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scalance.App.Services;
using Scalance.Core.Models;
using Scalance.Data;

namespace Scalance.App.ViewModels;

public sealed partial class TopologyViewModel : ObservableObject
{
    private readonly DeviceRepository _repo;
    private readonly OperationLog _log;

    public ObservableCollection<TopologyNodeVm> Nodes { get; } = new();
    public ObservableCollection<TopologyLinkVm> Links { get; } = new();

    [ObservableProperty] private string? statusMessage;

    public const double NodeWidth = 180;
    public const double NodeHeight = 80;

    public TopologyViewModel(DeviceRepository repo, OperationLog log)
    {
        _repo = repo;
        _log = log;
    }

    /// <summary>固定 GUID 用於本機節點的位置儲存。</summary>
    public static readonly Guid LocalMachineId = Guid.Empty;

    [RelayCommand]
    private async Task LoadAsync()
    {
        Nodes.Clear();
        Links.Clear();

        var devices = await _repo.ListAsync();
        var layout = TopologyLayout.Load();

        int index = 0;

        // 加入本機節點
        var localNode = CreateLocalMachineNode(layout, ref index);
        if (localNode is not null)
            Nodes.Add(localNode);

        foreach (var d in devices)
        {
            double x, y;
            if (layout.Positions.TryGetValue(d.Id, out var pos))
            {
                x = pos.X;
                y = pos.Y;
            }
            else
            {
                int col = index % 4;
                int row = index / 4;
                x = 50 + col * (NodeWidth + 120);
                y = 50 + row * (NodeHeight + 100);
            }

            Nodes.Add(new TopologyNodeVm(d, x, y));
            index++;
        }

        BuildLinks();
        StatusMessage = $"已載入 {Nodes.Count} 台設備（含本機），{Links.Count} 條連線。";
        _log.Info(StatusMessage);
    }

    private TopologyNodeVm? CreateLocalMachineNode(TopologyLayout layout, ref int index)
    {
        string? localIp = GetLocalIpAddress();
        if (localIp is null) localIp = "127.0.0.1";

        double x, y;
        if (layout.Positions.TryGetValue(LocalMachineId, out var pos))
        {
            x = pos.X;
            y = pos.Y;
        }
        else
        {
            int col = index % 4;
            int row = index / 4;
            x = 50 + col * (NodeWidth + 120);
            y = 50 + row * (NodeHeight + 100);
        }
        index++;

        string hostName;
        try { hostName = Environment.MachineName; }
        catch { hostName = "本機"; }

        return new TopologyNodeVm(
            deviceId: LocalMachineId,
            name: $"本機 ({hostName})",
            host: localIp,
            modelName: "本機 (Windows)",
            isOnline: true,
            isLocalMachine: true,
            x: x,
            y: y);
    }

    private static string? GetLocalIpAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.Wireless80211)) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bytes = ua.Address.GetAddressBytes();
                    // 排除 APIPA (169.254.x.x)
                    if (bytes[0] == 169 && bytes[1] == 254) continue;
                    return ua.Address.ToString();
                }
            }
        }
        catch { /* 靜默失敗 */ }
        return null;
    }

    [RelayCommand]
    private void AutoArrange()
    {
        int index = 0;
        foreach (var node in Nodes)
        {
            int col = index % 4;
            int row = index / 4;
            node.X = 50 + col * (NodeWidth + 120);
            node.Y = 50 + row * (NodeHeight + 100);
            index++;
        }
        BuildLinks();
        SaveLayout();
        StatusMessage = "已自動排列。";
    }

    [RelayCommand]
    private void SaveLayout()
    {
        var layout = new TopologyLayout();
        foreach (var n in Nodes)
            layout.Positions[n.DeviceId] = new TopologyLayout.NodePosition { X = n.X, Y = n.Y };
        layout.Save();
    }

    public void OnNodeMoved()
    {
        SaveLayout();
    }

    private void BuildLinks()
    {
        Links.Clear();
        var parsed = new List<(TopologyNodeVm Node, byte[] Subnet)>();
        foreach (var n in Nodes)
        {
            if (IPAddress.TryParse(n.Host, out var ip))
            {
                var bytes = ip.GetAddressBytes();
                if (bytes.Length == 4)
                    parsed.Add((n, new[] { bytes[0], bytes[1], bytes[2] }));
            }
        }

        for (int i = 0; i < parsed.Count; i++)
        {
            for (int j = i + 1; j < parsed.Count; j++)
            {
                if (parsed[i].Subnet[0] == parsed[j].Subnet[0] &&
                    parsed[i].Subnet[1] == parsed[j].Subnet[1] &&
                    parsed[i].Subnet[2] == parsed[j].Subnet[2])
                {
                    string label = "vlan1 \u2194 vlan1";
                    Links.Add(new TopologyLinkVm(parsed[i].Node, parsed[j].Node, label));
                }
            }
        }
    }
}

public sealed partial class TopologyNodeVm : ObservableObject
{
    public Guid DeviceId { get; }
    public string Name { get; }
    public string Host { get; }
    public string ModelName { get; }
    public bool IsOnline { get; }
    public bool IsLocalMachine { get; }

    [ObservableProperty] private double x;
    [ObservableProperty] private double y;

    public double CenterX => X + TopologyViewModel.NodeWidth / 2;
    public double CenterY => Y + TopologyViewModel.NodeHeight / 2;

    public TopologyNodeVm(Device device, double x, double y)
    {
        DeviceId = device.Id;
        Name = device.Name;
        Host = device.Host;
        ModelName = device.Model.ToString();
        IsOnline = device.LastSeenAt.HasValue &&
                   (DateTimeOffset.UtcNow - device.LastSeenAt.Value).TotalHours < 24;
        IsLocalMachine = false;
        this.x = x;
        this.y = y;
    }

    public TopologyNodeVm(Guid deviceId, string name, string host, string modelName,
                           bool isOnline, bool isLocalMachine, double x, double y)
    {
        DeviceId = deviceId;
        Name = name;
        Host = host;
        ModelName = modelName;
        IsOnline = isOnline;
        IsLocalMachine = isLocalMachine;
        this.x = x;
        this.y = y;
    }

    partial void OnXChanged(double value) => OnPropertyChanged(nameof(CenterX));
    partial void OnYChanged(double value) => OnPropertyChanged(nameof(CenterY));
}

public sealed class TopologyLinkVm
{
    public TopologyNodeVm Source { get; }
    public TopologyNodeVm Target { get; }

    /// <summary>連線標籤，例如 "vlan1 ↔ vlan1" 或 "P1 ↔ P2"。</summary>
    public string Label { get; set; }

    /// <summary>使用者備註（未來可編輯）。</summary>
    public string? Description { get; set; }

    public TopologyLinkVm(TopologyNodeVm source, TopologyNodeVm target, string label = "")
    {
        Source = source;
        Target = target;
        Label = label;
    }
}
