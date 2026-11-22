using System.Diagnostics;

namespace PCMonitorAgent;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        var baseDir = AppContext.BaseDirectory;
        var config = new ConfigManager(baseDir);
        var port = config.Current.Port;

        HardwareReader? hw = null;
        try { hw = new HardwareReader(); } catch { }

        using var fps = new FpsTracker(baseDir);
        using var adb = new AdbBridge(baseDir, port);
        var sessions = new SessionAnalyzer(baseDir);
        var media = new MediaController();
        using var server = new TelemetryServer(port, config, sessions, media);

        fps.Start();
        adb.Start();
        server.Start();

        var cts = new CancellationTokenSource();
        var loopTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var cfg = config.Current;
                var payload = new TelemetryPayload
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    UsbConnected = adb.IsConnected,
                    DeviceSerial = adb.DeviceSerial,
                    PresentMonActive = fps.IsRunning,
                    Theme = cfg.Theme,
                    FontScale = cfg.FontScale,
                    EnabledMetrics = cfg.EnabledMetrics,
                    Macros = cfg.Macros
                };

                try { hw?.ReadInto(payload); } catch { }

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
                    var (mTitle, mArtist, mPlaying, artVer) = await media.GetMediaInfoAsync();
                    payload.MediaTitle = mTitle;
                    payload.MediaArtist = mArtist;
                    payload.MediaPlaying = mPlaying;
                    payload.MediaArtVersion = artVer;
                }
                catch { }

                try { sessions.OnTick(payload, activePid); } catch { }
                try { await server.BroadcastAsync(payload); } catch { }

                try { await Task.Delay(cfg.RefreshMs, cts.Token); }
                catch { break; }
            }
        });

        using var tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = $"PC Monitor USB (порт {port})",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Студия экрана и отчёты", null, (_, _) =>
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/") { UseShellExecute = true });
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

        tray.ShowBalloonTip(2500, "PC Monitor USB v4 запущен",
            "Свайп вверх — плеер с обложкой, свайп влево — кнопки Stream Deck.", ToolTipIcon.Info);

        Application.Run();
        cts.Cancel();
        sessions.FlushOnExit();
        hw?.Dispose();
    }
}
