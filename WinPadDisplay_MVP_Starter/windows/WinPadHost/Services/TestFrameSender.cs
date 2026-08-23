using System.Linq;
using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace WinPadHost.Services;

public sealed class TestFrameSender : IAsyncDisposable
{
    private const double TargetFps = 30.0;
    private static readonly TimeSpan TargetFrameInterval =
        TimeSpan.FromSeconds(1.0 / TargetFps);

    private TcpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _inputTask;
    private SemaphoreSlim? _frameAck;
    private static int _lastInputError;

    public bool IsRunning => _loopTask is { IsCompleted: false };

    public async Task StartAsync(
        string host,
        int port,
        Action<string> log)
    {
        await StopAsync();

        _client = new TcpClient();
        _client.NoDelay = true;
        _client.SendBufferSize = 64 * 1024;

        IPAddress address;

        if (!IPAddress.TryParse(host.Trim(), out address!))
        {
            var addresses =
                await Dns.GetHostAddressesAsync(host.Trim());

            address =
                addresses.FirstOrDefault(
                    a => a.AddressFamily ==
                         AddressFamily.InterNetwork)
                ?? addresses.First();
        }

        log("========== Displays ==========");

        foreach (var s in System.Windows.Forms.Screen.AllScreens)
        {
            log(
                $"Screen: {s.DeviceName} | " +
                $"Primary={s.Primary} | " +
                $"Left={s.Bounds.Left} | " +
                $"Top={s.Bounds.Top} | " +
                $"Size={s.Bounds.Width}x{s.Bounds.Height}"
            );
        }

        log("==============================");
        log($"Connecting to {address}:{port} ...");

        await _client.ConnectAsync(address, port);

        log("Connected. Sending Windows desktop frames.");

        _cts = new CancellationTokenSource();
        _frameAck = new SemaphoreSlim(0, 1);

        _loopTask = RunAsync(
            _client.GetStream(),
            _frameAck,
            _cts.Token,
            log);

        _inputTask = RunInputAsync(
            _client.GetStream(),
            _frameAck,
            _cts.Token,
            log);
    }

    private static async Task RunAsync(
        NetworkStream stream,
        SemaphoreSlim frameAck,
        CancellationToken ct,
        Action<string> log)
    {
        int frame = 0;
        double captureMsTotal = 0;
        double encodeMsTotal = 0;
        double writeMsTotal = 0;
        double processingMsTotal = 0;

        var sw =
            System.Diagnostics.Stopwatch.StartNew();

        byte[] header = new byte[4];

        try
        {
            CapturedFrame captured = await Task.Run(CaptureDesktop, ct);

            while (!ct.IsCancellationRequested)
            {
                long frameStarted =
                    System.Diagnostics.Stopwatch.GetTimestamp();

                byte[] jpeg = captured.Jpeg;

                BinaryPrimitives.WriteUInt32BigEndian(
                    header,
                    (uint)jpeg.Length);

                long writeStarted =
                    System.Diagnostics.Stopwatch.GetTimestamp();

                await stream.WriteAsync(header, ct);
                await stream.WriteAsync(jpeg, ct);

                // Capture the next desktop frame while the current JPEG is
                // travelling to the iPad and waiting for its acknowledgement.
                // This keeps only one frame in the network while overlapping
                // the two largest pieces of work.
                Task<CapturedFrame> nextCaptureTask =
                    Task.Run(CaptureDesktop, ct);

                if (!await frameAck.WaitAsync(TimeSpan.FromSeconds(5), ct))
                    throw new IOException("Timed out waiting for iPad frame ACK.");

                CapturedFrame nextCaptured = await nextCaptureTask;

                double writeMs =
                    System.Diagnostics.Stopwatch.GetElapsedTime(
                        writeStarted).TotalMilliseconds;

                double processingMs =
                    System.Diagnostics.Stopwatch.GetElapsedTime(
                        frameStarted).TotalMilliseconds;

                frame++;
                captureMsTotal += captured.CaptureMs;
                encodeMsTotal += captured.EncodeMs;
                writeMsTotal += writeMs;
                processingMsTotal += processingMs;

                if (frame % 30 == 0)
                {
                    double fps =
                        frame / sw.Elapsed.TotalSeconds;

                    log(
                        $"Desktop frames: {frame}, " +
                        $"FPS: {fps:F1}, " +
                        $"JPEG: {jpeg.Length / 1024} KB | " +
                        $"avg capture: {captureMsTotal / frame:F1} ms, " +
                        $"JPEG encode: {encodeMsTotal / frame:F1} ms, " +
                        $"TCP write: {writeMsTotal / frame:F1} ms, " +
                        $"processing: {processingMsTotal / frame:F1} ms");
                }

                // Pace the loop to 30 FPS without adding a fixed delay after
                // capture/encode/send. If processing is slower than the frame
                // budget, start the next frame immediately.
                TimeSpan remaining =
                    TargetFrameInterval -
                    System.Diagnostics.Stopwatch.GetElapsedTime(frameStarted);

                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, ct);
                }

                captured = nextCaptured;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log("Sender stopped: " + ex.Message);
        }
    }

    private static CapturedFrame CaptureDesktop()
    {

        var screens = System.Windows.Forms.Screen.AllScreens;

        if (screens.Length == 0)
        {
            throw new InvalidOperationException(
                "No display found.");
        }

        // 选择 Windows 桌面坐标最右侧的显示器。
        // 目前你的虚拟显示器 4 就放在主显示器右边。
        var screen = screens
            .FirstOrDefault(s => !s.Primary)
            ?? throw new InvalidOperationException(
                 "No secondary display found.");

        Rectangle bounds = screen.Bounds;

        long captureStarted =
            System.Diagnostics.Stopwatch.GetTimestamp();

        // 抓取选中的虚拟/扩展显示器画面
        using var sourceBitmap =
            new Bitmap(
                bounds.Width,
                bounds.Height,
                PixelFormat.Format24bppRgb);

        using (Graphics sourceGraphics =
               Graphics.FromImage(sourceBitmap))
        {
            sourceGraphics.CopyFromScreen(
                bounds.Left,
                bounds.Top,
                0,
                0,
                bounds.Size,
                CopyPixelOperation.SourceCopy);

            DrawCursor(sourceGraphics, bounds);
        }

        double captureMs =
            System.Diagnostics.Stopwatch.GetElapsedTime(
                captureStarted).TotalMilliseconds;

        long encodeStarted =
            System.Diagnostics.Stopwatch.GetTimestamp();

        using var ms = new MemoryStream();

        sourceBitmap.Save(
            ms,
            ImageFormat.Jpeg);

        byte[] jpeg = ms.ToArray();

        double encodeMs =
            System.Diagnostics.Stopwatch.GetElapsedTime(
                encodeStarted).TotalMilliseconds;

        return new CapturedFrame(jpeg, captureMs, encodeMs);
    }

    private static void DrawCursor(Graphics graphics, Rectangle screenBounds)
    {
        NativeMethods.CursorInfo cursorInfo = new()
        {
            Size = Marshal.SizeOf<NativeMethods.CursorInfo>()
        };

        if (!NativeMethods.GetCursorInfo(ref cursorInfo) ||
            (cursorInfo.Flags & NativeMethods.CursorShowing) == 0 ||
            !screenBounds.Contains(cursorInfo.ScreenPosition.X,
                cursorInfo.ScreenPosition.Y))
        {
            return;
        }

        if (!NativeMethods.GetIconInfo(cursorInfo.CursorHandle, out var iconInfo))
            return;

        try
        {
            int x = cursorInfo.ScreenPosition.X -
                    screenBounds.Left -
                    (int)iconInfo.HotspotX;
            int y = cursorInfo.ScreenPosition.Y -
                    screenBounds.Top -
                    (int)iconInfo.HotspotY;

            IntPtr hdc = graphics.GetHdc();
            try
            {
                NativeMethods.DrawIconEx(
                    hdc,
                    x,
                    y,
                    cursorInfo.CursorHandle,
                    0,
                    0,
                    0,
                    IntPtr.Zero,
                    NativeMethods.DrawNormal);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }
        finally
        {
            if (iconInfo.MaskBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(iconInfo.MaskBitmap);
            if (iconInfo.ColorBitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(iconInfo.ColorBitmap);
        }
    }

    private static async Task RunInputAsync(
        NetworkStream stream,
        SemaphoreSlim frameAck,
        CancellationToken ct,
        Action<string> log)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        try
        {
            int received = 0;
            int moves = 0;
            int actions = 0;
            int sendFailures = 0;
            var reportTimer = System.Diagnostics.Stopwatch.StartNew();

            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null)
                    return;

                if (line == "A")
                {
                    if (frameAck.CurrentCount == 0)
                        frameAck.Release();
                    continue;
                }

                PointerCommandResult result = ProcessPointerCommand(line);
                received++;
                if (result.Action == "M")
                    moves++;
                else if (result.IsValid)
                    actions++;
                sendFailures += result.SendFailures;

                if (reportTimer.Elapsed >= TimeSpan.FromSeconds(1))
                {
                    NativeMethods.GetCursorPos(out NativeMethods.Point cursor);
                    log(
                        $"Input/s: received={received}, move={moves}, " +
                        $"actions={actions}, sendFailures={sendFailures}, " +
                        $"lastError={Volatile.Read(ref _lastInputError)}, " +
                        $"cursor=({cursor.X},{cursor.Y})");

                    received = 0;
                    moves = 0;
                    actions = 0;
                    sendFailures = 0;
                    reportTimer.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log("Input receiver stopped: " + ex.Message);
        }
    }

    private static PointerCommandResult ProcessPointerCommand(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !double.TryParse(
                parts[1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double normalizedX) ||
            !double.TryParse(
                parts[2],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double normalizedY))
        {
            return new PointerCommandResult(false, "?", 0);
        }

        normalizedX = Math.Clamp(normalizedX, 0, 1);
        normalizedY = Math.Clamp(normalizedY, 0, 1);

        var screen = System.Windows.Forms.Screen.AllScreens
            .FirstOrDefault(s => !s.Primary);
        if (screen == null)
            return new PointerCommandResult(false, parts[0], 0);

        int sendFailures = MovePointer(screen.Bounds, normalizedX, normalizedY) ? 0 : 1;

        switch (parts[0])
        {
            case "D":
                if (!SendMouseButton(NativeMethods.MouseLeftDown))
                    sendFailures++;
                break;
            case "U":
                if (!SendMouseButton(NativeMethods.MouseLeftUp))
                    sendFailures++;
                break;
            case "C":
                if (!SendMouseButton(NativeMethods.MouseLeftDown))
                    sendFailures++;
                if (!SendMouseButton(NativeMethods.MouseLeftUp))
                    sendFailures++;
                break;
        }

        return new PointerCommandResult(true, parts[0], sendFailures);
    }

    private static bool MovePointer(
        Rectangle screenBounds,
        double normalizedX,
        double normalizedY)
    {
        Rectangle virtualBounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        double screenX = screenBounds.Left +
                         normalizedX * Math.Max(1, screenBounds.Width - 1);
        double screenY = screenBounds.Top +
                         normalizedY * Math.Max(1, screenBounds.Height - 1);

        int absoluteX = (int)Math.Round(
            (screenX - virtualBounds.Left) * 65535.0 /
            Math.Max(1, virtualBounds.Width - 1));
        int absoluteY = (int)Math.Round(
            (screenY - virtualBounds.Top) * 65535.0 /
            Math.Max(1, virtualBounds.Height - 1));

        return SendMouseInput(
            absoluteX,
            absoluteY,
            NativeMethods.MouseMove |
            NativeMethods.MouseAbsolute |
            NativeMethods.MouseVirtualDesktop);
    }

    private static bool SendMouseButton(uint flags) =>
        SendMouseInput(0, 0, flags);

    private static bool SendMouseInput(int x, int y, uint flags)
    {
        NativeMethods.Input[] inputs =
        [
            new NativeMethods.Input
            {
                Type = NativeMethods.InputMouse,
                Data = new NativeMethods.InputUnion
                {
                    Mouse = new NativeMethods.MouseInput
                    {
                        X = x,
                        Y = y,
                        Flags = flags
                    }
                }
            }
        ];

        uint sent = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf<NativeMethods.Input>());

        if (sent == 0)
            Volatile.Write(ref _lastInputError, Marshal.GetLastWin32Error());

        return sent == inputs.Length;
    }

    private readonly record struct PointerCommandResult(
        bool IsValid,
        string Action,
        int SendFailures);

    private static class NativeMethods
    {
        internal const int CursorShowing = 0x00000001;
        internal const uint DrawNormal = 0x0003;
        internal const uint InputMouse = 0;
        internal const uint MouseMove = 0x0001;
        internal const uint MouseLeftDown = 0x0002;
        internal const uint MouseLeftUp = 0x0004;
        internal const uint MouseVirtualDesktop = 0x4000;
        internal const uint MouseAbsolute = 0x8000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CursorInfo
        {
            internal int Size;
            internal int Flags;
            internal IntPtr CursorHandle;
            internal Point ScreenPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IconInfo
        {
            [MarshalAs(UnmanagedType.Bool)]
            internal bool IsIcon;
            internal uint HotspotX;
            internal uint HotspotY;
            internal IntPtr MaskBitmap;
            internal IntPtr ColorBitmap;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input
        {
            internal uint Type;
            internal InputUnion Data;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)]
            internal MouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseInput
        {
            internal int X;
            internal int Y;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal IntPtr ExtraInfo;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetIconInfo(
            IntPtr iconHandle,
            out IconInfo iconInfo);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DrawIconEx(
            IntPtr deviceContext,
            int x,
            int y,
            IntPtr iconHandle,
            int width,
            int height,
            uint animationStep,
            IntPtr flickerFreeBrush,
            uint flags);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(IntPtr objectHandle);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(
            uint inputCount,
            Input[] inputs,
            int inputSize);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);
    }

    private readonly record struct CapturedFrame(
        byte[] Jpeg,
        double CaptureMs,
        double EncodeMs);

    public async Task StopAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();

            try
            {
                if (_loopTask != null)
                    await _loopTask;

                if (_inputTask != null)
                    await _inputTask;
            }
            catch
            {
            }

            _cts.Dispose();
            _cts = null;
        }

        _client?.Dispose();

        _client = null;
        _loopTask = null;
        _inputTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
