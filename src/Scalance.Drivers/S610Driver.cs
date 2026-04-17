using Scalance.Core.Models;

namespace Scalance.Drivers;

public sealed class S610Driver : SnmpDriverBase
{
    public override DeviceModelKind Model => DeviceModelKind.S610;
}
