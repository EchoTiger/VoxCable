namespace VoxCable.Platform;

/// <summary>
/// Ensures only one interactive VoxCable audio bridge instance runs per Windows user session.
/// Launching a second instance signals the running instance to restore and focus its window.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\VoxCable_SingleInstance_Mutex_v2";
    private const string SignalEventName = @"Local\VoxCable_Activate_Event_v2";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _listenerThread;
    private bool _disposed;

    public bool IsPrimaryInstance { get; }

    public event Action? ActivationRequested;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsPrimaryInstance = createdNew;
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SignalEventName);

        if (createdNew)
        {
            _listenerThread = new Thread(ListenLoop)
            {
                Name = "VoxCable.SingleInstanceListener",
                IsBackground = true
            };
            _listenerThread.Start();
        }
    }

    public void SignalExistingInstance()
    {
        try
        {
            _activateEvent.Set();
        }
        catch
        {
            // Ignore if existing instance is exiting
        }
    }

    private void ListenLoop()
    {
        var handles = new WaitHandle[] { _activateEvent, _cts.Token.WaitHandle };
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                int idx = WaitHandle.WaitAny(handles);
                if (idx == 0)
                {
                    ActivationRequested?.Invoke();
                }
                else
                {
                    break;
                }
            }
            catch
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _activateEvent.Set(); } catch { }
        _listenerThread?.Join(500);
        if (IsPrimaryInstance)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
        _activateEvent.Dispose();
        _cts.Dispose();
    }
}
