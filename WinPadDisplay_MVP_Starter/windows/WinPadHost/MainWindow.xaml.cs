using WinPadHost.Services;

namespace WinPadHost;

public partial class MainWindow : System.Windows.Window
{
    private readonly UsbDeviceService _usb = new();
    private readonly TestFrameSender _sender = new();

    public MainWindow()
    {
        InitializeComponent();
        Append("Ready. First click '检测 iPad'.");
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
            await _sender.StartAsync(HostBox.Text, port, Append);
        }
        catch (Exception ex)
        {
            Append("ERROR: " + ex.Message);
        }
    }

    private async void Stop_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await _sender.StopAsync();
        Append("Stopped.");
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
        await _sender.DisposeAsync();
        base.OnClosed(e);
    }
}
