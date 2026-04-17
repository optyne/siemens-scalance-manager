using Scalance.Core.Models;

namespace Scalance.Drivers;

public sealed class S615Driver : ScalanceCliDriverBase
{
    public override DeviceModelKind Model => DeviceModelKind.S615;
}
