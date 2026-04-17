using Scalance.Core.Models;

namespace Scalance.Drivers;

/// <summary>
/// Driver for SCALANCE X-family managed switches (XB-200 / XC-200 / XF-200BA / XP-200 / XR-300WG).
/// Inherits all CLI behaviour from <see cref="ScalanceCliDriverBase"/>:
///   - SNMP status/port walk via MIB-II (SnmpDriverBase)
///   - VLAN read via Q-BRIDGE MIB (Dot1qVlanReader)
///   - VLAN write, interface config, NTP and backup via SSH CLI
/// The X-family doesn't support IPsec VPN so those calls fall through to the base-class
/// capability gate.
/// </summary>
public sealed class XSeriesSwitchDriver : ScalanceCliDriverBase
{
    private readonly DeviceModelKind _model;
    public XSeriesSwitchDriver(DeviceModelKind model) { _model = model; }
    public override DeviceModelKind Model => _model;
}
