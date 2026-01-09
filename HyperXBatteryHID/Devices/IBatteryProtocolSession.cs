using System.Threading;
using HidSharp;

namespace HyperXBatteryHID.Devices;

internal interface IBatteryProtocolSession
{
    BatterySnapshot CreateDisconnectedSnapshot(DeviceCandidate device);

    BatterySnapshot Query(HidDevice hidDevice, HidStream stream, DeviceCandidate device, CancellationToken ct);
}


