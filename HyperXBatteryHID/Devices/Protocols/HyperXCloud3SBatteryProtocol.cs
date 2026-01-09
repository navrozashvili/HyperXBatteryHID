using HidSharp;
using System;
using System.Threading;

namespace HyperXBatteryHID.Devices.Protocols;

/// <summary>
/// HyperX Cloud III S Wireless (dongle) - experimental HID protocol.
/// Mirrors the behavior in hyperx_cloud3s_battery.py.
/// </summary>
internal sealed class HyperXCloud3SBatteryProtocol : IBatteryProtocol
{
    // Defaults from the Python script.
    public const int Vid = 0x03F0;
    public const int Pid = 0x06BE;

    public string Id => "hyperx.cloud3s";
    public string Name => "HyperX Cloud III S Wireless (dongle)";

    public bool Matches(HidDevice device)
        => device.VendorID == Vid && device.ProductID == Pid;

    public IBatteryProtocolSession CreateSession(ProtocolSessionOptions options)
        => new Session(options);

    private sealed class Session : IBatteryProtocolSession
    {
        // Battery query report
        private const byte ReportId = 0x0C;
        private const byte CmdBattery = 0x06;

        // Charging event report
        private const byte ChgReportId = 0x0D;
        private const byte ChgTag = 0x0A;

        private readonly ProtocolSessionOptions _options;
        private bool? _lastCharging;
        private DateTimeOffset? _lastChargingSeen;

        public Session(ProtocolSessionOptions options)
        {
            _options = options;
        }

        public BatterySnapshot CreateDisconnectedSnapshot(DeviceCandidate device)
            => new(
                Device: device,
                IsConnected: false,
                BatteryPercent: null,
                Charging: ChargingStatus.Unknown,
                DebugInfo: null,
                Timestamp: DateTimeOffset.Now
            );

        public BatterySnapshot Query(HidDevice hidDevice, HidStream stream, DeviceCandidate device, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var outLen = hidDevice.GetMaxOutputReportLength();
            if (outLen <= 0) outLen = 64;

            var req = new byte[outLen];
            // ReportID + fixed header + battery command
            // 0C 02 03 01 00 06 00 ...
            req[0] = ReportId;
            req[1] = 0x02;
            req[2] = 0x03;
            req[3] = 0x01;
            req[4] = 0x00;
            req[5] = CmdBattery;
            req[6] = 0x00;

            // "funky" devices can get stuck; keep write bounded.
            stream.WriteTimeout = Math.Clamp(_options.QueryTimeoutMs, 50, 2000);
            stream.Write(req, 0, req.Length);

            var inLen = hidDevice.GetMaxInputReportLength();
            if (inLen <= 0) inLen = 64;
            var buf = new byte[inLen];

            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Clamp(_options.QueryTimeoutMs, 50, 5000));
            int? pct = null;
            byte flags1 = 0;
            byte flags2 = 0;

            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                int read;
                try
                {
                    // ReadTimeout is already small (set by monitor); we loop to implement a larger deadline.
                    read = stream.Read(buf, 0, buf.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (read <= 0)
                    continue;

                // Some stacks return full buffers; some return shorter. Normalize.
                Span<byte> b = buf.AsSpan(0, read);

                var chg = TryParseCharging(b);
                if (chg.HasValue)
                {
                    _lastCharging = chg.Value;
                    _lastChargingSeen = DateTimeOffset.UtcNow;
                    continue; // keep reading until we also get battery
                }

                var bat = TryParseBattery(b);
                if (bat.HasValue)
                {
                    pct = bat.Value.Percent;
                    flags1 = bat.Value.Flags1;
                    flags2 = bat.Value.Flags2;
                    break;
                }
            }

            var charging = ChargingFromLastEvent();

            return new BatterySnapshot(
                Device: device,
                IsConnected: true,
                BatteryPercent: pct,
                Charging: charging,
                DebugInfo: pct.HasValue ? $"flags: {flags1:X2} {flags2:X2}" : null,
                Timestamp: DateTimeOffset.Now
            );
        }

        private ChargingStatus ChargingFromLastEvent()
        {
            if (_lastCharging is null || _lastChargingSeen is null)
                return ChargingStatus.Unknown;

            // Device emits charging state only on transitions; once we see "charging" we must
            // treat it as latched until we explicitly observe "not charging".
            if (_lastCharging.Value)
                return ChargingStatus.Charging;

            var ageMs = (int)Math.Max(0, (DateTimeOffset.UtcNow - _lastChargingSeen.Value).TotalMilliseconds);
            if (ageMs > _options.ChargingStaleMs)
                return ChargingStatus.Unknown;

            return _lastCharging.Value ? ChargingStatus.Charging : ChargingStatus.NotCharging;
        }

        private static (int Percent, byte Flags1, byte Flags2)? TryParseBattery(ReadOnlySpan<byte> b)
        {
            // Expect: 0C 02 03 01 00 06 <PCT> <FLAGS1> <FLAGS2> ...
            if (b.Length < 9) return null;
            if (b[0] != ReportId) return null;
            if (b[5] != CmdBattery) return null;
            var pct = (int)b[6];
            var flags1 = b[7];
            var flags2 = b[8];
            if (pct is < 0 or > 100)
                return null;
            return (pct, flags1, flags2);
        }

        private static bool? TryParseCharging(ReadOnlySpan<byte> b)
        {
            // Expected prefix:
            // 0D 02 03 00 0A <STATE> ...
            if (b.Length < 6) return null;
            if (b[0] != ChgReportId) return null;
            if (b[1] != 0x02 || b[2] != 0x03 || b[3] != 0x00) return null;
            if (b[4] != ChgTag) return null;
            var state = (int)b[5];
            return state switch
            {
                1 => true,
                0 => false,
                _ => null
            };
        }
    }
}


