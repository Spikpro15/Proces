using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace PCMonitorAgent;

/// <summary>
/// Reads currently playing Windows media (title, artist, playback status, and resized JPEG album art)
/// and executes playback / volume commands.
/// </summary>
public sealed class MediaController
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private long _lastPollTick;
    private string? _cachedTitle;
    private string? _cachedArtist;
    private bool _cachedPlaying;
    private string _lastTrackKey = "";
    private byte[]? _cachedArtJpeg;
    private int _artVersion;

    private const byte VK_VOLUME_MUTE = 0xAD;
    private const byte VK_VOLUME_DOWN = 0xAE;
    private const byte VK_VOLUME_UP = 0xAF;
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public byte[]? CurrentArtJpeg => _cachedArtJpeg;

    public async Task<(string? Title, string? Artist, bool Playing, int ArtVersion)> GetMediaInfoAsync()
    {
        var now = Environment.TickCount64;
        if (now - _lastPollTick < 900)
            return (_cachedTitle, _cachedArtist, _cachedPlaying, _artVersion);
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
                if (_cachedArtJpeg is not null)
                {
                    _cachedArtJpeg = null;
                    _lastTrackKey = "";
                    _artVersion++;
                }
                return (null, null, false, _artVersion);
            }

            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();

            _cachedTitle = string.IsNullOrWhiteSpace(props?.Title) ? null : props.Title.Trim();
            _cachedArtist = string.IsNullOrWhiteSpace(props?.Artist) ? null : props.Artist.Trim();
            _cachedPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var trackKey = $"{_cachedTitle}|{_cachedArtist}";
            if (trackKey != _lastTrackKey)
            {
                _lastTrackKey = trackKey;
                _cachedArtJpeg = await ExtractThumbnailJpegAsync(props?.Thumbnail);
                _artVersion++;
            }
        }
        catch
        {
            _cachedTitle = null;
            _cachedArtist = null;
            _cachedPlaying = false;
        }

        return (_cachedTitle, _cachedArtist, _cachedPlaying, _artVersion);
    }

    private static async Task<byte[]?> ExtractThumbnailJpegAsync(Windows.Storage.Streams.IRandomAccessStreamReference? thumbRef)
    {
        if (thumbRef is null) return null;
        try
        {
            using var winrtStream = await thumbRef.OpenReadAsync();
            using var netStream = winrtStream.AsStreamForRead();
            using var orig = new Bitmap(netStream);

            const int size = 240;
            using var resized = new Bitmap(size, size);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(orig, 0, 0, size, size);
            }

            using var ms = new MemoryStream();
            resized.Save(ms, ImageFormat.Jpeg);
            return ms.ToArray();
        }
        catch { return null; }
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
