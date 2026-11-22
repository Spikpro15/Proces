using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace PCMonitorAgent;

public sealed class HardwareReader : IDisposable
{
    private readonly Computer? _computer;
    private ulong _prevIdle, _prevKernel, _prevUser;
    private long _prevNetSent, _prevNetRecv, _prevNetTick;

    public HardwareReader()
    {
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false
            };
            _computer.Open();
        }
        catch { _computer = null; }
    }

    public void ReadInto(TelemetryPayload payload)
    {
        if (_computer is not null)
        {
            foreach (var hw in _computer.Hardware)
            {
                try
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware)
                        try { sub.Update(); } catch { }

                    switch (hw.HardwareType)
                    {
                        case HardwareType.Cpu:
                            payload.CpuName = hw.Name;
                            ReadCpu(hw, payload);
                            break;
                        case HardwareType.GpuNvidia:
                        case HardwareType.GpuAmd:
                        case HardwareType.GpuIntel:
                            if (string.IsNullOrEmpty(payload.GpuName) || hw.HardwareType == HardwareType.GpuNvidia)
                            {
                                payload.GpuName = hw.Name;
                                ReadGpu(hw, payload);
                            }
                            break;
                        case HardwareType.Memory:
                            ReadMemory(hw, payload);
                            break;
                    }
                }
                catch { }
            }
        }

        if (payload.CpuLoad is null) payload.CpuLoad = ReadCpuLoadWin32();
        if (payload.RamLoad is null) ReadRamWin32(payload);
        if (payload.GpuLoad is null || payload.GpuTemp is null) ReadNvidiaSmiFallback(payload);
        ReadNetworkSpeed(payload);
    }

    private void ReadNetworkSpeed(TelemetryPayload payload)
    {
        try
        {
            long sent = 0, recv = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = ni.GetIPv4Statistics();
                sent += stats.BytesSent;
                recv += stats.BytesReceived;
            }

            var now = Environment.TickCount64;
            if (_prevNetTick > 0 && now > _prevNetTick)
            {
                double dtSec = (now - _prevNetTick) / 1000.0;
                double upMbps = Math.Max(0, (sent - _prevNetSent) * 8.0 / (dtSec * 1_000_000.0));
                double downMbps = Math.Max(0, (recv - _prevNetRecv) * 8.0 / (dtSec * 1_000_000.0));
                if (double.IsFinite(upMbps)) payload.NetUpMbps = Math.Round(upMbps, 1);
                if (double.IsFinite(downMbps)) payload.NetDownMbps = Math.Round(downMbps, 1);
            }
            _prevNetSent = sent;
            _prevNetRecv = recv;
            _prevNetTick = now;
        }
        catch { }
    }

    private static void ReadCpu(IHardware hw, TelemetryPayload payload)
    {
        double? tctlTemp = null, packageTemp = null, maxTemp = null, maxClock = null;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || !float.IsFinite(s.Value.Value)) continue;
            var val = (double)s.Value.Value;

            if (s.SensorType == SensorType.Load && s.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase))
                payload.CpuLoad = Math.Round(val, 1);
            else if (s.SensorType == SensorType.Temperature)
            {
                if (s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase)) tctlTemp = val;
                else if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)) packageTemp = val;
                if (val > 0 && (maxTemp is null || val > maxTemp)) maxTemp = val;
            }
            else if (s.SensorType == SensorType.Power && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                payload.CpuPower = Math.Round(val, 1);
            else if (s.SensorType == SensorType.Clock && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                if (maxClock is null || val > maxClock) maxClock = val;
        }

        var chosenTemp = tctlTemp ?? packageTemp ?? maxTemp;
        if (chosenTemp is not null) payload.CpuTemp = Math.Round(chosenTemp.Value, 1);
        if (maxClock is not null) payload.CpuClock = Math.Round(maxClock.Value, 0);
    }

    private static void ReadGpu(IHardware hw, TelemetryPayload payload)
    {
        double? vramUsedMb = null, vramTotalMb = null;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || !float.IsFinite(s.Value.Value)) continue;
            var val = (double)s.Value.Value;

            if (s.SensorType == SensorType.Load && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                payload.GpuLoad = Math.Round(val, 1);
            else if (s.SensorType == SensorType.Temperature && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                payload.GpuTemp = Math.Round(val, 1);
            else if (s.SensorType == SensorType.Power && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                payload.GpuPower = Math.Round(val, 1);
            else if (s.SensorType == SensorType.Clock && s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
                payload.GpuClock = Math.Round(val, 0);
            else if (s.SensorType == SensorType.SmallData)
            {
                if (s.Name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase)) vramUsedMb = val;
                else if (s.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase)) vramTotalMb = val;
            }
        }

        if (vramUsedMb is not null) payload.GpuVramUsedGb = Math.Round(vramUsedMb.Value / 1024.0, 2);
        if (vramTotalMb is not null) payload.GpuVramTotalGb = Math.Round(vramTotalMb.Value / 1024.0, 2);
    }

    private static void ReadMemory(IHardware hw, TelemetryPayload payload)
    {
        double? used = null, avail = null;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null || !float.IsFinite(s.Value.Value)) continue;
            var val = (double)s.Value.Value;

            if (s.SensorType == SensorType.Load && s.Name.Equals("Memory", StringComparison.OrdinalIgnoreCase))
                payload.RamLoad = Math.Round(val, 1);
            else if (s.SensorType == SensorType.Data)
            {
                if (s.Name.Equals("Memory Used", StringComparison.OrdinalIgnoreCase)) used = val;
                else if (s.Name.Equals("Memory Available", StringComparison.OrdinalIgnoreCase)) avail = val;
            }
        }

        if (used is not null) payload.RamUsedGb = Math.Round(used.Value, 1);
        if (used is not null && avail is not null) payload.RamTotalGb = Math.Round(used.Value + avail.Value, 1);
    }

    private static void ReadNvidiaSmiFallback(TelemetryPayload payload)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total,power.draw,clocks.gr --format=csv,noheader,nounits",
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return;
            var line = proc.StandardOutput.ReadLine();
            proc.WaitForExit(1000);
            if (string.IsNullOrWhiteSpace(line)) return;

            var parts = line.Split(',');
            if (parts.Length < 5) return;
            if (string.IsNullOrEmpty(payload.GpuName)) payload.GpuName = parts[0].Trim();
            if (payload.GpuLoad is null && TryD(parts[1], out var load)) payload.GpuLoad = Math.Round(load, 1);
            if (payload.GpuTemp is null && TryD(parts[2], out var temp)) payload.GpuTemp = Math.Round(temp, 1);
            if (payload.GpuVramUsedGb is null && TryD(parts[3], out var vu)) payload.GpuVramUsedGb = Math.Round(vu / 1024.0, 2);
            if (payload.GpuVramTotalGb is null && TryD(parts[4], out var vt)) payload.GpuVramTotalGb = Math.Round(vt / 1024.0, 2);
            if (parts.Length > 5 && payload.GpuPower is null && TryD(parts[5], out var pw)) payload.GpuPower = Math.Round(pw, 1);
            if (parts.Length > 6 && payload.GpuClock is null && TryD(parts[6], out var clk)) payload.GpuClock = Math.Round(clk, 0);
        }
        catch { }
    }

    private static bool TryD(string s, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);

    private double? ReadCpuLoadWin32()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        ulong i = ToU64(idle), k = ToU64(kernel), u = ToU64(user);
        if (_prevKernel == 0 && _prevUser == 0) { _prevIdle = i; _prevKernel = k; _prevUser = u; return null; }
        ulong di = i - _prevIdle, dk = k - _prevKernel, du = u - _prevUser;
        _prevIdle = i; _prevKernel = k; _prevUser = u;
        ulong total = dk + du;
        if (total == 0) return null;
        var res = (1.0 - (double)di / total) * 100.0;
        return double.IsFinite(res) ? Math.Round(Math.Clamp(res, 0.0, 100.0), 1) : null;
    }

    private static void ReadRamWin32(TelemetryPayload payload)
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref mem) || mem.ullTotalPhys == 0) return;
        double totalGb = mem.ullTotalPhys / 1073741824.0;
        double usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
        payload.RamTotalGb = Math.Round(totalGb, 1);
        payload.RamUsedGb = Math.Round(usedGb, 1);
        payload.RamLoad = Math.Round((usedGb / totalGb) * 100.0, 1);
    }

    private static ulong ToU64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)] private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }
    [StructLayout(LayoutKind.Sequential)] private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public void Dispose() { try { _computer?.Close(); } catch { } }
}
