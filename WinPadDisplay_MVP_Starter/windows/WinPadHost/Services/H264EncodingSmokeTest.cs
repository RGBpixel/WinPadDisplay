using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace WinPadHost.Services;

public static class H264EncodingSmokeTest
{
    private const int Width = 1920;
    private const int Height = 1440;
    private const int FramesPerSecond = 30;
    private const int FrameCount = FramesPerSecond;
    private const long HnsPerSecond = 10_000_000;

    public static string Run(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var startup = MediaFactory.MFStartup();
        startup.CheckError();

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using IMFAttributes writerAttributes = MediaFactory.MFCreateAttributes(1);
            writerAttributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);

            using IMFSinkWriter writer = MediaFactory.MFCreateSinkWriterFromURL(
                Path.GetFullPath(outputPath), null, writerAttributes);

            using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            ConfigureVideoType(outputType, VideoFormatGuids.H264);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, 8_000_000u);

            int streamIndex = writer.AddStream(outputType);

            using IMFMediaType inputType = MediaFactory.MFCreateMediaType();
            ConfigureVideoType(inputType, VideoFormatGuids.Rgb32);
            inputType.Set(MediaTypeAttributeKeys.DefaultStride, unchecked((uint)(Width * 4)));

            writer.SetInputMediaType(streamIndex, inputType, null);
            writer.BeginWriting();

            byte[] frame = new byte[Width * Height * 4];
            long frameDuration = HnsPerSecond / FramesPerSecond;

            for (int frameNumber = 0; frameNumber < FrameCount; frameNumber++)
            {
                FillTestFrame(frame, frameNumber);
                WriteFrame(writer, streamIndex, frame, frameNumber * frameDuration, frameDuration);
            }

            writer.Finalize();
            stopwatch.Stop();

            var file = new FileInfo(outputPath);
            return $"H.264 smoke test succeeded: {file.FullName}, " +
                   $"{FrameCount} frames, {file.Length:N0} bytes, {stopwatch.ElapsedMilliseconds} ms.";
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    public static string RunDesktopCapture(string outputPath)
    {
        var screen = System.Windows.Forms.Screen.AllScreens
            .FirstOrDefault(candidate => !candidate.Primary)
            ?? throw new InvalidOperationException("No secondary display found.");

        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var startup = MediaFactory.MFStartup();
        startup.CheckError();

        var stopwatch = Stopwatch.StartNew();
        double captureMilliseconds = 0;
        double writeMilliseconds = 0;

        try
        {
            using IMFAttributes writerAttributes = MediaFactory.MFCreateAttributes(1);
            writerAttributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);

            using IMFSinkWriter writer = MediaFactory.MFCreateSinkWriterFromURL(
                Path.GetFullPath(outputPath), null, writerAttributes);

            using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            ConfigureVideoType(outputType, VideoFormatGuids.H264);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, 8_000_000u);
            int streamIndex = writer.AddStream(outputType);

            using IMFMediaType inputType = MediaFactory.MFCreateMediaType();
            ConfigureVideoType(inputType, VideoFormatGuids.Rgb32);
            inputType.Set(MediaTypeAttributeKeys.DefaultStride, unchecked((uint)(Width * 4)));
            writer.SetInputMediaType(streamIndex, inputType, null);
            writer.BeginWriting();

            byte[] frame = new byte[Width * Height * 4];
            long frameDuration = HnsPerSecond / FramesPerSecond;
            long nextFrameAt = Stopwatch.GetTimestamp();

            using var capturedBitmap = new Bitmap(
                screen.Bounds.Width,
                screen.Bounds.Height,
                PixelFormat.Format32bppRgb);
            using Graphics captureGraphics = Graphics.FromImage(capturedBitmap);
            using var encodedBitmap = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
            using Graphics resizeGraphics = Graphics.FromImage(encodedBitmap);

            for (int frameNumber = 0; frameNumber < FrameCount; frameNumber++)
            {
                WaitUntil(nextFrameAt);
                nextFrameAt += Stopwatch.Frequency / FramesPerSecond;

                long captureStarted = Stopwatch.GetTimestamp();
                captureGraphics.CopyFromScreen(
                    screen.Bounds.Left,
                    screen.Bounds.Top,
                    0,
                    0,
                    screen.Bounds.Size,
                    CopyPixelOperation.SourceCopy);

                if (capturedBitmap.Size == encodedBitmap.Size)
                {
                    CopyBitmapPixels(capturedBitmap, frame);
                }
                else
                {
                    resizeGraphics.DrawImage(
                        capturedBitmap,
                        new Rectangle(0, 0, Width, Height));
                    CopyBitmapPixels(encodedBitmap, frame);
                }
                captureMilliseconds += Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;

                long writeStarted = Stopwatch.GetTimestamp();
                WriteFrame(writer, streamIndex, frame, frameNumber * frameDuration, frameDuration);
                writeMilliseconds += Stopwatch.GetElapsedTime(writeStarted).TotalMilliseconds;
            }

            writer.Finalize();
            stopwatch.Stop();

            var file = new FileInfo(outputPath);
            return $"H.264 desktop capture succeeded: {screen.DeviceName} " +
                   $"source {screen.Bounds.Width}x{screen.Bounds.Height}, " +
                   $"encoded {Width}x{Height}, {FrameCount} frames, {file.Length:N0} bytes, " +
                   $"elapsed {stopwatch.ElapsedMilliseconds} ms, " +
                   $"avg capture {captureMilliseconds / FrameCount:F1} ms, " +
                   $"avg encode/write {writeMilliseconds / FrameCount:F1} ms.";
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    private static void ConfigureVideoType(IMFMediaType mediaType, Guid subtype)
    {
        mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        mediaType.Set(MediaTypeAttributeKeys.Subtype, subtype);
        mediaType.Set(MediaTypeAttributeKeys.FrameSize, Pack(Width, Height));
        mediaType.Set(MediaTypeAttributeKeys.FrameRate, Pack(FramesPerSecond, 1));
        mediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        mediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    private static void WriteFrame(
        IMFSinkWriter writer,
        int streamIndex,
        byte[] frame,
        long sampleTime,
        long sampleDuration)
    {
        using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(frame.Length);
        buffer.Lock(out IntPtr destination, out _, out _);
        try
        {
            Marshal.Copy(frame, 0, destination, frame.Length);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = frame.Length;

        using IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = sampleTime;
        sample.SampleDuration = sampleDuration;
        writer.WriteSample(streamIndex, sample);
    }

    private static void FillTestFrame(byte[] frame, int frameNumber)
    {
        int movingBarLeft = frameNumber * Width / FrameCount;

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width * 4;
            byte green = (byte)(y * 255 / Height);

            for (int x = 0; x < Width; x++)
            {
                int offset = row + x * 4;
                bool inMovingBar = x >= movingBarLeft && x < movingBarLeft + 96;
                frame[offset] = inMovingBar ? (byte)32 : (byte)(x * 255 / Width);
                frame[offset + 1] = inMovingBar ? (byte)220 : green;
                frame[offset + 2] = inMovingBar ? (byte)255 : (byte)48;
                frame[offset + 3] = 255;
            }
        }
    }

    private static void CopyBitmapPixels(Bitmap bitmap, byte[] destination)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(
            rectangle,
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);

        try
        {
            int rowBytes = bitmap.Width * 4;
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, destination, 0, destination.Length);
                return;
            }

            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr sourceRow = data.Scan0 + y * data.Stride;
                Marshal.Copy(sourceRow, destination, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void WaitUntil(long targetTimestamp)
    {
        while (true)
        {
            TimeSpan remaining = Stopwatch.GetElapsedTime(
                Stopwatch.GetTimestamp(), targetTimestamp);
            if (remaining <= TimeSpan.Zero)
                return;

            if (remaining > TimeSpan.FromMilliseconds(2))
                Thread.Sleep(1);
            else
                Thread.SpinWait(100);
        }
    }
}
