using HidSharp;

namespace HyperXBatteryHID.Devices;

internal interface IBatteryProtocol
{
    string Id { get; }
    string Name { get; }

    bool Matches(HidDevice device);

    IBatteryProtocolSession CreateSession(ProtocolSessionOptions options);
}


