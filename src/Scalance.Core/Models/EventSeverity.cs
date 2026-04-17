namespace Scalance.Core.Models;

/// <summary>
/// Destination sink for event messages on S615, as accepted by the
/// <c>severity</c> command in EVENTS configuration mode
/// (PH_SCALANCE-S615-CLI_76 sec 13.1.10.11 pp. 820-821).
/// </summary>
public enum EventSink
{
    Mail,
    Log,
    Syslog,
}

/// <summary>
/// Threshold values accepted by the <c>severity</c> command (manual p. 821):
///   info     — log/send all levels
///   warning  — log/send warning + critical
///   critical — log/send critical only
/// </summary>
public enum EventSeverity
{
    Info,
    Warning,
    Critical,
}
