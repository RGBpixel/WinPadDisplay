using System.Diagnostics;
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
}
