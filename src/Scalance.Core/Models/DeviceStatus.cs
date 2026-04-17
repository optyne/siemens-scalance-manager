namespace Scalance.Core.Models;

public sealed record DeviceStatus(
    string SystemName,
    string SystemDescription,
    string? Firmware,
    TimeSpan Uptime,
    IReadOnlyList<PortStatus> Ports);

public sealed record PortStatus(
    int Index,
    string Name,
    bool AdminUp,
    bool LinkUp,
    long SpeedBps,
    string? MacAddress);
