using System.Diagnostics;
using System.Text.Json;
using WinPadHost.Models;

namespace WinPadHost.Services;

public sealed class UsbDeviceService
{
    public async Task<IReadOnlyList<UsbDeviceInfo>> GetDevicesAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = "-m pymobiledevice3 usbmux list",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Python。请确认已安装 Python。 ");
        string stdout = await p.StandardOutput.ReadToEndAsync();
        string stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        if (p.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "pymobiledevice3 执行失败" : stderr);

        return JsonSerializer.Deserialize<List<UsbDeviceInfo>>(stdout,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }
}
