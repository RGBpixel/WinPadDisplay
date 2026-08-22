using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinPadHost.Services;

public sealed class TestFrameSender : IAsyncDisposable
{
    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public async Task StartAsync(string host, int port, Action<string> log)
    {
        await StopAsync();

        _client = new TcpClient();

        IPAddress address;
        if (!IPAddress.TryParse(host.Trim(), out address!))
        {
            var addresses = await Dns.GetHostAddressesAsync(host.Trim());
            address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.First();
        }

        log($"Connecting to {address}:{port} ...");
        await _client.ConnectAsync(address, port);
        log("Connected. Sending length-prefixed JPEG test frames.");

        _cts = new CancellationTokenSource();
        _loopTask = RunAsync(_client.GetStream(), _cts.Token, log);
    }

    private static async Task RunAsync(NetworkStream stream, CancellationToken ct, Action<string> log)
    {
        int frame = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[] jpeg = CreateTestFrame(frame++);
                byte[] header = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)jpeg.Length);
                await stream.WriteAsync(header, ct);
                await stream.WriteAsync(jpeg, ct);
                await stream.FlushAsync(ct);

                if (frame % 30 == 0)
                    log($"Sent {frame} frames, {sw.Elapsed.TotalSeconds:F1}s");

                await Task.Delay(66, ct); // ~15 FPS transport validation only
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log("Sender stopped: " + ex.Message);
        }
    }

    private static byte[] CreateTestFrame(int frame)
    {
        const int width = 1280;
        const int height = 800;
        const int stride = width * 4;
        byte[] pixels = new byte[stride * height];

        int t = frame % 255;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                pixels[i + 0] = (byte)((x + t) % 256);       // B
                pixels[i + 1] = (byte)((y + t * 2) % 256);   // G
                pixels[i + 2] = (byte)((x + y + t) % 256);   // R
                pixels[i + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 72 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public async Task StopAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            try { if (_loopTask != null) await _loopTask; } catch { }
            _cts.Dispose();
            _cts = null;
        }

        _client?.Dispose();
        _client = null;
        _loopTask = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
