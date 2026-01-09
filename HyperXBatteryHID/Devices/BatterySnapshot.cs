using System;

namespace HyperXBatteryHID.Devices;

internal sealed record BatterySnapshot(
    DeviceCandidate? Device,
    bool IsConnected,
    int? BatteryPercent,
    ChargingStatus Charging,
    string? DebugInfo,
    DateTimeOffset Timestamp
);


