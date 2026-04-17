namespace Scalance.Core.Models;

public sealed class Vlan
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<VlanPortMembership> Ports { get; set; } = new();
}

public sealed record VlanPortMembership(
    int PortIndex,
    VlanMemberMode Mode);

public enum VlanMemberMode
{
    Excluded,
    Tagged,
    Untagged
}
