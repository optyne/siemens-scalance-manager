namespace Scalance.Core.Models;

/// <summary>
/// Which SNMP versions the agent will answer. Maps to the S615
/// <c>snmp agent version {v3only | all}</c> command (PH_SCALANCE-S615-CLI_76
/// sec 9.8.2.5 pp. 441-442). Default on the device is <c>All</c>; set
/// <see cref="V3Only"/> to force authenticated SNMPv3 traffic only.
/// </summary>
public enum SnmpAgentVersionPolicy
{
    All,
    V3Only,
}

/// <summary>
/// Aggregate of the three SNMP agent knobs exposed by the system
/// (enabled / version policy / listen port). Driver methods take these
/// one-at-a-time; the model lets the UI present them together.
/// </summary>
public sealed class SnmpAgentConfig
{
    public bool Enabled { get; set; } = true;
    public SnmpAgentVersionPolicy VersionPolicy { get; set; } = SnmpAgentVersionPolicy.All;
    /// <summary>Listen port; 161 is the device default. Range 1024-65535 per manual.</summary>
    public int Port { get; set; } = 161;
}
