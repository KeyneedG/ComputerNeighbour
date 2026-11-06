using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ComputerNeighbour.Platform;

/// <summary>Cheap read-only signals for character context; no WMI, background sampler, or persistence.</summary>
public sealed class SystemContext
{
    private readonly Stopwatch session = Stopwatch.StartNew();
    private readonly object sampleGate = new();
    private TimeSpan previousCpu;
    private TimeSpan previousElapsed;
    private double? sampledCpu;

    public string Describe()
    {
        var values = new List<string>
        {
            $"Companion session duration: {Duration(session.Elapsed)}.",
            $"OS uptime: {Duration(TimeSpan.FromMilliseconds(Environment.TickCount64))}."
        };
        if (OperatingSystem.IsWindows())
        {
            values.Add(DescribePower());
            values.Add(DescribeMemory());
            var width = GetSystemMetrics(0); var height = GetSystemMetrics(1);
            values.Add(width > 0 && height > 0 ? $"Primary display: {width} × {height} physical pixels." : "Primary display: unavailable.");
        }
        else
        {
            values.Add("Battery/AC, system memory pressure and display: unavailable on this platform.");
        }
        values.Add(DescribeOwnCpu());
        return string.Join("\n", values);
    }

    private static string DescribePower()
    {
        if (!GetSystemPowerStatus(out var status)) return "Battery/AC: unavailable.";
        var source = status.AcLineStatus switch { 0 => "on battery", 1 => "connected to AC", _ => "power source unknown" };
        if ((status.BatteryFlag & 128) != 0) return $"Power: {source}; no system battery.";
        if (status.BatteryFlag == 255) return $"Power: {source}; battery status unavailable.";
        var charge = status.BatteryLifePercent <= 100 ? $"{status.BatteryLifePercent}%" : "unknown charge";
        var charging = (status.BatteryFlag & 8) != 0 ? ", charging" : "";
        var saver = status.SystemStatusFlag == 1 ? ", battery saver active" : "";
        var remaining = status.BatteryLifeTime != uint.MaxValue ? $", estimated remaining {Duration(TimeSpan.FromSeconds(status.BatteryLifeTime))}" : "";
        return $"Power: {source}; battery {charge}{charging}{saver}{remaining}.";
    }

    private static string DescribeMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) return "System memory pressure: unavailable.";
        const double gib = 1024d * 1024 * 1024;
        return FormattableString.Invariant($"System memory: {status.MemoryLoad}% in use; {status.AvailablePhysical / gib:F1} GiB available of {status.TotalPhysical / gib:F1} GiB.");
    }

    private string DescribeOwnCpu()
    {
        lock (sampleGate)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var currentCpu = process.TotalProcessorTime; var elapsed = session.Elapsed;
                if (previousElapsed > TimeSpan.Zero && elapsed - previousElapsed >= TimeSpan.FromMilliseconds(250))
                    sampledCpu = Math.Clamp((currentCpu - previousCpu).TotalMilliseconds / (elapsed - previousElapsed).TotalMilliseconds / Math.Max(1, Environment.ProcessorCount) * 100, 0, 100);
                previousCpu = currentCpu; previousElapsed = elapsed;
                var cpu = sampledCpu is { } sample ? sample.ToString("F1", CultureInfo.InvariantCulture) + "% of total logical CPU capacity over the last context interval" : "unavailable until the next sample";
                return FormattableString.Invariant($"Companion process CPU: {cpu}; working memory {process.WorkingSet64 / (1024d * 1024):F0} MiB.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return "Companion process CPU/memory: unavailable.";
            }
        }
    }

    private static string Duration(TimeSpan time) => time.TotalDays >= 1
        ? FormattableString.Invariant($"{(int)time.TotalDays}d {time.Hours}h {time.Minutes}m")
        : FormattableString.Invariant($"{(int)time.TotalHours}h {time.Minutes}m");

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
