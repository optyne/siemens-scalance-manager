using Lextm.SharpSnmpLib;
using Scalance.Core.Models;
using Scalance.Protocols.Snmp;

namespace Scalance.Drivers;

internal static class Dot1qVlanReader
{
    public static async Task<IReadOnlyList<Vlan>> ReadAsync(SnmpClient snmp, CancellationToken ct)
    {
        var names = await snmp.WalkAsync(StandardOids.Dot1qVlanStaticName, ct);
        var egress = await snmp.WalkAsync(StandardOids.Dot1qVlanStaticEgressPorts, ct);
        var untagged = await snmp.WalkAsync(StandardOids.Dot1qVlanStaticUntaggedPorts, ct);

        var egressMap = ToVlanMap(egress, StandardOids.Dot1qVlanStaticEgressPorts);
        var untaggedMap = ToVlanMap(untagged, StandardOids.Dot1qVlanStaticUntaggedPorts);

        var result = new List<Vlan>();
        foreach (var v in names)
        {
            var id = ExtractVlanIdFromOid(v.Id.ToString(), StandardOids.Dot1qVlanStaticName);
            if (id is null) continue;

            var vlan = new Vlan
            {
                Id = id.Value,
                Name = v.Data.ToString()
            };

            var egressBits = egressMap.TryGetValue(id.Value, out var e) ? BitmapToPorts(e) : new HashSet<int>();
            var untaggedBits = untaggedMap.TryGetValue(id.Value, out var u) ? BitmapToPorts(u) : new HashSet<int>();

            foreach (var port in egressBits)
            {
                var mode = untaggedBits.Contains(port) ? VlanMemberMode.Untagged : VlanMemberMode.Tagged;
                vlan.Ports.Add(new VlanPortMembership(port, mode));
            }
            result.Add(vlan);
        }
        return result;
    }

    private static Dictionary<int, byte[]> ToVlanMap(IReadOnlyList<Variable> vars, string baseOid)
    {
        var map = new Dictionary<int, byte[]>();
        foreach (var v in vars)
        {
            var id = ExtractVlanIdFromOid(v.Id.ToString(), baseOid);
            if (id is null) continue;
            if (v.Data is OctetString os)
                map[id.Value] = os.GetRaw();
        }
        return map;
    }

    private static int? ExtractVlanIdFromOid(string oid, string baseOid)
    {
        if (!oid.StartsWith(baseOid)) return null;
        var rest = oid.Substring(baseOid.Length).Trim('.');
        return int.TryParse(rest, out var id) ? id : null;
    }

    private static HashSet<int> BitmapToPorts(byte[] bitmap)
    {
        var ports = new HashSet<int>();
        for (int i = 0; i < bitmap.Length; i++)
        {
            var b = bitmap[i];
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (0x80 >> bit)) != 0)
                    ports.Add(i * 8 + bit + 1);
            }
        }
        return ports;
    }
}
