using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace PCMonitorAgent;

/// <summary>
/// Reads Windows SMTC media info + raw album art bytes (JPEG/PNG/WebP — no GDI+ crash on WebP)
/// with automatic retry when a player loads thumbnail slightly after track change.
/// </summary>
public sealed class MediaController
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private long _lastPollTick;
    private string? _cachedTitle;
    private string? _cachedArtist;
    private bool _cachedPlaying;
    private double? _cachedPosSec;
    private double? _cachedDurSec;
    private string _lastTrackKey = "";
    private byte[]? _cachedArtBytes;
    private string _cachedArtContentType = "image/jpeg";
    private int _artVersion;
    private int _artRetriesLeft;

    private const byte VK_VOLUME_MUTE = 0xAD;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public byte[]? CurrentArtBytes => _cachedArtBytes;
    public string CurrentArtContentType => _cachedArtContentType;

    public async Task<(string? Title, string? Artist, bool Playing, int ArtVersion, double? PosSec, double? DurSec)> GetMediaInfoAsync()
    {
        var now = Environment.TickCount64;
        if (now - _lastPollTick < 750)
            return (_cachedTitle, _cachedArtist, _cachedPlaying, _artVersion, _cachedPosSec, _cachedDurSec);
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
                _cachedPosSec = null;
                _cachedDurSec = null;
                if (_cachedArtBytes is not null)
                {
                    _cachedArtBytes = null;
                    _lastTrackKey = "";
                    _artVersion++;
                }
                return (null, null, false, _artVersion, null, null);
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            _cachedTitle = string.IsNullOrWhiteSpace(props?.Title) ? null : props.Title.Trim();
            _cachedArtist = string.IsNullOrWhiteSpace(props?.Artist) ? null : props.Artist.Trim();
            _cachedPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            if (timeline is not null && timeline.EndTime.TotalSeconds > 1)
            {
                double dur = timeline.EndTime.TotalSeconds;
                double pos = timeline.Position.TotalSeconds;
                if (_cachedPlaying && timeline.LastUpdatedTime != DateTimeOffset.MinValue)
                {
                    double elapsed = (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds;
                    if (elapsed > 0 && elapsed < 3600) pos += elapsed;
                }
                _cachedDurSec = Math.Round(dur, 1);
                _cachedPosSec = Math.Round(Math.Clamp(pos, 0, dur), 1);
            }
            else
            {
                _cachedPosSec = null;
                _cachedDurSec = null;
            }

            var trackKey = $"{_cachedTitle}|{_cachedArtist}";
            if (trackKey != _lastTrackKey)
            {
                _lastTrackKey = trackKey;
                _artRetriesLeft = 6;
                var (bytes, ct) = await ReadRawThumbnailAsync(props?.Thumbnail);
                _cachedArtBytes = bytes;
                _cachedArtContentType = ct;
                if (bytes is not null) _artRetriesLeft = 0;
                _artVersion++;
            }
            else if (_cachedArtBytes is null && _artRetriesLeft > 0)
            {
                _artRetriesLeft--;
                var (bytes, ct) = await ReadRawThumbnailAsync(props?.Thumbnail);
                if (bytes is not null)
                {
                    _cachedArtBytes = bytes;
                    _cachedArtContentType = ct;
                    _artRetriesLeft = 0;
                    _artVersion++;
                }
            }
        }
        catch
        {
            _cachedTitle = null;
            _cachedArtist = null;
            _cachedPlaying = false;
            _cachedPosSec = null;
            _cachedDurSec = null;
        }

        return (_cachedTitle, _cachedArtist, _cachedPlaying, _artVersion, _cachedPosSec, _cachedDurSec);
    }

    private static async Task<(byte[]? Bytes, string ContentType)> ReadRawThumbnailAsync(Windows.Storage.Streams.IRandomAccessStreamReference? thumbRef)
    {
        if (thumbRef is null) return (null, "image/jpeg");
        try
        {
            using var winrtStream = await thumbRef.OpenReadAsync();
            var contentType = string.IsNullOrWhiteSpace(winrtStream.ContentType) ? "image/jpeg" : winrtStream.ContentType;
            using var netStream = winrtStream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            var arr = ms.ToArray();
            if (arr.Length < 100) return (null, contentType);
            return (arr, contentType);
        }
        catch { return (null, "image/jpeg"); }
    }

    public async Task<bool> ExecuteCommandAsync(string action)
    {
        try
        {
            switch (action)
            {
                case "vol_up": SendKey(VK_VOLUME_UP); return true;
                case "vol_down": SendKey(VK_VOLUME_DOWN); return true;
                case "vol_mute": SendKey(VK_VOLUME_MUTE); return true;
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

            _lastPollTick = 0;
            return handled;
        }
        catch { return false; }
    }

    private static void SendKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
