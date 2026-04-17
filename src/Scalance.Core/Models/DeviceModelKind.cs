namespace Scalance.Core.Models;

public enum DeviceModelKind
{
    Unknown = 0,
    S610,
    S615,
    Xc200,
    Xb200,
    Xf200Ba,
    Xp200,
    Xr300Wg
}

public enum DeviceFamily
{
    Unknown = 0,
    SecurityModule,
    SecurityRouter,
    ManagedSwitch
}

public static class DeviceModelExtensions
{
    public static DeviceFamily Family(this DeviceModelKind kind) => kind switch
    {
        DeviceModelKind.S610 => DeviceFamily.SecurityModule,
        DeviceModelKind.S615 => DeviceFamily.SecurityRouter,
        DeviceModelKind.Xc200
            or DeviceModelKind.Xb200
            or DeviceModelKind.Xf200Ba
            or DeviceModelKind.Xp200
            or DeviceModelKind.Xr300Wg => DeviceFamily.ManagedSwitch,
        _ => DeviceFamily.Unknown
    };
}
