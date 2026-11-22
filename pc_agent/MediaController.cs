using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace PCMonitorAgent;

/// <summary>
/// Reads currently playing Windows media (SMTC: Spide, Spotify, Yandex Music, Edge/YouTube, etc.)
/// and sends playback / volume commands.
/// </summary>
public sealed class MediaController
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private long _lastPollTick;
    private string? _cachedTitle;
    private string? _cachedArtist;
    private bool _cachedPlaying;

    private const byte VK_VOLUME_MUTE = 0xAD;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public async Task<(string? Title, string? Artist, bool Playing)> GetMediaInfoAsync()
    {
        var now = Environment.TickCount64;
        if (now - _lastPollTick < 900)
            return (_cachedTitle, _cachedArtist, _cachedPlaying);
        _lastPollTick = now;

        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager?.GetCurrentSession();
            if (session is null)
            {
                _cachedTitle = null;
                _cachedArtist = null;
                _cachedPlaying = false;
                return (null, null, false);
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();

            _cachedTitle = string.IsNullOrWhiteSpace(props?.Title) ? null : props.Title.Trim();
            _cachedArtist = string.IsNullOrWhiteSpace(props?.Artist) ? null : props.Artist.Trim();
            _cachedPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            _cachedTitle = null;
            _cachedArtist = null;
            _cachedPlaying = false;
        }

        return (_cachedTitle, _cachedArtist, _cachedPlaying);
    }

    public async Task<bool> ExecuteCommandAsync(string action)
    {
        try
        {
            switch (action)
            {
                case "vol_up":
                    SendKey(VK_VOLUME_UP);
                    return true;
                case "vol_down":
                    SendKey(VK_VOLUME_DOWN);
                    return true;
                case "vol_mute":
                    SendKey(VK_VOLUME_MUTE);
                    return true;
            }

            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _manager?.GetCurrentSession();

            bool handled = false;
            if (session is not null)
            {
                handled = action switch
                {
                    "play_pause" => await session.TryTogglePlayPauseAsync(),
                    "next" => await session.TrySkipNextAsync(),
                    "prev" => await session.TrySkipPreviousAsync(),
                    _ => false
                };
            }

            if (!handled)
            {
                switch (action)
                {
                    case "play_pause": SendKey(VK_MEDIA_PLAY_PAUSE); handled = true; break;
                    case "next": SendKey(VK_MEDIA_NEXT_TRACK); handled = true; break;
                    case "prev": SendKey(VK_MEDIA_PREV_TRACK); handled = true; break;
                }
            }

            _lastPollTick = 0; // force immediate refresh on next tick
            return handled;
        }
        catch
        {
            return false;
        }
    }

    private static void SendKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
