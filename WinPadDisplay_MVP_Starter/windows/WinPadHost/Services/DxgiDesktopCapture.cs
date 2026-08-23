using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace WinPadHost.Services;

public sealed class DxgiDesktopCapture : IDisposable
{
    private readonly IDXGIFactory1 _factory;
    private readonly IDXGIAdapter1 _adapter;
    private readonly IDXGIOutput1 _output;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly Rectangle _desktopBounds;
    private ID3D11Texture2D? _staging;
    private byte[]? _basePixels;
    private byte[]? _outputPixels;
    private int _width;
    private int _height;
    private bool _disposed;

    public string DeviceName { get; }

    public DxgiDesktopCapture(string deviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        DeviceName = deviceName;
        _factory = CreateDXGIFactory1<IDXGIFactory1>();
        FindOutputByName(
            _factory,
            deviceName,
            out _adapter,
            out _output);

        D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            null,
            out ID3D11Device? device).CheckError();
        _device = device
            ?? throw new InvalidOperationException("D3D11CreateDevice returned no device.");
        _context = _device.ImmediateContext;
        _duplication = _output.DuplicateOutput(_device);

        var coordinates = _output.Description.DesktopCoordinates;
        _desktopBounds = Rectangle.FromLTRB(
            coordinates.Left,
            coordinates.Top,
            coordinates.Right,
            coordinates.Bottom);
    }

    public DxgiCapturedFrame Capture(int timeoutMilliseconds = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (timeoutMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));

        long started = Stopwatch.GetTimestamp();
        bool changed = TryAcquireFrame(
            _basePixels == null ? Math.Max(timeoutMilliseconds, 1000) : timeoutMilliseconds);

        if (_basePixels == null || _outputPixels == null)
            throw new InvalidOperationException("DXGI duplication has not produced an initial frame.");

        Buffer.BlockCopy(_basePixels, 0, _outputPixels, 0, _basePixels.Length);
        DrawCursor(_outputPixels, _width, _height, _width * 4, _desktopBounds);

        return new DxgiCapturedFrame(
            _outputPixels,
            _width,
            _height,
            _width * 4,
            changed,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private bool TryAcquireFrame(int timeoutMilliseconds)
    {
        var result = _duplication.AcquireNextFrame(
            (uint)timeoutMilliseconds,
            out _,
            out IDXGIResource resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout)
            return false;

        result.CheckError();
        try
        {
            using ID3D11Texture2D source = resource.QueryInterface<ID3D11Texture2D>();
            EnsureBuffers(source.Description);
            _context.CopyResource(_staging!, source);

            MappedSubresource mapped = _context.Map(
                _staging!,
                0,
                MapMode.Read,
                Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes = _width * 4;
                for (int y = 0; y < _height; y++)
                {
                    Marshal.Copy(
                        mapped.DataPointer + y * (int)mapped.RowPitch,
                        _basePixels!,
                        y * rowBytes,
                        rowBytes);
                }
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }

            return true;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame().CheckError();
        }
    }

    private void EnsureBuffers(Texture2DDescription description)
    {
        int width = checked((int)description.Width);
        int height = checked((int)description.Height);
        if (_staging != null && width == _width && height == _height)
            return;

        _staging?.Dispose();
        _width = width;
        _height = height;
        var stagingDescription = new Texture2DDescription
        {
            Width = description.Width,
            Height = description.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = description.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };
        _staging = _device.CreateTexture2D(stagingDescription);
        _basePixels = new byte[checked(width * height * 4)];
        _outputPixels = new byte[_basePixels.Length];
    }

    private static void DrawCursor(
        byte[] pixels,
        int width,
        int height,
        int stride,
        Rectangle desktopBounds)
    {
        var cursorInfo = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursorInfo) ||
            (cursorInfo.Flags & CursorShowing) == 0 ||
            !desktopBounds.Contains(cursorInfo.ScreenPosition.X, cursorInfo.ScreenPosition.Y) ||
            !GetIconInfo(cursorInfo.CursorHandle, out IconInfo iconInfo))
        {
            return;
        }

        GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                width,
                height,
                stride,
                PixelFormat.Format32bppArgb,
                pinned.AddrOfPinnedObject());
            using Graphics graphics = Graphics.FromImage(bitmap);
            IntPtr hdc = graphics.GetHdc();
            try
            {
                DrawIconEx(
                    hdc,
                    cursorInfo.ScreenPosition.X - desktopBounds.Left - (int)iconInfo.HotspotX,
                    cursorInfo.ScreenPosition.Y - desktopBounds.Top - (int)iconInfo.HotspotY,
                    cursorInfo.CursorHandle,
                    0,
                    0,
                    0,
                    IntPtr.Zero,
                    DrawNormal);
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }
        finally
        {
            pinned.Free();
            if (iconInfo.MaskBitmap != IntPtr.Zero)
                DeleteObject(iconInfo.MaskBitmap);
            if (iconInfo.ColorBitmap != IntPtr.Zero)
                DeleteObject(iconInfo.ColorBitmap);
        }
    }

    private static void FindOutputByName(
        IDXGIFactory1 factory,
        string deviceName,
        out IDXGIAdapter1 selectedAdapter,
        out IDXGIOutput1 selectedOutput)
    {
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Failure)
                break;

            for (uint outputIndex = 0; ; outputIndex++)
            {
                if (adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Failure)
                    break;

                if (output.Description.DeviceName.Equals(
                    deviceName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    selectedAdapter = adapter;
                    selectedOutput = output.QueryInterface<IDXGIOutput1>();
                    output.Dispose();
                    return;
                }

                output.Dispose();
            }

            adapter.Dispose();
        }

        throw new InvalidOperationException($"Could not find DXGI output {deviceName}.");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _staging?.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
        _output.Dispose();
        _adapter.Dispose();
        _factory.Dispose();
    }

    private const int CursorShowing = 1;
    private const uint DrawNormal = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X; internal int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        internal int Size;
        internal int Flags;
        internal IntPtr CursorHandle;
        internal Point ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] internal bool IsIcon;
        internal uint HotspotX;
        internal uint HotspotY;
        internal IntPtr MaskBitmap;
        internal IntPtr ColorBitmap;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr iconHandle, out IconInfo iconInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(
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
    private static extern bool DeleteObject(IntPtr objectHandle);
}

public readonly record struct DxgiCapturedFrame(
    byte[] Bgra,
    int Width,
    int Height,
    int Stride,
    bool DesktopChanged,
    double CaptureMs);
