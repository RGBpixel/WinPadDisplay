using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace WinPadHost.Services;

public static class H264AnnexBEncoderSmokeTest
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

        MediaFactory.MFStartup().CheckError();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var outputInfo = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.H264
            };

            using IMFActivateCollection activations = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                (uint)EnumFlag.EnumFlagSyncmft,
                null,
                outputInfo);

            using IMFActivate activation = activations.FirstOrDefault()
                ?? throw new InvalidOperationException("No synchronous H.264 encoder found.");
            using IMFTransform encoder = activation.ActivateObject<IMFTransform>();

            using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            ConfigureVideoType(outputType, VideoFormatGuids.H264);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, 8_000_000u);
            outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 77u);
            encoder.SetOutputType(0, outputType, 0);

            using IMFMediaType inputType = MediaFactory.MFCreateMediaType();
            ConfigureVideoType(inputType, VideoFormatGuids.NV12);
            inputType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)Width);
            inputType.Set(MediaTypeAttributeKeys.SampleSize, (uint)(Width * Height * 3 / 2));
            inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            encoder.SetInputType(0, inputType, 0);

            encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            OutputStreamInfo streamInfo = encoder.GetOutputStreamInfo(0);
            byte[] nv12 = new byte[Width * Height * 3 / 2];
            long frameDuration = HnsPerSecond / FramesPerSecond;
            int encodedSamples = 0;
            int startCodes = 0;

            using var output = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read);

            for (int frameNumber = 0; frameNumber < FrameCount; frameNumber++)
            {
                FillNV12Frame(nv12, frameNumber);
                using IMFSample inputSample = CreateSample(
                    nv12,
                    frameNumber * frameDuration,
                    frameDuration);

                encoder.ProcessInput(0, inputSample, 0);
                DrainAvailableOutput(
                    encoder,
                    streamInfo,
                    output,
                    ref encodedSamples,
                    ref startCodes);
            }

            encoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            encoder.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            DrainAvailableOutput(
                encoder,
                streamInfo,
                output,
                ref encodedSamples,
                ref startCodes);

            encoder.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            output.Flush();
            stopwatch.Stop();

            return $"H.264 Annex-B smoke test succeeded: {encodedSamples} samples, " +
                   $"{startCodes} start codes, {output.Length:N0} bytes, " +
                   $"{stopwatch.ElapsedMilliseconds} ms, {Path.GetFullPath(outputPath)}.";
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    public static string RunReusableEncoder(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var stopwatch = Stopwatch.StartNew();
        byte[] nv12 = new byte[Width * Height * 3 / 2];
        long frameDuration = HnsPerSecond / FramesPerSecond;
        int encodedSamples = 0;
        int startCodes = 0;

        using var encoder = new H264AnnexBEncoder(Width, Height, FramesPerSecond);
        using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);

        for (int frameNumber = 0; frameNumber < FrameCount; frameNumber++)
        {
            FillNV12Frame(nv12, frameNumber);
            WriteEncodedSamples(
                encoder.EncodeFrame(
                    nv12,
                    frameNumber * frameDuration,
                    frameDuration),
                output,
                ref encodedSamples,
                ref startCodes);
        }

        WriteEncodedSamples(
            encoder.Complete(),
            output,
            ref encodedSamples,
            ref startCodes);
        output.Flush();
        stopwatch.Stop();

        return $"Reusable H.264 encoder succeeded: {encodedSamples} samples, " +
               $"{startCodes} start codes, {output.Length:N0} bytes, " +
               $"{stopwatch.ElapsedMilliseconds} ms, {Path.GetFullPath(outputPath)}.";
    }

    public static string RunBgraPipeline(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var stopwatch = Stopwatch.StartNew();
        byte[] bgra = new byte[Width * Height * 4];
        byte[] nv12 = new byte[Width * Height * 3 / 2];
        long frameDuration = HnsPerSecond / FramesPerSecond;
        int encodedSamples = 0;
        int startCodes = 0;
        double conversionMilliseconds = 0;

        using var encoder = new H264AnnexBEncoder(Width, Height, FramesPerSecond);
        using var output = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read);

        for (int frameNumber = 0; frameNumber < FrameCount; frameNumber++)
        {
            FillBgraFrame(bgra, frameNumber);

            long conversionStarted = Stopwatch.GetTimestamp();
            BgraToNv12Converter.Convert(bgra, Width, Height, Width * 4, nv12);
            conversionMilliseconds += Stopwatch.GetElapsedTime(
                conversionStarted).TotalMilliseconds;

            WriteEncodedSamples(
                encoder.EncodeFrame(
                    nv12,
                    frameNumber * frameDuration,
                    frameDuration),
                output,
                ref encodedSamples,
                ref startCodes);
        }

        WriteEncodedSamples(
            encoder.Complete(),
            output,
            ref encodedSamples,
            ref startCodes);
        output.Flush();
        stopwatch.Stop();

        return $"BGRA-to-H.264 pipeline succeeded: {encodedSamples} samples, " +
               $"{startCodes} start codes, {output.Length:N0} bytes, " +
               $"avg BGRA-to-NV12 {conversionMilliseconds / FrameCount:F1} ms, " +
               $"total {stopwatch.ElapsedMilliseconds} ms, {Path.GetFullPath(outputPath)}.";
    }

    public static async Task<string> RunThreadedPipelineAsync(
        string outputPath,
        int testFrameCount = FrameCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (testFrameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(testFrameCount));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var stopwatch = Stopwatch.StartNew();
        byte[] nv12 = new byte[Width * Height * 3 / 2];
        long frameDuration = HnsPerSecond / FramesPerSecond;
        int encodedSamples = 0;
        int startCodes = 0;

        using var encoder = new H264EncoderWorker(Width, Height, FramesPerSecond);
        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);

        for (int frameNumber = 0; frameNumber < testFrameCount; frameNumber++)
        {
            FillNV12Frame(nv12, frameNumber);
            WriteEncodedSamples(
                await encoder.EncodeFrameAsync(
                    nv12,
                    frameNumber * frameDuration,
                    frameDuration),
                output,
                ref encodedSamples,
                ref startCodes);
            await Task.Yield();
        }

        output.Flush();
        stopwatch.Stop();
        return $"Threaded H.264 pipeline succeeded: {encodedSamples} samples, " +
               $"{startCodes} start codes, {output.Length:N0} bytes, " +
               $"{stopwatch.ElapsedMilliseconds} ms, {Path.GetFullPath(outputPath)}.";
    }

    private static void WriteEncodedSamples(
        IReadOnlyList<byte[]> samples,
        Stream destination,
        ref int encodedSamples,
        ref int startCodes)
    {
        foreach (byte[] sample in samples)
        {
            destination.Write(sample);
            encodedSamples++;
            startCodes += CountAnnexBStartCodes(sample);
        }
    }

    private static void DrainAvailableOutput(
        IMFTransform encoder,
        OutputStreamInfo streamInfo,
        Stream destination,
        ref int encodedSamples,
        ref int startCodes)
    {
        while (true)
        {
            IMFSample? callerSample = null;
            IMFMediaBuffer? callerBuffer = null;

            bool encoderProvidesSamples =
                (streamInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;

            if (!encoderProvidesSamples)
            {
                callerSample = MediaFactory.MFCreateSample();
                callerBuffer = MediaFactory.MFCreateMemoryBuffer(
                    Math.Max(streamInfo.Size, 2 * 1024 * 1024));
                callerSample.AddBuffer(callerBuffer);
            }

            var outputBuffer = new OutputDataBuffer
            {
                StreamID = 0,
                Sample = callerSample!
            };
            var result = encoder.ProcessOutput(
                ProcessOutputFlags.None,
                1,
                ref outputBuffer,
                out _);

            if (result == ResultCode.TransformNeedMoreInput)
            {
                callerBuffer?.Dispose();
                callerSample?.Dispose();
                return;
            }

            result.CheckError();

            IMFSample sample = outputBuffer.Sample ?? callerSample
                ?? throw new InvalidOperationException("Encoder returned no output sample.");

            try
            {
                using IMFMediaBuffer contiguous = sample.ConvertToContiguousBuffer();
                byte[] bytes = CopyBuffer(contiguous);
                destination.Write(bytes);
                encodedSamples++;
                startCodes += CountAnnexBStartCodes(bytes);
            }
            finally
            {
                outputBuffer.Events?.Dispose();
                if (!ReferenceEquals(sample, callerSample))
                    sample.Dispose();
                callerBuffer?.Dispose();
                callerSample?.Dispose();
            }
        }
    }

    private static IMFSample CreateSample(byte[] bytes, long time, long duration)
    {
        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(bytes.Length);
        buffer.Lock(out IntPtr destination, out _, out _);
        try
        {
            Marshal.Copy(bytes, 0, destination, bytes.Length);
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = bytes.Length;
        IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        sample.SampleTime = time;
        sample.SampleDuration = duration;
        return sample;
    }

    private static byte[] CopyBuffer(IMFMediaBuffer buffer)
    {
        int length = buffer.CurrentLength;
        byte[] bytes = new byte[length];
        buffer.Lock(out IntPtr source, out _, out _);
        try
        {
            Marshal.Copy(source, bytes, 0, length);
        }
        finally
        {
            buffer.Unlock();
        }

        return bytes;
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

    private static void FillNV12Frame(byte[] frame, int frameNumber)
    {
        int movingBarLeft = frameNumber * Width / FrameCount;
        int yPlaneLength = Width * Height;

        for (int y = 0; y < Height; y++)
        {
            int row = y * Width;
            for (int x = 0; x < Width; x++)
            {
                frame[row + x] = x >= movingBarLeft && x < movingBarLeft + 96
                    ? (byte)220
                    : (byte)(32 + (x * 160 / Width));
            }
        }

        Array.Fill(frame, (byte)128, yPlaneLength, frame.Length - yPlaneLength);
    }

    private static void FillBgraFrame(byte[] frame, int frameNumber)
    {
        int movingBarLeft = frameNumber * Width / FrameCount;
        for (int y = 0; y < Height; y++)
        {
            int row = y * Width * 4;
            byte green = (byte)(y * 255 / Height);
            for (int x = 0; x < Width; x++)
            {
                int offset = row + x * 4;
                bool movingBar = x >= movingBarLeft && x < movingBarLeft + 96;
                frame[offset] = movingBar ? (byte)32 : (byte)(x * 255 / Width);
                frame[offset + 1] = movingBar ? (byte)220 : green;
                frame[offset + 2] = movingBar ? (byte)255 : (byte)48;
                frame[offset + 3] = 255;
            }
        }
    }

    private static int CountAnnexBStartCodes(byte[] bytes)
    {
        int count = 0;
        for (int index = 0; index + 3 < bytes.Length; index++)
        {
            if (bytes[index] == 0 && bytes[index + 1] == 0 &&
                (bytes[index + 2] == 1 ||
                 (bytes[index + 2] == 0 && bytes[index + 3] == 1)))
            {
                count++;
                index += bytes[index + 2] == 1 ? 2 : 3;
            }
        }

        return count;
    }
}
