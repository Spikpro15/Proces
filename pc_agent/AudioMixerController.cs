using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCMonitorAgent;

/// <summary>
/// Native Windows Core Audio (WASAPI) per-app volume mixer (no external NuGet packages required).
/// Allows adjusting volume & mute for individual apps (Game, Discord, Browser, Spide, OBS) from Android.
/// </summary>
public static class AudioMixerController
{
    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    public static List<AudioAppSession> GetActiveSessions()
    {
        var result = new Dictionary<int, AudioAppSession>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
            enumerator.GetDefaultAudioEndpoint(0, 1, out var device);
            var iid = IID_IAudioSessionManager2;
            device.Activate(ref iid, 23, IntPtr.Zero, out var mgrObj);
            var mgr = (IAudioSessionManager2)mgrObj;
            mgr.GetSessionEnumerator(out var sessionEnum);
            sessionEnum.GetCount(out int count);

            for (int i = 0; i < count; i++)
            {
                try
                {
                    sessionEnum.GetSession(i, out var ctrl);
                    ctrl.GetProcessId(out uint pidU);
                    int pid = (int)pidU;
                    if (pid <= 0 || result.ContainsKey(pid)) continue;

                    string name;
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        name = p.ProcessName;
                    }
                    catch { continue; }

                    if (string.IsNullOrWhiteSpace(name) || name.Equals("Idle", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var vol = ctrl as ISimpleAudioVolume;
                    if (vol is null) continue;
                    vol.GetMasterVolume(out float level);
                    vol.GetMute(out bool muted);

                    result[pid] = new AudioAppSession
                    {
                        Pid = pid,
                        Name = name,
                        Volume = Math.Clamp((int)Math.Round(level * 100f), 0, 100),
                        Muted = muted
                    };
                }
                catch { }
            }
        }
        catch { }

        return result.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Take(10).ToList();
    }

    public static bool SetAppAudio(int targetPid, int? volumePct, bool? muted)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)!)!;
            enumerator.GetDefaultAudioEndpoint(0, 1, out var device);
            var iid = IID_IAudioSessionManager2;
            device.Activate(ref iid, 23, IntPtr.Zero, out var mgrObj);
            var mgr = (IAudioSessionManager2)mgrObj;
            mgr.GetSessionEnumerator(out var sessionEnum);
            sessionEnum.GetCount(out int count);

            bool changed = false;
            var ctx = Guid.Empty;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    sessionEnum.GetSession(i, out var ctrl);
                    ctrl.GetProcessId(out uint pidU);
                    if ((int)pidU != targetPid) continue;

                    var vol = ctrl as ISimpleAudioVolume;
                    if (vol is null) continue;

                    if (volumePct is not null)
                        vol.SetMasterVolume(Math.Clamp(volumePct.Value, 0, 100) / 100f, ref ctx);
                    if (muted is not null)
                        vol.SetMute(muted.Value, ref ctx);
                    changed = true;
                }
                catch { }
            }
            return changed;
        }
        catch { return false; }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int NotImpl1();
        int NotImpl2();
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DDE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionCount, out IAudioSessionControl2 Session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int NotImpl0(); int NotImpl1(); int NotImpl2(); int NotImpl3(); int NotImpl4();
        int NotImpl5(); int NotImpl6(); int NotImpl7(); int NotImpl8();
        int NotImpl9(); int NotImpl10();
        [PreserveSig] int GetProcessId(out uint pRetVal);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float fLevel, ref Guid EventContext);
        [PreserveSig] int GetMasterVolume(out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid EventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }
}
