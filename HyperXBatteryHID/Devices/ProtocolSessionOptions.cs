namespace HyperXBatteryHID.Devices;

internal sealed record ProtocolSessionOptions(
    int QueryTimeoutMs,
    int ChargingStaleMs
);


