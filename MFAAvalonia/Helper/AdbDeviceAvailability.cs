using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace MFAAvalonia.Helper;

/// <summary>
/// 轻量 ADB 在线探测：MaaToolkit 会把已安装但未启动的 MuMu 实例也列出来，
/// 自动分配设备时必须过滤掉这些“有端口但连不上”的假设备。
/// </summary>
public static class AdbDeviceAvailability
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, (bool Online, DateTime CheckedAt)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(3);

    public static bool IsOnline(string? adbPath, string? serial)
    {
        if (string.IsNullOrWhiteSpace(adbPath) || string.IsNullOrWhiteSpace(serial))
            return false;

        var key = adbPath + "|" + serial;
        lock (SyncRoot)
        {
            if (Cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.CheckedAt < CacheDuration)
                return cached.Online;
        }

        var online = Probe(adbPath, serial);
        lock (SyncRoot)
            Cache[key] = (online, DateTime.UtcNow);
        return online;
    }

    private static bool Probe(string adbPath, string serial)
    {
        try
        {
            if (serial.Contains(':', StringComparison.Ordinal))
                Run(adbPath, "connect " + serial, 2500);

            var output = Run(adbPath, "-s " + serial + " get-state", 2500);
            return output.Contains("device", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("offline", StringComparison.OrdinalIgnoreCase)
                && !output.Contains("unknown", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Run(string fileName, string arguments, int timeoutMs)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            }
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return string.Empty;
        }
        return output;
    }
}
