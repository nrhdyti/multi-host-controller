using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using MultiHost.Shared;

namespace MultiHost.Agent.Services;

/// <summary>Collects host facts and resource metrics. Linux reads /proc; Windows uses performance counters.</summary>
public sealed class MetricsCollector : IDisposable
{
    private long _prevIdle, _prevTotal;
    private object? _winCpu, _winMem;

    public HeartbeatRequest Collect()
    {
        var hb = new HeartbeatRequest { IpAddress = GetIp() };
        try
        {
            if (OperatingSystem.IsLinux()) CollectLinux(hb);
            else if (OperatingSystem.IsWindows()) CollectWindows(hb);
            else hb.MemoryTotalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
            CollectDisk(hb);
        }
        catch { /* metrics are best-effort; never break the heartbeat */ }
        hb.CpuPercent = Math.Round(Math.Clamp(hb.CpuPercent, 0, 100), 1);
        return hb;
    }

    private void CollectLinux(HeartbeatRequest hb)
    {
        var parts = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(p => long.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var idle = parts[3] + (parts.Length > 4 ? parts[4] : 0);
        var total = parts.Sum();
        if (_prevTotal > 0 && total > _prevTotal)
            hb.CpuPercent = 100.0 * (1 - (double)(idle - _prevIdle) / (total - _prevTotal));
        _prevIdle = idle; _prevTotal = total;

        long memTotal = 0, memAvail = 0;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:")) memTotal = Kb(line);
            else if (line.StartsWith("MemAvailable:")) memAvail = Kb(line);
        }
        hb.MemoryTotalMb = memTotal / 1024;
        hb.MemoryUsedMb = (memTotal - memAvail) / 1024;
        static long Kb(string l) => long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
    }

    [SupportedOSPlatform("windows")]
    private void CollectWindows(HeartbeatRequest hb)
    {
        _winCpu ??= new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _winMem ??= new PerformanceCounter("Memory", "Available MBytes");
        hb.CpuPercent = ((PerformanceCounter)_winCpu).NextValue();
        hb.MemoryTotalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
        hb.MemoryUsedMb = hb.MemoryTotalMb - (long)((PerformanceCounter)_winMem).NextValue();
    }

    private static void CollectDisk(HeartbeatRequest hb)
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory) ?? "/";
        var d = new DriveInfo(root);
        hb.DiskTotalGb = d.TotalSize / 1024 / 1024 / 1024;
        hb.DiskUsedGb = (d.TotalSize - d.AvailableFreeSpace) / 1024 / 1024 / 1024;
    }

    public static string GetIp()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var ip = ni.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (ip is not null) return ip.Address.ToString();
        }
        return "";
    }

    /// <summary>Capability tags used by the master to check application prerequisites.</summary>
    public static List<string> DetectCapabilities(string ansiblePath)
    {
        var caps = new List<string>();
        if (OperatingSystem.IsLinux()) caps.Add("linux");
        else if (OperatingSystem.IsWindows()) caps.Add("windows");
        else if (OperatingSystem.IsMacOS()) caps.Add("macos");

        if (OperatingSystem.IsLinux())
        {
            if (Directory.Exists("/run/systemd/system")) caps.Add("systemd");
            if (File.Exists("/.dockerenv")) caps.Add("container");
            if (Environment.UserName == "root") caps.Add("root");
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ansiblePath, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p is not null && p.WaitForExit(8000) && p.ExitCode == 0) caps.Add("ansible");
        }
        catch { /* ansible not installed */ }
        return caps;
    }

    public void Dispose()
    {
        (_winCpu as IDisposable)?.Dispose();
        (_winMem as IDisposable)?.Dispose();
    }
}
