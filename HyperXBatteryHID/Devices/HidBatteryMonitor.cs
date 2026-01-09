using HidSharp;
using HyperXBatteryHID.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HyperXBatteryHID.Devices;

internal sealed class HidBatteryMonitor : IDisposable
{
    private readonly AppLogger _log;
    private readonly DeviceCandidate _device;
    private readonly IBatteryProtocol _protocol;
    private readonly IBatteryProtocolSession _session;
    private readonly int _queryIntervalMs;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _runner;
    private int _disposed;

    public event Action<BatterySnapshot>? SnapshotUpdated;

    public HidBatteryMonitor(
        AppLogger log,
        DeviceCandidate device,
        IBatteryProtocol protocol,
        ProtocolSessionOptions options,
        int queryIntervalMs)
    {
        _log = log;
        _device = device;
        _protocol = protocol;
        _session = protocol.CreateSession(options);
        _queryIntervalMs = Math.Max(250, queryIntervalMs);

        _runner = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
    }

    private void Emit(BatterySnapshot s)
    {
        try { SnapshotUpdated?.Invoke(s); }
        catch (Exception ex) { _log.Warn(ex, "SnapshotUpdated handler threw"); }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HidDevice? hidDevice = null;
            try
            {
                hidDevice = DeviceList.Local
                    .GetHidDevices(_device.VendorId, _device.ProductId)
                    .FirstOrDefault(d => string.Equals(d.DevicePath, _device.DevicePath, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "Device enumeration failed");
            }

            if (hidDevice is null || !_protocol.Matches(hidDevice))
            {
                Emit(_session.CreateDisconnectedSnapshot(_device));
                await SleepAsync(ct, _queryIntervalMs).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (!hidDevice.TryOpen(out var stream))
                {
                    Emit(_session.CreateDisconnectedSnapshot(_device));
                    await SleepAsync(ct, _queryIntervalMs).ConfigureAwait(false);
                    continue;
                }

                using (stream)
                {
                    stream.ReadTimeout = 50;   // small slice so we can respond to cancellation and implement our own deadlines
                    stream.WriteTimeout = 200; // avoid hanging forever on funky device state

                    while (!ct.IsCancellationRequested)
                    {
                        var snap = _session.Query(hidDevice, stream, _device, ct);
                        Emit(snap);
                        await SleepAsync(ct, _queryIntervalMs).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Common cases: device unplugged mid-read/write, HID stack hiccup.
                _log.Warn(ex, "HID monitor loop error (will retry)");
                Emit(_session.CreateDisconnectedSnapshot(_device));
                await SleepAsync(ct, _queryIntervalMs).ConfigureAwait(false);
            }
        }
    }

    private static async Task SleepAsync(CancellationToken ct, int ms)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _cts.Cancel(); } catch { /* ignore */ }

        // Best-effort cancellation. Avoid blocking UI thread during shutdown.
        _ = _runner.ContinueWith(
            t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    _log.Warn(t.Exception.GetBaseException(), "HID monitor background task faulted");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        _cts.Dispose();
    }
}


