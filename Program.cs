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
        using var server = new TelemetryServer(port, config, sessions);

        fps.Start();
        adb.Start();
        server.Start();

        var cts = new CancellationTokenSource();
        var loopTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var payload = new TelemetryPayload
                {
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    UsbConnected = adb.IsConnected,
                    DeviceSerial = adb.DeviceSerial,
                    PresentMonActive = fps.IsRunning,
                    EnabledMetrics = config.Current.EnabledMetrics
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

                try { sessions.OnTick(payload, activePid); } catch { }
                try { await server.BroadcastAsync(payload); } catch { }

                try { await Task.Delay(config.Current.RefreshMs, cts.Token); }
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
        menu.Items.Add("Настройки и отчёты (в браузере)", null, (_, _) =>
        {
            Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}/") { UseShellExecute = true });
        });
        menu.Items.Add("Открыть папку отчётов", null, (_, _) =>
        {
            Process.Start(new ProcessStartInfo(sessions.ReportsDir) { UseShellExecute = true });
        });
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

        tray.ShowBalloonTip(2500, "PC Monitor USB запущен",
            "Подключи Android по USB и открой приложение на телефоне.", ToolTipIcon.Info);

        Application.Run();
        cts.Cancel();
        sessions.FlushOnExit();
        hw?.Dispose();
    }
}
