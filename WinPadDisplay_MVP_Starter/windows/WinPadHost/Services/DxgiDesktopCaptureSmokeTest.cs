using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace WinPadHost.Services;

public static class DxgiDesktopCaptureSmokeTest
{
    public static string Run(int requestedFrames = 120)
    {
        if (requestedFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestedFrames));

        var screen = System.Windows.Forms.Screen.AllScreens
            .FirstOrDefault(candidate => !candidate.Primary)
            ?? throw new InvalidOperationException("No secondary display found.");

        using IDXGIFactory1 factory = CreateDXGIFactory1<IDXGIFactory1>();
        FindOutput(factory, screen.Bounds, out IDXGIAdapter1 adapter, out IDXGIOutput1 output);
        D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            null,
            out ID3D11Device? createdDevice).CheckError();
        ID3D11Device device = createdDevice
            ?? throw new InvalidOperationException("D3D11CreateDevice returned no device.");
        using (adapter)
        using (output)
        using (device)
        using (ID3D11DeviceContext context = device.ImmediateContext)
        using (IDXGIOutputDuplication duplication = output.DuplicateOutput(device))
        {
            ID3D11Texture2D? staging = null;
            byte[]? pixels = null;
            int capturedFrames = 0;
            int timeouts = 0;
            double acquireMilliseconds = 0;
            double copyMilliseconds = 0;
            var total = Stopwatch.StartNew();

            try
            {
                while (capturedFrames < requestedFrames &&
                       total.Elapsed < TimeSpan.FromSeconds(15))
                {
                    long acquireStarted = Stopwatch.GetTimestamp();
                    var result = duplication.AcquireNextFrame(
                        250,
                        out _,
                        out IDXGIResource resource);
                    acquireMilliseconds += Stopwatch.GetElapsedTime(
                        acquireStarted).TotalMilliseconds;

                    if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                    {
                        timeouts++;
                        continue;
                    }

                    result.CheckError();
                    try
                    {
                        using ID3D11Texture2D source = resource.QueryInterface<ID3D11Texture2D>();
                        Texture2DDescription sourceDescription = source.Description;

                        if (staging == null)
                        {
                            var stagingDescription = new Texture2DDescription
                            {
                                Width = sourceDescription.Width,
                                Height = sourceDescription.Height,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = sourceDescription.Format,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Staging,
                                BindFlags = BindFlags.None,
                                CPUAccessFlags = CpuAccessFlags.Read,
                                MiscFlags = ResourceOptionFlags.None
                            };
                            staging = device.CreateTexture2D(stagingDescription);
                            pixels = new byte[
                                checked((int)sourceDescription.Width) *
                                checked((int)sourceDescription.Height) * 4];
                        }

                        long copyStarted = Stopwatch.GetTimestamp();
                        context.CopyResource(staging, source);
                        MappedSubresource mapped = context.Map(
                            staging,
                            0,
                            MapMode.Read,
                            Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            int width = checked((int)sourceDescription.Width);
                            int height = checked((int)sourceDescription.Height);
                            int rowBytes = width * 4;
                            for (int y = 0; y < height; y++)
                            {
                                Marshal.Copy(
                                    mapped.DataPointer + y * (int)mapped.RowPitch,
                                    pixels!,
                                    y * rowBytes,
                                    rowBytes);
                            }
                        }
                        finally
                        {
                            context.Unmap(staging, 0);
                        }

                        copyMilliseconds += Stopwatch.GetElapsedTime(
                            copyStarted).TotalMilliseconds;
                        capturedFrames++;
                    }
                    finally
                    {
                        resource.Dispose();
                        duplication.ReleaseFrame().CheckError();
                    }
                }
            }
            finally
            {
                staging?.Dispose();
            }

            if (capturedFrames == 0)
                throw new InvalidOperationException("DXGI duplication produced no desktop frames.");

            return $"DXGI capture: {output.Description.DeviceName}, " +
                   $"{screen.Bounds.Width}x{screen.Bounds.Height}, " +
                   $"frames={capturedFrames}, timeouts={timeouts}, " +
                   $"avg acquire call={acquireMilliseconds / (capturedFrames + timeouts):F2} ms, " +
                   $"avg copy/map={copyMilliseconds / capturedFrames:F2} ms, " +
                   $"elapsed={total.ElapsedMilliseconds} ms.";
        }
    }

    private static void FindOutput(
        IDXGIFactory1 factory,
        System.Drawing.Rectangle targetBounds,
        out IDXGIAdapter1 selectedAdapter,
        out IDXGIOutput1 selectedOutput)
    {
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapterResult = factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter);
            if (adapterResult.Failure)
                break;

            for (uint outputIndex = 0; ; outputIndex++)
            {
                var outputResult = adapter.EnumOutputs(outputIndex, out IDXGIOutput output);
                if (outputResult.Failure)
                    break;

                OutputDescription description = output.Description;
                var bounds = description.DesktopCoordinates;
                bool matches =
                    bounds.Left == targetBounds.Left &&
                    bounds.Top == targetBounds.Top &&
                    bounds.Right - bounds.Left == targetBounds.Width &&
                    bounds.Bottom - bounds.Top == targetBounds.Height;

                if (matches)
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

        throw new InvalidOperationException("Could not match the secondary display to a DXGI output.");
    }
}
