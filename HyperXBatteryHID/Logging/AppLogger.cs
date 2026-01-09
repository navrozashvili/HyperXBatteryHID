using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace HyperXBatteryHID.Logging;

internal sealed class AppLogger : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter _writer;

    public string LogFilePath { get; }

    public AppLogger(string logFilePath)
    {
        LogFilePath = logFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
        _writer = new StreamWriter(new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Warn(Exception ex, string message)
    {
        Write("WARN", $"{message}{Environment.NewLine}{ex}");
    }

    public void Error(Exception ex, string message)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");
    }

    private void Write(string level, string message)
    {
        var ts = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        lock (_lock)
        {
            _writer.WriteLine($"{ts} [{level}] {message}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}


