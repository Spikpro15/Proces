using LibreHardwareMonitor.Hardware;

namespace PCMonitorAgent;

public sealed class HardwareReader : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();

    public HardwareReader()
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

    public void ReadInto(TelemetryPayload payload)
    {
        _computer.Accept(_visitor);

        foreach (var hw in _computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    payload.CpuName = hw.Name;
                    ReadCpu(hw, payload);
                    break;

                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    // Prefer discrete GPU over integrated if multiple exist
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
    }

    private static void ReadCpu(IHardware hw, TelemetryPayload payload)
    {
        double? tctlTemp = null;
        double? packageTemp = null;
        double? maxTemp = null;
        double? maxClock = null;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null) continue;
            var val = (double)s.Value.Value;

            if (s.SensorType == SensorType.Load && s.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase))
            {
                payload.CpuLoad = Math.Round(val, 1);
            }
            else if (s.SensorType == SensorType.Temperature)
            {
                if (s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                    tctlTemp = val;
                else if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                    packageTemp = val;

                if (val > 0 && (maxTemp is null || val > maxTemp))
                    maxTemp = val;
            }
            else if (s.SensorType == SensorType.Power &&
                     s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            {
                payload.CpuPower = Math.Round(val, 1);
            }
            else if (s.SensorType == SensorType.Clock &&
                     s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
            {
                if (maxClock is null || val > maxClock)
                    maxClock = val;
            }
        }

        var chosenTemp = tctlTemp ?? packageTemp ?? maxTemp;
        if (chosenTemp is not null)
            payload.CpuTemp = Math.Round(chosenTemp.Value, 1);

        if (maxClock is not null)
            payload.CpuClock = Math.Round(maxClock.Value, 0);
    }

    private static void ReadGpu(IHardware hw, TelemetryPayload payload)
    {
        double? vramUsedMb = null;
        double? vramTotalMb = null;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null) continue;
            var val = (double)s.Value.Value;

            if (s.SensorType == SensorType.Load &&
                s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
            {
                payload.GpuLoad = Math.Round(val, 1);
            }
            else if (s.SensorType == SensorType.Temperature &&
                     s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
            {
                payload.GpuTemp = Math.Round(val, 1);
            }
            else if (s.SensorType == SensorType.Power &&
                     s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            {
                payload.GpuPower = Math.Round(val, 1);
            }
            else if (s.SensorType == SensorType.Clock &&
                     s.Name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase))
            {
                payload.GpuClock = Math.Round(val, 0);
            }
            else if (s.SensorType == SensorType.SmallData)
            {
                if (s.Name.Equals("GPU Memory Used", StringComparison.OrdinalIgnoreCase))
                    vramUsedMb = val;
                else if (s.Name.Equals("GPU Memory Total", StringComparison.OrdinalIgnoreCase))
                    vramTotalMb = val;
            }
        }

        if (vramUsedMb is not null)
            payload.GpuVramUsedGb = Math.Round(vramUsedMb.Value / 1024.0, 2);
        if (vramTotalMb is not null)
            payload.GpuVramTotalGb = Math.Round(vramTotalMb.Value / 1024.0, 2);
    }

    private static void ReadMemory(IHardware hw, TelemetryPayload payload)
    {
        double? used = null;
        double? avail = null;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is null) continue;
            var val = (double)s.Value.Value;

            if (s.SensorType == SensorType.Load &&
                s.Name.Equals("Memory", StringComparison.OrdinalIgnoreCase))
            {
                payload.RamLoad = Math.Round(val, 1);
            }
            else if (s.SensorType == SensorType.Data)
            {
                if (s.Name.Equals("Memory Used", StringComparison.OrdinalIgnoreCase))
                    used = val;
                else if (s.Name.Equals("Memory Available", StringComparison.OrdinalIgnoreCase))
                    avail = val;
            }
        }

        if (used is not null)
            payload.RamUsedGb = Math.Round(used.Value, 1);
        if (used is not null && avail is not null)
            payload.RamTotalGb = Math.Round(used.Value + avail.Value, 1);
    }

    public void Dispose()
    {
        _computer.Close();
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
                sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
