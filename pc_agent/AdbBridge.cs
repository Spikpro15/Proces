using System.Diagnostics;

namespace PCMonitorAgent;

public sealed class AdbBridge : IDisposable
{
    private readonly string _adbPath;
    private readonly int _port;
    private readonly CancellationTokenSource _cts = new();

    public bool IsConnected { get; private set; }
    public string? DeviceSerial { get; private set; }
    public bool AdbFound => File.Exists(_adbPath) || _adbPath == "adb";

    public AdbBridge(string baseDir, int port)
    {
        _port = port;
        var bundled = Path.Combine(baseDir, "tools", "platform-tools", "adb.exe");
        var local = Path.Combine(baseDir, "adb.exe");
        _adbPath = File.Exists(bundled) ? bundled : File.Exists(local) ? local : "adb";
    }

    public void Start() => Task.Run(() => WatchLoop(_cts.Token));

    public void ForceReconnect() => Task.Run(CheckAndForwardOnce);

    public async Task<(bool Ok, string Message)> InstallApkAsync(string apkPath)
    {
        if (!File.Exists(apkPath))
            return (false, "Файл PCMonitor-Android.apk не найден рядом с PCMonitorAgent.exe");

        var serial = DeviceSerial;
        var prefix = string.IsNullOrEmpty(serial) ? "" : $"-s {serial} ";
        var output = await RunAdbAsync($"{prefix}install -r \"{apkPath}\"");

        if (output is not null && output.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.OrdinalIgnoreCase))
        {
            await RunAdbAsync($"{prefix}uninstall com.pcmonitor.usb");
            output = await RunAdbAsync($"{prefix}install \"{apkPath}\"");
        }

        if (output is not null && output.Contains("Success", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(500);
            await RunAdbAsync($"{prefix}reverse tcp:{_port} tcp:{_port}");
            await RunAdbAsync($"{prefix}shell am start -n com.pcmonitor.usb/.MainActivity");
            return (true, "Приложение на телефоне обновлено и перезапущено!");
        }
        return (false, string.IsNullOrWhiteSpace(output) ? "Не удалось установить APK через ADB" : output.Trim());
    }

    private async Task WatchLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await CheckAndForwardOnce();
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    private async Task CheckAndForwardOnce()
    {
        try
        {
            var output = await RunAdbAsync("devices");
            if (output is null) { IsConnected = false; DeviceSerial = null; return; }

            string? serial = null;
            foreach (var rawLine in output.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.EndsWith("\tdevice", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains(" device", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[1] == "device") { serial = parts[0]; break; }
                }
            }

            if (serial is not null)
            {
                await RunAdbAsync($"-s {serial} reverse tcp:{_port} tcp:{_port}");
                IsConnected = true;
                DeviceSerial = serial;
            }
            else
            {
                IsConnected = false;
                DeviceSerial = null;
            }
        }
        catch { IsConnected = false; DeviceSerial = null; }
    }

    private async Task<string?> RunAdbAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
        catch { return null; }
    }

    public void Dispose() => _cts.Cancel();
}
