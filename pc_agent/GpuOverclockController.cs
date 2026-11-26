using System.Runtime.InteropServices;

namespace PCMonitorAgent;

/// <summary>
/// Direct NVIDIA NVAPI PStates20 overclocking controller (nvapi64.dll) with Thermal Guard auto-reset.
/// Uses the exact NV_GPU_PERF_PSTATES20_INFO_V1 layout (0x1c94 bytes, version 0x11c94).
/// </summary>
public sealed class GpuOverclockController
{
    private const int StructBytes = 0x1c94;
    private const int StructInts = StructBytes / 4;
    private const int StructVersion = 0x11c94;
    private const double ThermalGuardTempC = 84.0;

    private delegate IntPtr QueryInterfaceDel(uint id);
    private delegate int NvInitDel();
    private delegate int NvEnumGpusDel(IntPtr[] handles, out int count);
    private delegate int NvPstatesDel(IntPtr gpuHandle, IntPtr infoPtr);

    private NvPstatesDel? _getPstates;
    private NvPstatesDel? _setPstates;
    private IntPtr _gpuHandle = IntPtr.Zero;
    private readonly object _lock = new();

    public bool IsSupported { get; private set; }
    public string ActiveProfile { get; private set; } = "stock";
    public int CoreOffsetMhz { get; private set; }
    public int MemOffsetMhz { get; private set; }
    public string StatusText { get; private set; } = "Сток (0 / 0 МГц)";

    public GpuOverclockController()
    {
        TryInitNvapi();
    }

    private void TryInitNvapi()
    {
        try
        {
            if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib))
            {
                StatusText = "NVAPI не найден";
                return;
            }
            if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qiPtr))
            {
                StatusText = "NVAPI недоступен";
                return;
            }

            var qi = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDel>(qiPtr);
            IntPtr initPtr = qi(0x0150E828);
            IntPtr enumPtr = qi(0xE5AC921F);
            IntPtr getPtr = qi(0x6FF81213);
            IntPtr setPtr = qi(0x0F4DAE6B);

            if (initPtr == IntPtr.Zero || enumPtr == IntPtr.Zero || getPtr == IntPtr.Zero || setPtr == IntPtr.Zero)
            {
                StatusText = "NVAPI функции недоступны";
                return;
            }

            var nvInit = Marshal.GetDelegateForFunctionPointer<NvInitDel>(initPtr);
            var nvEnum = Marshal.GetDelegateForFunctionPointer<NvEnumGpusDel>(enumPtr);
            _getPstates = Marshal.GetDelegateForFunctionPointer<NvPstatesDel>(getPtr);
            _setPstates = Marshal.GetDelegateForFunctionPointer<NvPstatesDel>(setPtr);

            if (nvInit() != 0) return;

            var handles = new IntPtr[64];
            if (nvEnum(handles, out int count) != 0 || count <= 0)
            {
                StatusText = "NVIDIA GPU не найдена";
                return;
            }

            _gpuHandle = handles[0];
            IsSupported = true;
            ReadCurrentOffsets();
        }
        catch
        {
            IsSupported = false;
            StatusText = "Ошибка NVAPI";
        }
    }

    public void CheckThermalGuard(double? gpuTempC)
    {
        if (!IsSupported || gpuTempC is null) return;
        if (gpuTempC.Value >= ThermalGuardTempC && ActiveProfile != "stock")
        {
            ApplyProfile("stock");
            StatusText = $"🛡 Автосброс ({gpuTempC.Value:0}°C ≥ 84°C)";
        }
    }

    public (bool Ok, string Message) ApplyProfile(string profile)
    {
        lock (_lock)
        {
            if (!IsSupported || _setPstates is null || _gpuHandle == IntPtr.Zero)
                return (false, "Разгон NVAPI недоступен на этой видеокарте");

            var (coreMhz, memMhz, label) = profile switch
            {
                "mild" => (60, 200, "Мягкий (+5%)"),
                "standard" => (115, 450, "Стандарт (+10%)"),
                "extreme" => (170, 750, "Экстрим (+20%)"),
                _ => (0, 0, "Сток")
            };

            bool okCore = SetClockOffset(domainId: 0, offsetMhz: coreMhz);
            bool okMem = SetClockOffset(domainId: 4, offsetMhz: memMhz * 2); // GDDR6/7 NVAPI uses 2x delta

            if (okCore)
            {
                ActiveProfile = profile is "mild" or "standard" or "extreme" ? profile : "stock";
                CoreOffsetMhz = coreMhz;
                MemOffsetMhz = okMem ? memMhz : 0;
                StatusText = $"{label} ({(coreMhz >= 0 ? "+" : "")}{coreMhz} / +{(okMem ? memMhz : 0)} МГц)";
                return (true, StatusText);
            }

            StatusText = "Драйвер отклонил разгон (нужны права админа)";
            return (false, StatusText);
        }
    }

    private void ReadCurrentOffsets()
    {
        if (_getPstates is null || _gpuHandle == IntPtr.Zero) return;
        var buf = new int[StructInts];
        buf[0] = StructVersion;
        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            if (_getPstates(_gpuHandle, pin.AddrOfPinnedObject()) == 0)
            {
                // pstates[0].clocks[0] starts at int index 7; delta value at +3 = 10
                int coreKHz = buf[10];
                // pstates[0].clocks[1] starts at int index 18; delta value at +3 = 21
                int memKHz = buf[21];
                CoreOffsetMhz = coreKHz / 1000;
                MemOffsetMhz = memKHz / 2000;
                StatusText = CoreOffsetMhz == 0 && MemOffsetMhz == 0
                    ? "Сток (0 / 0 МГц)"
                    : $"Активно (+{CoreOffsetMhz} / +{MemOffsetMhz} МГц)";
            }
        }
        catch { }
        finally { pin.Free(); }
    }

    private bool SetClockOffset(int domainId, int offsetMhz)
    {
        if (_setPstates is null || _gpuHandle == IntPtr.Zero) return false;
        int clampedKHz = Math.Clamp(offsetMhz, -250, 1500) * 1000;
        var buf = new int[StructInts];
        buf[0] = StructVersion;
        buf[2] = 1; // numPstates
        buf[3] = 1; // numClocks
        buf[5] = 0; // pstateId = P0
        buf[7] = domainId; // 0 = Core, 4 = Memory
        buf[10] = clampedKHz;

        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            return _setPstates(_gpuHandle, pin.AddrOfPinnedObject()) == 0;
        }
        catch { return false; }
        finally { pin.Free(); }
    }
}
