using System.Collections.Concurrent;

namespace WinPadHost.Services;

internal sealed class DxgiCaptureWorker : IDisposable
{
    private readonly BlockingCollection<CaptureRequest> _requests = new();
    private readonly TaskCompletionSource<bool> _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly string _deviceName;
    private bool _disposed;

    public DxgiCaptureWorker(string deviceName)
    {
        _deviceName = deviceName;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "WinPad DXGI capture"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        _started.Task.GetAwaiter().GetResult();
    }

    public Task<DxgiCapturedFrame> CaptureAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<DxgiCapturedFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Add(new CaptureRequest(completion));
        return completion.Task;
    }

    private void Run()
    {
        try
        {
            using var capture = new DxgiDesktopCapture(_deviceName);
            _started.TrySetResult(true);

            foreach (CaptureRequest request in _requests.GetConsumingEnumerable())
            {
                try
                {
                    request.Completion.TrySetResult(capture.Capture());
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
            while (_requests.TryTake(out CaptureRequest? request))
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

    private sealed record CaptureRequest(
        TaskCompletionSource<DxgiCapturedFrame> Completion);
}
