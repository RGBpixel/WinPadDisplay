using WinPadHost.Services;

namespace WinPadHost;

public partial class MainWindow : System.Windows.Window
{
    private readonly UsbDeviceService _usb = new();
    private readonly TestFrameSender _sender = new();
    private readonly WinPadStartupService _startup = new();
    private CancellationTokenSource? _startupCts;
    private bool _startupInProgress;

    public MainWindow()
    {
        InitializeComponent();
        Append("Ready. Click '一键启动 WinPad'.");
    }

    private async void StartAll_Click(
        object sender,
        System.Windows.RoutedEventArgs e)
    {
        await StartAllAsync();
    }

    private async Task StartAllAsync()
    {
        if (_startupInProgress)
        {
            Append("Startup is already in progress.");
            return;
        }

        _startupInProgress = true;
        StartAllButton.IsEnabled = false;
        _startupCts = new CancellationTokenSource();
        CancellationToken ct = _startupCts.Token;

        try
        {
            Append("========== WinPad one-click startup ==========");
            await _startup.EnsureVirtualDisplayAsync(Append, ct);

            Append("Checking USB iPad...");
            var devices = (await _usb.GetDevicesAsync())
                .Where(d =>
                    (d.ProductType?.StartsWith(
                        "iPad",
                        StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (d.DeviceName?.Contains(
                        "iPad",
                        StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
            ct.ThrowIfCancellationRequested();

            if (devices.Count == 0)
            {
                Append("No USB iPad found. Connect/unlock the iPad, then click '一键启动 WinPad'.");
                return;
            }

            foreach (var d in devices)
                Append($"{d.DeviceName} | {d.ProductType} | iPadOS/iOS {d.ProductVersion} | {d.ConnectionType}");

            if (!int.TryParse(PortBox.Text, out int port))
            {
                Append("Invalid port.");
                return;
            }

            await _startup.EnsureUsbForwardAsync(port, Append, ct);
            Append("Waiting up to 45 seconds for WinPad Client on iPad...");

            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            int attempt = 0;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;

                try
                {
                    Action<string> attemptLog = attempt == 1 ? Append : _ => { };
                    await _sender.StartAsync(HostBox.Text, port, attemptLog);
                    await Task.Delay(750, ct);

                    if (_sender.IsRunning)
                    {
                        Append("READY");
                        Append("Windows desktop is streaming to the iPad.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    if (attempt == 1 || attempt % 5 == 0)
                        Append($"Waiting for iPad app (attempt {attempt}): {ex.Message}");
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(
                        remaining < TimeSpan.FromMilliseconds(500)
                            ? remaining
                            : TimeSpan.FromMilliseconds(500),
                        ct);
            }

            await _sender.StopAsync();
            Append("Timed out waiting for WinPad Client. Open the iPad app and click '一键启动 WinPad' again.");
        }
        catch (OperationCanceledException)
        {
            Append("One-click startup cancelled.");
        }
        catch (Exception ex)
        {
            Append("STARTUP ERROR: " + ex.Message);
        }
        finally
        {
            _startupCts?.Dispose();
            _startupCts = null;
            _startupInProgress = false;
            StartAllButton.IsEnabled = true;
        }
    }

    private async void DetectIpad_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Append("Checking USB devices...");
            var devices = await _usb.GetDevicesAsync();

            if (devices.Count == 0)
            {
                Append("No iPad/iPhone found via usbmux.");
                return;
            }

            foreach (var d in devices)
                Append($"{d.DeviceName} | {d.ProductType} | iPadOS/iOS {d.ProductVersion} | {d.ConnectionType} | {d.Identifier}");
        }
        catch (Exception ex)
        {
            Append("ERROR: " + ex.Message);
        }
    }

    private async void Connect_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out int port))
        {
            Append("Invalid port.");
            return;
        }

        try
        {
            await _startup.EnsureUsbForwardAsync(port, Append);
            await _sender.StartAsync(HostBox.Text, port, Append);
        }
        catch (Exception ex)
        {
            Append("ERROR: " + ex.Message);
        }
    }

    private async void Stop_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _startupCts?.Cancel();
        await _sender.StopAsync();
        Append("Streaming stopped.");
    }

    private void Append(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
            LogBox.ScrollToEnd();
        });
    }

    protected override async void OnClosed(EventArgs e)
    {
        _startupCts?.Cancel();
        await _sender.DisposeAsync();
        _startup.Dispose();
        base.OnClosed(e);
    }
}
