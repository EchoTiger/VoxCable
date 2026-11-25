using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VoxCable.Audio;

/// <summary>
/// Captures audio exclusively from a single target Windows process tree (e.g. Spotify, Chrome, or a Game)
/// using Windows 10 Build 20348+ / Windows 11 WASAPI Process Loopback (<c>VAD\Process_Loopback</c>).
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private const uint AudioClientActivationTypeProcessLoopback = 1;
    private const uint ProcessLoopbackModeIncludeTree = 0;
    private const uint AudclntStreamFlagsLoopback = 0x00020000;
    private const uint AudclntStreamFlagsEventCallback = 0x00040000;
    private const uint AudclntBufferFlagsSilent = 0x00000002;
    private const int AudclntShareModeShared = 0;
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly uint _targetProcessId;
    private readonly int _sampleRate;
    private readonly CancellationTokenSource _cts = new();

    private IAudioClientNative? _audioClient;
    private IAudioCaptureClientNative? _captureClient;
    private AutoResetEvent? _sampleReadyEvent;
    private Thread? _captureThread;
    private volatile bool _isCapturing;
    private bool _disposed;

    public WaveFormat WaveFormat { get; }

    public event Action<byte[], int>? DataAvailable;
    public event Action<Exception?>? CaptureStopped;

    public static bool IsSupportedOnCurrentOs => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);

    public ProcessLoopbackCapture(uint targetProcessId, int sampleRate = 48000)
    {
        if (!IsSupportedOnCurrentOs)
            throw new PlatformNotSupportedException("Per-app Process Loopback requires Windows 10 Build 20348 or Windows 11.");
        if (targetProcessId == 0)
            throw new ArgumentOutOfRangeException(nameof(targetProcessId));

        _targetProcessId = targetProcessId;
        _sampleRate = Math.Clamp(sampleRate, 8000, 192000);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 2);
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProcessLoopbackCapture));
        if (_isCapturing) return;

        _audioClient = ActivateProcessLoopbackClient(_targetProcessId);

        var fmt = new WaveFormatExNative
        {
            wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
            nChannels = 2,
            nSamplesPerSec = (uint)_sampleRate,
            wBitsPerSample = 32,
            nBlockAlign = 8,
            nAvgBytesPerSec = (uint)(_sampleRate * 8),
            cbSize = 0
        };

        IntPtr fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExNative>());
        try
        {
            Marshal.StructureToPtr(fmt, fmtPtr, false);
            const long bufferDuration100Ns = 200_000; // 20 ms
            int hr = _audioClient.Initialize(
                AudclntShareModeShared,
                AudclntStreamFlagsLoopback | AudclntStreamFlagsEventCallback,
                bufferDuration100Ns,
                0,
                fmtPtr,
                IntPtr.Zero);
            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            Marshal.FreeHGlobal(fmtPtr);
        }

        _sampleReadyEvent = new AutoResetEvent(false);
        Marshal.ThrowExceptionForHR(_audioClient.SetEventHandle(_sampleReadyEvent.SafeWaitHandle.DangerousGetHandle()));

        Marshal.ThrowExceptionForHR(_audioClient.GetService(IidAudioCaptureClient, out object svc));
        _captureClient = (IAudioCaptureClientNative)svc;

        Marshal.ThrowExceptionForHR(_audioClient.Start());
        _isCapturing = true;

        _captureThread = new Thread(CaptureLoop)
        {
            Name = $"VoxCable.ProcessLoopback.{_targetProcessId}",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _captureThread.Start();
    }

    private void CaptureLoop()
    {
        Exception? fatalError = null;
        byte[] packetBuf = new byte[WaveFormat.AverageBytesPerSecond / 10]; // 100 ms scratch

        using var mmcss = MmcssHelper.EnterProAudioThread();
        try
        {
            var token = _cts.Token;
            var waitHandles = new WaitHandle[] { _sampleReadyEvent!, token.WaitHandle };

            while (!token.IsCancellationRequested && _captureClient != null)
            {
                int signaled = WaitHandle.WaitAny(waitHandles, 40);
                if (signaled == 1 || token.IsCancellationRequested)
                    break;

                DrainPackets(ref packetBuf);
            }
        }
        catch (Exception ex)
        {
            fatalError = ex;
        }
        finally
        {
            _isCapturing = false;
            try { _audioClient?.Stop(); } catch { }
            CaptureStopped?.Invoke(fatalError);
        }
    }

    private void DrainPackets(ref byte[] packetBuf)
    {
        if (_captureClient == null) return;

        while (true)
        {
            int hr = _captureClient.GetNextPacketSize(out uint packetFrames);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (packetFrames == 0) break;

            hr = _captureClient.GetBuffer(out IntPtr dataPtr, out uint numFrames, out uint flags, out _, out _);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);

            try
            {
                int bytes = (int)numFrames * WaveFormat.BlockAlign;
                if (bytes > 0)
                {
                    if (packetBuf.Length < bytes)
                    {
                        packetBuf = new byte[bytes];
                    }

                    if ((flags & AudclntBufferFlagsSilent) != 0 || dataPtr == IntPtr.Zero)
                    {
                        Array.Clear(packetBuf, 0, bytes);
                    }
                    else
                    {
                        Marshal.Copy(dataPtr, packetBuf, 0, bytes);
                    }

                    DataAvailable?.Invoke(packetBuf, bytes);
                }
            }
            finally
            {
                _captureClient.ReleaseBuffer(numFrames);
            }
        }
    }

    private static IAudioClientNative ActivateProcessLoopbackClient(uint pid)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationTypeProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = pid,
                ProcessLoopbackMode = ProcessLoopbackModeIncludeTree
            }
        };

        int blobSize = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr blobPtr = Marshal.AllocHGlobal(blobSize);
        try
        {
            Marshal.StructureToPtr(activationParams, blobPtr, false);

            var propVar = new PropVariantBlob
            {
                vt = 65, // VT_BLOB
                blob = new BlobNative
                {
                    cbSize = (uint)blobSize,
                    pBlobData = blobPtr
                }
            };

            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                IidAudioClient,
                ref propVar,
                handler,
                out var asyncOp);

            try
            {
                if (!handler.Wait(TimeSpan.FromSeconds(3)))
                    throw new TimeoutException("Timed out activating Windows Process Loopback audio interface.");

                return handler.GetClient();
            }
            finally
            {
                if (asyncOp != null)
                {
                    Marshal.ReleaseComObject(asyncOp);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public void Stop()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }

        if (_captureThread != null && Thread.CurrentThread != _captureThread)
        {
            _captureThread.Join(1000);
            _captureThread = null;
        }

        if (_captureClient != null)
        {
            try { Marshal.ReleaseComObject(_captureClient); } catch { }
            _captureClient = null;
        }

        if (_audioClient != null)
        {
            try { _audioClient.Stop(); } catch { }
            try { Marshal.ReleaseComObject(_audioClient); } catch { }
            _audioClient = null;
        }

        _sampleReadyEvent?.Dispose();
        _sampleReadyEvent = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts.Dispose();
    }

    #region Native COM Interop

    [DllImport("mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [In, MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [In] ref PropVariantBlob activationParams,
        [In] IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public uint ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlobNative
    {
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public BlobNative blob;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExNative
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject
    {
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClientNative
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService([In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClientNative
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly ManualResetEventSlim _done = new(false);
        private int _hr;
        private IAudioClientNative? _client;
        private Exception? _exception;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out _hr, out object activated);
                if (_hr >= 0 && activated != null)
                {
                    _client = (IAudioClientNative)activated;
                }
            }
            catch (Exception ex)
            {
                _exception = ex;
            }
            finally
            {
                _done.Set();
            }
        }

        public bool Wait(TimeSpan timeout) => _done.Wait(timeout);

        public IAudioClientNative GetClient()
        {
            if (_exception != null) throw _exception;
            if (_hr < 0) Marshal.ThrowExceptionForHR(_hr);
            return _client ?? throw new InvalidOperationException("Process Loopback activation returned null IAudioClient.");
        }
    }

    #endregion
}
