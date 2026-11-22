using System.Runtime.InteropServices;

namespace PCMonitorAgent;

/// <summary>
/// Executes global Windows hotkeys for the phone Stream Deck panel (OBS, Discord, screenshots, volume).
/// </summary>
public static class MacroController
{
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static readonly Dictionary<string, byte> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11,
        ["shift"] = 0x10,
        ["alt"] = 0x12,
        ["win"] = 0x5B, ["lwin"] = 0x5B,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C,
        ["space"] = 0x20, ["enter"] = 0x0D, ["esc"] = 0x1B, ["tab"] = 0x09,
        ["vol_mute"] = 0xAD, ["vol_down"] = 0xAE, ["vol_up"] = 0xAF,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73,
        ["f5"] = 0x74, ["f6"] = 0x75, ["f7"] = 0x76, ["f8"] = 0x77,
        ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
        ["f13"] = 0x7C, ["f14"] = 0x7D, ["f15"] = 0x7E, ["f16"] = 0x7F,
        ["f17"] = 0x80, ["f18"] = 0x81, ["f19"] = 0x82, ["f20"] = 0x83,
        ["f21"] = 0x84, ["f22"] = 0x85, ["f23"] = 0x86, ["f24"] = 0x87
    };

    public static bool Trigger(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey)) return false;

        var tokens = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var vks = new List<byte>();

        foreach (var tok in tokens)
        {
            if (KeyMap.TryGetValue(tok, out var mapped))
            {
                vks.Add(mapped);
            }
            else if (tok.Length == 1)
            {
                char c = char.ToUpperInvariant(tok[0]);
                if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                    vks.Add((byte)c);
            }
        }

        if (vks.Count == 0) return false;

        try
        {
            foreach (var vk in vks)
                keybd_event(vk, 0, 0, UIntPtr.Zero);
            Thread.Sleep(35);
            for (int i = vks.Count - 1; i >= 0; i--)
                keybd_event(vks[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            return true;
        }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
