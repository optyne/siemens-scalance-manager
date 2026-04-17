using Scalance.Core.Abstractions;
using Scalance.Core.Models;

namespace Scalance.Drivers;

public sealed class DeviceDriverFactory : IDeviceDriverFactory
{
    public IDeviceDriver Create(DeviceModelKind model) => model switch
    {
        DeviceModelKind.S610 => new S610Driver(),
        DeviceModelKind.S615 => new S615Driver(),
        DeviceModelKind.Xc200
            or DeviceModelKind.Xb200
            or DeviceModelKind.Xf200Ba
            or DeviceModelKind.Xp200
            or DeviceModelKind.Xr300Wg => new XSeriesSwitchDriver(model),
        _ => throw new NotSupportedException($"No driver for {model}.")
    };
}
