using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoxCable.Dsp;
using VoxCable.Soundboard;

namespace VoxCable.Audio;

/// <summary>
/// Full-duplex studio audio bridge supporting:
/// - Mic / Device Loopback primary input with adaptive clock-drift compensation
/// - Optional Windows 11 Per-App Process Loopback secondary input
/// - Primary Virtual Cable / Render output + optional independent Local Monitor ("Hear Myself") output
/// - Deadlock-free error recovery and MMCSS high-resolution timer boost
/// </summary>
public sealed class AudioBridgeEngine : IDisposable
{
    private readonly object _lifecycleLock = new();
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private WasapiOut? _monitorOutput;
    private ProcessLoopbackCapture? _appLoopback;
    private MMDevice? _inputDevice;
    private MMDevice? _outputDevice;
    private MMDevice? _monitorDevice;
    private LowLatencyRingWaveProvider? _ringBuffer;
    private LowLatencyRingWaveProvider? _appRingBuffer;
    private LowLatencyRingWaveProvider? _monitorRingBuffer;
    private DriftCompensatingSampleProvider? _driftCompensator;
    private DspChainSampleProvider? _dspChain;
    private IDisposable? _timerResolutionScope;

    private volatile bool _isRunning;
    private int _stoppingUnexpected;
    private int _sessionGeneration;

    public DspParameters Parameters { get; } = new();
    public SoundboardEngine Soundboard { get; } = new();

    public bool IsRunning => _isRunning;
    public int SampleRate { get; private set; } = 48000;
    public int InputChannelCount { get; private set; }
    public int OutputChannelCount { get; private set; }
    public bool IsMonitorActive => _monitorOutput != null && _isRunning;
    public bool IsAppLoopbackActive => _appLoopback != null && _isRunning;

    public string? ActiveInputId { get; private set; }
    public bool ActiveInputIsLoopback { get; private set; }
    public string? ActiveOutputId { get; private set; }
    public string? ActiveMonitorId { get; private set; }
    public uint? ActiveAppLoopbackPid { get; private set; }

    public double BufferedLatencyMs => _ringBuffer?.BufferedMilliseconds ?? 0.0;
    public long UnderrunCount => _ringBuffer?.UnderrunCount ?? 0;
    public long OverflowDropCount => _ringBuffer?.OverflowDropCount ?? 0;
    public float DriftRateRatio => _driftCompensator?.CurrentRateRatio ?? 1.0f;

    public float InputPeakL => _dspChain?.InputPeakL ?? 0f;
    public float InputPeakR => _dspChain?.InputPeakR ?? 0f;
    public float OutputPeakL => _dspChain?.OutputPeakL ?? 0f;
    public float OutputPeakR => _dspChain?.OutputPeakR ?? 0f;
    public bool InputClipping => _dspChain?.InputClipping ?? false;
    public bool OutputClipping => _dspChain?.OutputClipping ?? false;
    public bool NoiseGateOpen => _dspChain?.NoiseGateOpen ?? true;
    public float CompressorGainReductionDb => _dspChain?.CompressorGainReductionDb ?? 0f;

    public event Action<string>? EngineError;
    public event Action? StateChanged;

    public int CopyWaveformSnapshot(float[] destination)
    {
        var chain = _dspChain;
        return chain != null ? chain.CopyWaveformSnapshot(destination) : 0;
    }

    public void Start(
        AudioInputSource input,
        AudioOutputTarget output,
        int latencyMs = 40,
        AudioOutputTarget? monitorOutput = null,
        uint? appLoopbackProcessId = null)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (output == null) throw new ArgumentNullException(nameof(output));

        latencyMs = Math.Clamp(latencyMs, 15, 120);

        lock (_lifecycleLock)
        {
            StopInternal();
            int gen = Interlocked.Increment(ref _sessionGeneration);
            Interlocked.Exchange(ref _stoppingUnexpected, 0);

            try
            {
                _timerResolutionScope = MmcssHelper.RequestHighResTimer(1);

                using var enumerator = new MMDeviceEnumerator();
                _inputDevice = enumerator.GetDevice(input.Id);
                _outputDevice = enumerator.GetDevice(output.Id);

                var outputMixFormat = _outputDevice.AudioClient.MixFormat;
                int targetSampleRate = outputMixFormat.SampleRate;
                int targetChannels = outputMixFormat.Channels;

                SampleRate = targetSampleRate;
                OutputChannelCount = targetChannels;
                Soundboard.SetSampleRate(targetSampleRate);

                // Initialize primary capture
                int effectiveTargetMs = latencyMs;
                int effectiveMaxMs = latencyMs * 2 + 45;

                if (input.IsLoopback)
                {
                    _capture = new WasapiLoopbackCapture(_inputDevice);
                    // Windows WASAPI loopback delivers ~50ms bursts; ensure buffer ceiling won't drop packets
                    effectiveTargetMs = Math.Max(latencyMs, 45);
                    effectiveMaxMs = Math.Max(effectiveMaxMs, 140);
                }
                else
                {
                    try
                    {
                        int captureInterval = Math.Clamp(latencyMs / 2, 10, 30);
                        _capture = new WasapiCapture(_inputDevice, useEventSync: true, captureInterval);
                    }
                    catch
                    {
                        _capture = new WasapiCapture(_inputDevice);
                    }
                }

                InputChannelCount = _capture.WaveFormat.Channels;

                _ringBuffer = new LowLatencyRingWaveProvider(
                    _capture.WaveFormat,
                    targetLatencyMs: effectiveTargetMs,
                    maxLatencyMs: effectiveMaxMs,
                    enablePriming: true);

                var ringRef = _ringBuffer;
                _capture.DataAvailable += (_, e) =>
                {
                    if (e.BytesRecorded > 0)
                    {
                        ringRef.Write(e.Buffer, 0, e.BytesRecorded);
                    }
                };

                _capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null && _isRunning && gen == Volatile.Read(ref _sessionGeneration))
                    {
                        TriggerUnexpectedStop($"Input capture stopped: {AudioDeviceManager.TranslateAudioError(e.Exception)}", gen);
                    }
                };

                // Build primary input pull chain
                ISampleProvider sampleChain = _ringBuffer.ToSampleProvider();
                sampleChain = new ChannelNormalizerSampleProvider(sampleChain, () => Parameters.InputChannelMode);

                if (_capture.WaveFormat.SampleRate != targetSampleRate)
                {
                    sampleChain = new WdlResamplingSampleProvider(sampleChain, targetSampleRate);
                }

                _driftCompensator = new DriftCompensatingSampleProvider(sampleChain, _ringBuffer);
                sampleChain = _driftCompensator;

                // Optional Windows 11 Per-App Process Loopback secondary bus
                ISampleProvider? appSampleBus = null;
                if (appLoopbackProcessId is uint pid && pid > 0 && ProcessLoopbackCapture.IsSupportedOnCurrentOs)
                {
                    _appLoopback = new ProcessLoopbackCapture(pid, targetSampleRate);
                    _appRingBuffer = new LowLatencyRingWaveProvider(
                        _appLoopback.WaveFormat,
                        targetLatencyMs: Math.Max(latencyMs, 35),
                        maxLatencyMs: 140,
                        enablePriming: false);

                    var appRingRef = _appRingBuffer;
                    _appLoopback.DataAvailable += (buf, bytes) => appRingRef.Write(buf, 0, bytes);
                    appSampleBus = _appRingBuffer.ToSampleProvider();
                    ActiveAppLoopbackPid = pid;
                }

                // Optional Local Monitor ("Hear Myself") secondary output tee
                bool useMonitor = monitorOutput != null &&
                                  !string.Equals(monitorOutput.Id, output.Id, StringComparison.OrdinalIgnoreCase);

                Action<float[], int, int>? monitorTap = null;
                if (useMonitor && monitorOutput != null)
                {
                    _monitorDevice = enumerator.GetDevice(monitorOutput.Id);
                    var monMix = _monitorDevice.AudioClient.MixFormat;
                    var monStereoFormat = WaveFormat.CreateIeeeFloatWaveFormat(targetSampleRate, 2);
                    _monitorRingBuffer = new LowLatencyRingWaveProvider(
                        monStereoFormat,
                        targetLatencyMs: Math.Max(latencyMs, 30),
                        maxLatencyMs: 130,
                        enablePriming: true);

                    var monRingRef = _monitorRingBuffer;
                    byte[] monScratch = Array.Empty<byte>();
                    monitorTap = (samples, offset, count) =>
                    {
                        int bytes = count * sizeof(float);
                        if (monScratch.Length < bytes) monScratch = new byte[bytes];
                        MemoryMarshal.AsBytes(samples.AsSpan(offset, count)).CopyTo(monScratch);
                        monRingRef.Write(monScratch, 0, bytes);
                    };

                    ISampleProvider monChain = _monitorRingBuffer.ToSampleProvider();
                    if (monMix.SampleRate != targetSampleRate)
                    {
                        monChain = new WdlResamplingSampleProvider(monChain, monMix.SampleRate);
                    }

                    ISampleProvider monAdapted = new OutputChannelAdapter(monChain, monMix.Channels);
                    IWaveProvider monWave = ExactFloatWaveProvider.Is32BitFloatFormat(monMix)
                        ? new ExactFloatWaveProvider(monAdapted, monMix)
                        : monAdapted.ToWaveProvider();

                    _monitorOutput = CreateAndInitWasapiOut(_monitorDevice, monWave, latencyMs);
                    ActiveMonitorId = monitorOutput.Id;
                }

                _dspChain = new DspChainSampleProvider(
                    sampleChain,
                    Parameters,
                    Soundboard,
                    targetSampleRate,
                    appSampleBus,
                    monitorTap);

                ISampleProvider adaptedOutput = new OutputChannelAdapter(_dspChain, targetChannels);
                IWaveProvider finalWaveOutput = ExactFloatWaveProvider.Is32BitFloatFormat(outputMixFormat)
                    ? new ExactFloatWaveProvider(adaptedOutput, outputMixFormat)
                    : adaptedOutput.ToWaveProvider();

                _output = CreateAndInitWasapiOut(_outputDevice, finalWaveOutput, latencyMs);
                _output.PlaybackStopped += (_, e) =>
                {
                    if (e.Exception != null && _isRunning && gen == Volatile.Read(ref _sessionGeneration))
                    {
                        TriggerUnexpectedStop($"Output playback stopped: {AudioDeviceManager.TranslateAudioError(e.Exception)}", gen);
                    }
                };

                _capture.StartRecording();
                _appLoopback?.Start();
                _output.Play();
                _monitorOutput?.Play();

                ActiveInputId = input.Id;
                ActiveInputIsLoopback = input.IsLoopback;
                ActiveOutputId = output.Id;
                _isRunning = true;
            }
            catch
            {
                StopInternal();
                throw;
            }
        }

        StateChanged?.Invoke();
    }

    private void TriggerUnexpectedStop(string errorMessage, int expectedGeneration)
    {
        if (Interlocked.Exchange(ref _stoppingUnexpected, 1) != 0)
            return;

        // Always unwind off the WASAPI audio thread to avoid self-join / Dispatcher ABBA deadlocks
        Task.Run(() =>
        {
            lock (_lifecycleLock)
            {
                if (expectedGeneration != Volatile.Read(ref _sessionGeneration))
                    return;
                StopInternal();
            }

            StateChanged?.Invoke();
            EngineError?.Invoke(errorMessage);
        });
    }

    private static WasapiOut CreateAndInitWasapiOut(MMDevice outputDevice, IWaveProvider provider, int latencyMs)
    {
        var wasapiOut = new WasapiOut(outputDevice, AudioClientShareMode.Shared, useEventSync: true, latency: latencyMs);
        try
        {
            wasapiOut.Init(provider);
            return wasapiOut;
        }
        catch (Exception ex) when (ex is COMException || ex is ArgumentException || ex is NotSupportedException)
        {
            wasapiOut.Dispose();
            var fallback = new WasapiOut(outputDevice, AudioClientShareMode.Shared, useEventSync: false, latency: latencyMs);
            fallback.Init(provider);
            return fallback;
        }
    }

    public void Stop()
    {
        bool wasRunning;
        lock (_lifecycleLock)
        {
            wasRunning = _isRunning;
            Interlocked.Increment(ref _sessionGeneration);
            StopInternal();
        }

        if (wasRunning)
        {
            StateChanged?.Invoke();
        }
    }

    private void StopInternal()
    {
        _isRunning = false;

        if (_appLoopback != null)
        {
            try { _appLoopback.Stop(); } catch { }
            try { _appLoopback.Dispose(); } catch { }
            _appLoopback = null;
        }

        if (_capture != null)
        {
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }

        if (_monitorOutput != null)
        {
            try { _monitorOutput.Stop(); } catch { }
            try { _monitorOutput.Dispose(); } catch { }
            _monitorOutput = null;
        }

        if (_output != null)
        {
            try { _output.Stop(); } catch { }
            try { _output.Dispose(); } catch { }
            _output = null;
        }

        _ringBuffer?.Clear();
        _ringBuffer = null;
        _appRingBuffer?.Clear();
        _appRingBuffer = null;
        _monitorRingBuffer?.Clear();
        _monitorRingBuffer = null;
        _driftCompensator = null;

        _dspChain?.ResetMeters();
        _dspChain = null;

        if (_inputDevice != null)
        {
            try { _inputDevice.Dispose(); } catch { }
            _inputDevice = null;
        }

        if (_outputDevice != null)
        {
            try { _outputDevice.Dispose(); } catch { }
            _outputDevice = null;
        }

        if (_monitorDevice != null)
        {
            try { _monitorDevice.Dispose(); } catch { }
            _monitorDevice = null;
        }

        _timerResolutionScope?.Dispose();
        _timerResolutionScope = null;

        ActiveInputId = null;
        ActiveOutputId = null;
        ActiveMonitorId = null;
        ActiveAppLoopbackPid = null;
        InputChannelCount = 0;
        OutputChannelCount = 0;
    }

    public void Dispose()
    {
        Stop();
    }
}
