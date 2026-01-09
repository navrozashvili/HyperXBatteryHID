using HidSharp;
using HyperXBatteryHID.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HyperXBatteryHID.Devices;

internal sealed class HidProbeService : IDisposable
{
    private readonly AppLogger _log;
    private readonly IReadOnlyList<IBatteryProtocol> _protocols;
    private readonly int _intervalMs;
    private readonly object _lock = new();

    private readonly SemaphoreSlim _forceProbe = new(0, int.MaxValue);
    private CancellationTokenSource? _cts;
    private Task? _runner;
    private int _disposed;
    private List<DeviceCandidate> _last = [];

    public event Action<IReadOnlyList<DeviceCandidate>>? DevicesChanged;

    public HidProbeService(AppLogger log, IEnumerable<IBatteryProtocol> protocols, int intervalMs)
    {
        _log = log;
        _protocols = protocols.ToList();
        _intervalMs = Math.Max(250, intervalMs);
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_runner is not null) return;

            _cts = new CancellationTokenSource();
            _runner = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public void ForceProbe()
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try { _forceProbe.Release(); }
        catch (ObjectDisposedException) { /* ignore */ }
    }

    private void ProbeOnceSafe()
    {
        try { ProbeOnce(); }
        catch (Exception ex) { _log.Warn(ex, "HID probe failed"); }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Initial immediate probe so the UI populates quickly.
        ProbeOnceSafe();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var delayTask = Task.Delay(_intervalMs, ct);
                var forceTask = _forceProbe.WaitAsync(ct);

                await Task.WhenAny(delayTask, forceTask).ConfigureAwait(false);

                // Coalesce multiple ForceProbe() calls into a single enumeration pass.
                while (_forceProbe.Wait(0)) { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "HID probe scheduling loop error (will retry)");
            }

            ProbeOnceSafe();
        }
    }

    private void ProbeOnce()
    {
        var list = DeviceList.Local;
        var devices = list.GetHidDevices();

        var found = new List<DeviceCandidate>();

        foreach (var d in devices)
        {
            foreach (var p in _protocols)
            {
                if (!p.Matches(d))
                    continue;

                var cand = new DeviceCandidate(
                    ProtocolId: p.Id,
                    ProtocolName: p.Name,
                    DevicePath: d.DevicePath,
                    VendorId: d.VendorID,
                    ProductId: d.ProductID,
                    Manufacturer: SafeGet(() => d.GetManufacturer()),
                    Product: SafeGet(() => d.GetProductName()),
                    SerialNumber: SafeGet(() => d.GetSerialNumber())
                );

                found.Add(cand);
            }
        }

        found = found
            .GroupBy(c => (c.ProtocolId, c.DevicePath), StringTupleComparer.Instance)
            .Select(g => g.First())
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<DeviceCandidate> last;
        lock (_lock)
        {
            last = _last;
            if (SequenceEqual(last, found))
                return;
            _last = found;
        }

        DevicesChanged?.Invoke(found);
    }

    private static bool SequenceEqual(List<DeviceCandidate> a, List<DeviceCandidate> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var aa = a[i];
            var bb = b[i];
            if (!string.Equals(aa.ProtocolId, bb.ProtocolId, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(aa.DevicePath, bb.DevicePath, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static string? SafeGet(Func<string> f)
    {
        try { return f(); }
        catch { return null; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        CancellationTokenSource? cts;
        Task? runner;

        lock (_lock)
        {
            cts = _cts;
            runner = _runner;
            _cts = null;
            _runner = null;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch { /* ignore */ }
            cts.Dispose();
        }

        if (runner is not null)
        {
            _ = runner.ContinueWith(
                t =>
                {
                    if (t.IsFaulted && t.Exception is not null)
                    {
                        _log.Warn(t.Exception.GetBaseException(), "HID probe background task faulted");
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }

        _forceProbe.Dispose();
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string, string)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals((string, string) x, (string, string) y)
            => string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2)
            );
    }
}


