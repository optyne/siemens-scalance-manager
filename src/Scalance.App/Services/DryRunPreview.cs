using Scalance.Core.Abstractions;
using Scalance.Drivers;

namespace Scalance.App.Services;

/// <summary>
/// Helper that surfaces the planned-but-unsent CLI batch after a Set* call on
/// a <see cref="ScalanceCliDriverBase"/>. Call right after a driver write so
/// the operator sees, in the operation log, what WOULD have been sent when
/// <see cref="ScalanceCliDriverBase.DryRun"/> is true.
/// </summary>
public static class DryRunPreview
{
    public static void LogIfDryRun(IDeviceDriver driver, OperationLog log, string feature)
    {
        if (driver is not ScalanceCliDriverBase cli) return;
        if (!cli.DryRun) return;
        var cmds = cli.LastPlannedCommands;
        if (cmds.Count == 0) return;
        log.Warn($"{feature}: DryRun — {cmds.Count} command(s) planned, NOT sent.");
        foreach (var c in cmds)
            log.Info($"  cli> {c}");
    }
}
