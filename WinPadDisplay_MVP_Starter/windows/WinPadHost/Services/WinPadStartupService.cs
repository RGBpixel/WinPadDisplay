using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;

namespace WinPadHost.Services;

public sealed class WinPadStartupService : IDisposable
{
    private const string IddSampleAppPath =
        @"D:\WinPadDisplay\IddSample\x64\Debug\IddSampleApp.exe";

    private Process? _forwardProcess;

    public async Task EnsureVirtualDisplayAsync(
        Action<string> log,
        CancellationToken ct = default)
    {
        if (HasWinPadDisplay())
        {
            log("Virtual display is ready (1920x1440).");
            return;
        }

        if (Process.GetProcessesByName("IddSampleApp").Length == 0)
        {
            if (!File.Exists(IddSampleAppPath))
                throw new FileNotFoundException("IddSampleApp.exe not found.", IddSampleAppPath);

            log("Starting virtual display helper. Windows may ask for UAC approval...");
            Process.Start(new ProcessStartInfo
            {
                FileName = IddSampleAppPath,
                WorkingDirectory = Path.GetDirectoryName(IddSampleAppPath)!,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        else
        {
            log("IddSampleApp is already running; waiting for the display...");
        }

        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500, ct);
            if (HasWinPadDisplay())
            {
                log("Virtual display is ready (1920x1440).");
                return;
            }
        }

        throw new InvalidOperationException(
            "Virtual display did not appear. Check the IddSampleApp/UAC window.");
    }

    public async Task EnsureUsbForwardAsync(
        int port,
        Action<string> log,
        CancellationToken ct = default)
    {
        if (IsLocalPortListening(port))
        {
            log($"USB forwarding is already listening on 127.0.0.1:{port}.");
            return;
        }
        log($"Starting USB forwarding on 127.0.0.1:{port}...");

        _forwardProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"-m pymobiledevice3 usbmux forward {port} {port}",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Unable to start pymobiledevice3.");

        DateTime deadline = DateTime.UtcNow.AddSeconds(5);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_forwardProcess.HasExited)
                throw new InvalidOperationException(
                    $"USB forwarding exited with code {_forwardProcess.ExitCode}.");

            if (IsLocalPortListening(port))
            {
                log($"USB forwarding is ready on 127.0.0.1:{port}.");
                return;
            }

            await Task.Delay(200, ct);
        }

        throw new InvalidOperationException(
            $"USB forwarding did not open 127.0.0.1:{port} within 5 seconds.");
    }

    private static bool HasWinPadDisplay()
    {
        return System.Windows.Forms.Screen.AllScreens.Any(
            s => !s.Primary &&
                 s.Bounds.Width == 1920 &&
                 s.Bounds.Height == 1440);
    }
    private static bool IsLocalPortListening(int port)
    {
        return IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Any(ep => ep.Port == port &&
                       (IPAddress.IsLoopback(ep.Address) ||
                        ep.Address.Equals(IPAddress.Any) ||
                        ep.Address.Equals(IPAddress.IPv6Any)));
    }

    public void Dispose()
    {
        try
        {
            if (_forwardProcess is { HasExited: false })
                _forwardProcess.Kill(entireProcessTree: true);
        }
        catch { }

        _forwardProcess?.Dispose();
        _forwardProcess = null;
    }
}
