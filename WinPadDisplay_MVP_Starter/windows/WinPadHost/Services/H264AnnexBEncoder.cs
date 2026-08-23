using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace WinPadHost.Services;

public sealed class H264AnnexBEncoder : IDisposable
{
    private readonly IMFActivateCollection _activations;
    private readonly IMFActivate _activation;
    private readonly IMFTransform _encoder;
    private readonly OutputStreamInfo _streamInfo;
    private bool _completed;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int FramesPerSecond { get; }
    public int NV12FrameSize => Width * Height * 3 / 2;

    public H264AnnexBEncoder(int width, int height, int framesPerSecond = 30, int bitrate = 8_000_000)
    {
        if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(width), "H.264 dimensions must be positive and even.");
        if (framesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        if (bitrate <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitrate));

        Width = width;
        Height = height;
        FramesPerSecond = framesPerSecond;

        MediaFactory.MFStartup().CheckError();
        try
        {
            var outputInfo = new RegisterTypeInfo
            {
                GuidMajorType = MediaTypeGuids.Video,
                GuidSubtype = VideoFormatGuids.H264
            };

            _activations = MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                (uint)EnumFlag.EnumFlagSyncmft,
                null,
                outputInfo);
            _activation = _activations.FirstOrDefault()
                ?? throw new InvalidOperationException("No synchronous H.264 encoder found.");
            _encoder = _activation.ActivateObject<IMFTransform>();

            using IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            ConfigureVideoType(outputType, VideoFormatGuids.H264);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 77u);
            _encoder.SetOutputType(0, outputType, 0);

            using IMFMediaType inputType = MediaFactory.MFCreateMediaType();
            ConfigureVideoType(inputType, VideoFormatGuids.NV12);
            inputType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)Width);
            inputType.Set(MediaTypeAttributeKeys.SampleSize, (uint)NV12FrameSize);
            inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            _encoder.SetInputType(0, inputType, 0);

            _encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            _streamInfo = _encoder.GetOutputStreamInfo(0);
        }
        catch
        {
            MediaFactory.MFShutdown();
            throw;
        }
    }

    public IReadOnlyList<byte[]> EncodeFrame(byte[] nv12Frame, long sampleTime, long sampleDuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("The encoder has already been completed.");
        if (nv12Frame.Length != NV12FrameSize)
            throw new ArgumentException($"Expected {NV12FrameSize} NV12 bytes.", nameof(nv12Frame));

        using IMFSample inputSample = CreateSample(nv12Frame, sampleTime, sampleDuration);
        _encoder.ProcessInput(0, inputSample, 0);
        return DrainAvailableOutput();
    }

    public IReadOnlyList<byte[]> Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            return Array.Empty<byte[]>();

        _completed = true;
        _encoder.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _encoder.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        IReadOnlyList<byte[]> remaining = DrainAvailableOutput();
        _encoder.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
        return remaining;
    }

    private List<byte[]> DrainAvailableOutput()
    {
        var encodedSamples = new List<byte[]>();
        while (true)
        {
            IMFSample? callerSample = null;
            IMFMediaBuffer? callerBuffer = null;
            bool encoderProvidesSamples =
                (_streamInfo.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;

            if (!encoderProvidesSamples)
            {
                callerSample = MediaFactory.MFCreateSample();
                callerBuffer = MediaFactory.MFCreateMemoryBuffer(
                    Math.Max(_streamInfo.Size, 2 * 1024 * 1024));
                callerSample.AddBuffer(callerBuffer);
            }

            var outputBuffer = new OutputDataBuffer { StreamID = 0, Sample = callerSample! };
            var result = _encoder.ProcessOutput(
                ProcessOutputFlags.None, 1, ref outputBuffer, out _);

            if (result == ResultCode.TransformNeedMoreInput)
            {
                callerBuffer?.Dispose();
                callerSample?.Dispose();
                return encodedSamples;
            }

            result.CheckError();
            IMFSample sample = outputBuffer.Sample ?? callerSample
                ?? throw new InvalidOperationException("Encoder returned no output sample.");
            try
            {
                using IMFMediaBuffer contiguous = sample.ConvertToContiguousBuffer();
                encodedSamples.Add(CopyBuffer(contiguous));
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

    private IMFSample CreateSample(byte[] bytes, long time, long duration)
    {
        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(bytes.Length);
        buffer.Lock(out IntPtr destination, out _, out _);
        try { Marshal.Copy(bytes, 0, destination, bytes.Length); }
        finally { buffer.Unlock(); }

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
        try { Marshal.Copy(source, bytes, 0, length); }
        finally { buffer.Unlock(); }
        return bytes;
    }

    private void ConfigureVideoType(IMFMediaType mediaType, Guid subtype)
    {
        mediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        mediaType.Set(MediaTypeAttributeKeys.Subtype, subtype);
        mediaType.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)Width, (uint)Height));
        mediaType.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)FramesPerSecond, 1));
        mediaType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        mediaType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u);
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (!_completed) Complete();
        }
        finally
        {
            _disposed = true;
            _encoder.Dispose();
            _activation.Dispose();
            _activations.Dispose();
            MediaFactory.MFShutdown();
        }
    }
}
