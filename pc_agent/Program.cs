using System.Diagnostics;

namespace PCMonitorAgent;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        var baseDir = AppContext.BaseDirectory;
        var apkPath = Path.Combine(baseDir, "PCMonitor-Android.apk");
        var config = new ConfigManager(baseDir);
        var port = config.Current.Port;

        HardwareReader? hw = null;
        try { hw = new HardwareReader(); } catch { }

        using var fps = new FpsTracker(baseDir);
        using var adb = new AdbBridge(baseDir, port);
        var sessions = new SessionAnalyzer(baseDir);
        var media = new MediaController();
        using var weather = new WeatherService(config);
        using var obs = new ObsController(config);
        using var pingMon = new PingMonitor();
        var gpuOc = new GpuOverclockController();
        using var server = new TelemetryServer(port, config, sessions, media, adb, obs, gpuOc, baseDir);

        fps.Start();
        adb.Start();
        weather.Start();
        obs.Start();
        pingMon.Start();
        server.Start();

        var cts = new CancellationTokenSource();
        long lastAudioTick = 0;
        var cachedAudio = new List<AudioAppSession>();
        string lastAutoState = "idle";
        long lastActivityTick = Environment.TickCount64;
        var loopTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var cfg = config.Current;
                var payload = new TelemetryPayload
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    PcName = Environment.MachineName,
                    UsbConnected = adb.IsConnected,
                    DeviceSerial = adb.DeviceSerial,
                    PresentMonActive = fps.IsRunning,
                    Weather = weather.Current,
                    ObsConnected = obs.IsConnected,
                    ObsStreaming = obs.IsStreaming,
                    ObsRecording = obs.IsRecording,
                    ObsStreamTime = obs.StreamTimecode,
                    ObsCurrentScene = obs.CurrentScene,
                    ObsScenes = obs.Scenes,
                    PcApkAvailable = File.Exists(apkPath),
                    WallpaperVersion = server.WallpaperVersion,
                    StandbyMode = cfg.StandbyMode,
                    Theme = cfg.Theme,
                    FontScale = cfg.FontScale,
                    LayoutCols = cfg.LayoutCols,
                    LayoutRows = cfg.LayoutRows,
                    ActivePreset = cfg.ActivePreset,
                    LayoutBlocks = cfg.LayoutBlocks,
                    Macros = cfg.Macros
                };

                try { hw?.ReadInto(payload); } catch { }
                try
                {
                    gpuOc.CheckThermalGuard(payload.GpuTemp);
                    payload.OcSupported = gpuOc.IsSupported;
                    payload.OcProfile = gpuOc.ActiveProfile;
                    payload.OcCoreMhz = gpuOc.CoreOffsetMhz;
                    payload.OcMemMhz = gpuOc.MemOffsetMhz;
                    payload.OcStatus = gpuOc.StatusText;
                }
                catch { }

                int? activePid = null;
                try
                {
                    var (curFps, low1, game, pid) = fps.GetCurrentFps();
                    payload.Fps = curFps;
                    payload.Fps1Low = low1;
                    payload.ActiveGame = game;
                    activePid = pid;
                }
                catch { }

                try
                {
                    var (mTitle, mArtist, mPlaying, artVer, posSec, durSec) = await media.GetMediaInfoAsync();
                    payload.MediaTitle = mTitle;
                    payload.MediaArtist = mArtist;
                    payload.MediaPlaying = mPlaying;
                    payload.MediaArtVersion = artVer;
                    payload.MediaPosSec = posSec;
                    payload.MediaDurSec = durSec;
                }
                catch { }

                try { sessions.OnTick(payload, activePid); payload.GamePlaytime = sessions.ActivePlaytimeText; } catch { }

                // Poll WASAPI per-app audio sessions every ~1.5s
                long nowTick = Environment.TickCount64;
                if (nowTick - lastAudioTick > 1500)
                {
                    lastAudioTick = nowTick;
                    try { cachedAudio = AudioMixerController.GetActiveSessions(); } catch { }
                }
                payload.AudioSessions = cachedAudio;
                payload.PingMs = pingMon.PingMs;
                payload.JitterMs = pingMon.JitterMs;
                payload.TopProcesses = pingMon.TopProcesses;

                // Auto-switch preset based on OBS streaming / active game / idle
                if (cfg.AutoSwitchModes)
                {
                    string desiredState = obs.IsStreaming ? "stream"
                        : (payload.Fps is not null && !string.IsNullOrEmpty(payload.ActiveGame)) ? "game" : "idle";

                    if (desiredState != "idle") lastActivityTick = nowTick;

                    if (desiredState != lastAutoState)
                    {
                        if (desiredState == "stream") { lastAutoState = "stream"; config.ApplyPreset("streamer"); }
                        else if (desiredState == "game") { lastAutoState = "game"; config.ApplyPreset("gamer"); }
                        else if (nowTick - lastActivityTick > 15000) { lastAutoState = "idle"; config.ApplyPreset("tiktok"); }
                    }
                }
                try { await server.BroadcastAsync(payload); } catch { }

                try { await Task.Delay(cfg.RefreshMs, cts.Token); }
                catch { break; }
            }
        });

        using var tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = $"PC Monitor USB v9 Bento (порт {port})",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Bento-студия экрана и отчёты", null, (_, _) =>
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/") { UseShellExecute = true });
        });
        menu.Items.Add("Обновить APK на телефоне по USB", null, async (_, _) =>
        {
            var (ok, msg) = await adb.InstallApkAsync(apkPath);
            tray.ShowBalloonTip(3000, ok ? "APK обновлён" : "Ошибка обновления APK", msg,
                ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
        });
        menu.Items.Add("Открыть папку отчётов", null, (_, _) =>
        {
            Process.Start(new ProcessStartInfo(sessions.ReportsDir) { UseShellExecute = true });
        });

        var autoItem = new ToolStripMenuItem("Автозапуск с Windows")
        {
            CheckOnClick = true,
            Checked = AutoStartManager.IsEnabled()
        };
        autoItem.Click += (_, _) =>
        {
            AutoStartManager.SetEnabled(autoItem.Checked);
            autoItem.Checked = AutoStartManager.IsEnabled();
        };
        menu.Items.Add(autoItem);

        menu.Items.Add("Переподключить USB (ADB)", null, (_, _) => adb.ForceReconnect());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) =>
        {
            cts.Cancel();
            sessions.FlushOnExit();
            tray.Visible = false;
            Application.Exit();
        });

        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) =>
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/") { UseShellExecute = true });
        };

        tray.ShowBalloonTip(2500, "PC Monitor USB v9 Bento запущен",
            "Открой студию для полного кастома блоков, погоды и цветов.", ToolTipIcon.Info);

        Application.Run();
        cts.Cancel();
        sessions.FlushOnExit();
        hw?.Dispose();
    }
}
