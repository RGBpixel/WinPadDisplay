using System.Collections.Concurrent;

namespace WinPadHost.Services;

internal sealed class H264EncoderWorker : IDisposable
{
    private readonly BlockingCollection<EncodeRequest> _requests = new();
    private readonly Thread _thread;
    private readonly TaskCompletionSource<bool> _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _width;
    private readonly int _height;
    private readonly int _framesPerSecond;
    private bool _disposed;

    public int NV12FrameSize => _width * _height * 3 / 2;

    public H264EncoderWorker(int width, int height, int framesPerSecond)
    {
        _width = width;
        _height = height;
        _framesPerSecond = framesPerSecond;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WinPad H264 encoder"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _started.Task.GetAwaiter().GetResult();
    }

    public Task<IReadOnlyList<byte[]>> EncodeFrameAsync(
        byte[] nv12Frame,
        long sampleTime,
        long sampleDuration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<IReadOnlyList<byte[]>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Add(new EncodeRequest(
            nv12Frame,
            sampleTime,
            sampleDuration,
            completion));
        return completion.Task;
    }

    private void Run()
    {
        try
        {
            using var encoder = new H264AnnexBEncoder(
                _width,
                _height,
                _framesPerSecond);
            _started.TrySetResult(true);

            foreach (EncodeRequest request in _requests.GetConsumingEnumerable())
            {
                try
                {
                    request.Completion.TrySetResult(
                        encoder.EncodeFrame(
                            request.Frame,
                            request.SampleTime,
                            request.SampleDuration));
                }
                catch (Exception ex)
                {
                    request.Completion.TrySetException(ex);
                }
            }
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            while (_requests.TryTake(out EncodeRequest? request))
                request.Completion.TrySetException(ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _requests.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _requests.Dispose();
    }

    private sealed record EncodeRequest(
        byte[] Frame,
        long SampleTime,
        long SampleDuration,
        TaskCompletionSource<IReadOnlyList<byte[]>> Completion);
}
