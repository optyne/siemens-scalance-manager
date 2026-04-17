namespace Scalance.Core.Models;

/// <summary>
/// Device restart modes per PH_SCALANCE-S615-CLI_76 sec 5.3.1 p. 130-131:
///   restart              — reboot with current configuration
///   restart memory       — reset to factory settings but keep protected
///                          presets (IP/mask/gateway, DHCP, hostname, user
///                          accounts, mode, login text, PROFINET name),
///                          then reboot
///   restart factory      — full factory reset (including protected presets)
///                          then reboot
/// </summary>
public enum RestartMode
{
    /// <summary>Reboot with the current saved configuration.</summary>
    Current,
    /// <summary>Factory-reset non-protected settings (keeps IP/user/etc.) then reboot.</summary>
    Memory,
    /// <summary>Full factory reset including IP/user/etc. then reboot.</summary>
    Factory,
}
