using NAudio.Wave;
using VoxCable.Soundboard;

namespace VoxCable.Dsp;

/// <summary>
/// Full real-time stereo studio DSP pipeline combining:
/// - Atomic parameter snapshots & per-sample gain/mute/bleep smoothing (zero zipper clicks)
/// - Broadcast voice strip (HPF, Spectral Wiener Denoiser, Hysteresis Noise Gate, RMS Compressor)
/// - Creative FX (Formant-preserving Pitch Shifter + Auto-Tune, 3-Band EQ, Chorus, Robot, Squelched Radio, Bitcrusher, Echo, Reverb)
/// - Secondary Per-App Process Loopback mix bus & Soundboard mixing
/// - Independent Local Monitor output tap ("Hear Myself" + SFX preview)
/// - Time-accurate dBFS peak metering, Clip LEDs, and lock-free waveform scope snapshot
/// </summary>
public sealed class DspChainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider? _source;
    private readonly ISampleProvider? _appLoopbackSource;
    private readonly Action<float[], int, int>? _monitorTap;
    private readonly DspParameters _params;
    private readonly SoundboardEngine _soundboard;
    private readonly int _sampleRate;

    private readonly HighPassFilterEffect _highPass;
    private readonly SpectralDenoiserEffect _denoiser;
    private readonly NoiseGateEffect _noiseGate;
    private readonly CompressorEffect _compressor;
    private readonly AutoTuneTracker _autoTune;
    private readonly PhaseVocoderPitchShifter _pitchShifter;
    private readonly ThreeBandEqEffect _eq;
    private readonly ChorusEffect _chorus;
    private readonly RobotModEffect _robot;
    private readonly RadioEffect _radio;
    private readonly BitcrusherEffect _bitcrusher;
    private readonly EchoEffect _echo;
    private readonly ReverbEffect _reverb;

    private float[] _appScratch = Array.Empty<float>();
    private float[] _monitorBuf = Array.Empty<float>();
    private float[] _sfxMonitorScratch = Array.Empty<float>();

    // Smoothed linear gains (eliminate clicks on slider moves & mute/bleep switches)
    private float _smoothInGain = 1f;
    private float _smoothOutGain = 1f;
    private float _smoothMuteGain = 1f;
    private float _smoothBleepMix;
    private double _bleepPhase;
    private readonly float _gainSmoothAlpha;

    // Previous enable edge detection (auto-reset stale state on re-enable)
    private bool _prevDenoiseEnabled;
    private bool _prevPitchActive;
    private bool _prevChorusEnabled;
    private bool _prevGateEnabled;

    // Lock-free waveform scope buffer (128 mono downsampled points)
    private readonly float[] _waveRing = new float[128];
    private int _waveWriteIdx;
    private int _waveDecimCounter;
    private readonly int _waveDecimStride;

    // Metering & telemetry
    private volatile float _inputPeakL;
    private volatile float _inputPeakR;
    private volatile float _outputPeakL;
    private volatile float _outputPeakR;
    private volatile bool _inputClipping;
    private volatile bool _outputClipping;
    private int _inClipHoldFrames;
    private int _outClipHoldFrames;

    public DspChainSampleProvider(
        ISampleProvider? source,
        DspParameters parameters,
        SoundboardEngine soundboard,
        int sampleRate = 48000,
        ISampleProvider? appLoopbackSource = null,
        Action<float[], int, int>? monitorTap = null)
    {
        _source = source;
        _appLoopbackSource = appLoopbackSource;
        _monitorTap = monitorTap;
        _params = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _soundboard = soundboard ?? throw new ArgumentNullException(nameof(soundboard));

        _sampleRate = Math.Max(8000, source?.WaveFormat.SampleRate ?? sampleRate);
        WaveFormat = source?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 2);

        _gainSmoothAlpha = 1f - MathF.Exp(-1f / (0.008f * _sampleRate)); // 8 ms smooth ramp
        _waveDecimStride = Math.Max(1, _sampleRate / 6000); // ~21 ms visible waveform window

        _highPass = new HighPassFilterEffect(_sampleRate);
        _denoiser = new SpectralDenoiserEffect(_sampleRate);
        _noiseGate = new NoiseGateEffect(_sampleRate);
        _compressor = new CompressorEffect(_sampleRate);
        _autoTune = new AutoTuneTracker(_sampleRate);
        _pitchShifter = new PhaseVocoderPitchShifter(_sampleRate);
        _eq = new ThreeBandEqEffect(_sampleRate);
        _chorus = new ChorusEffect(_sampleRate);
        _robot = new RobotModEffect(_sampleRate);
        _radio = new RadioEffect(_sampleRate);
        _bitcrusher = new BitcrusherEffect();
        _echo = new EchoEffect(_sampleRate);
        _reverb = new ReverbEffect(_sampleRate);
    }

    public WaveFormat WaveFormat { get; }

    public float InputPeakL => _inputPeakL;
    public float InputPeakR => _inputPeakR;
    public float OutputPeakL => _outputPeakL;
    public float OutputPeakR => _outputPeakR;
    public bool InputClipping => _inputClipping;
    public bool OutputClipping => _outputClipping;
    public bool NoiseGateOpen => _noiseGate.IsOpen;
    public float CompressorGainReductionDb => _compressor.GainReductionDb;

    public int CopyWaveformSnapshot(float[] destination)
    {
        int n = Math.Min(destination.Length, _waveRing.Length);
        int start = Volatile.Read(ref _waveWriteIdx);
        for (int i = 0; i < n; i++)
        {
            destination[i] = _waveRing[(start + i) % _waveRing.Length];
        }
        return n;
    }

    public void ResetMeters()
    {
        _inputPeakL = 0f;
        _inputPeakR = 0f;
        _outputPeakL = 0f;
        _outputPeakR = 0f;
        _inputClipping = false;
        _outputClipping = false;
        _inClipHoldFrames = 0;
        _outClipHoldFrames = 0;
        Array.Clear(_waveRing);
    }

    public void ResetEffects()
    {
        _highPass.Reset();
        _denoiser.Reset();
        _noiseGate.Reset();
        _compressor.Reset();
        _autoTune.Reset();
        _pitchShifter.Reset();
        _eq.Reset();
        _chorus.Reset();
        _robot.Reset();
        _radio.Reset();
        _bitcrusher.Reset();
        _echo.Reset();
        _reverb.Reset();
        _smoothInGain = 1f;
        _smoothOutGain = 1f;
        _smoothMuteGain = 1f;
        _smoothBleepMix = 0f;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source != null ? _source.Read(buffer, offset, count) : count;
        if (read <= 0) return 0;

        ProcessBuffer(buffer, offset, read);
        return read;
    }

    public void ProcessBuffer(float[] buffer, int offset, int count)
    {
        int stereoCount = count - (count % 2);
        if (stereoCount <= 0) return;

        int frames = stereoCount / 2;
        var p = _params.Current;

        // Time-accurate meter decay (~18 dB/sec regardless of WASAPI buffer size)
        float decayFactor = MathF.Pow(10f, -18f * (frames / (float)_sampleRate) / 20f);

        // Pre-FX input peak metering (unclamped to detect real input clipping)
        ComputePeak(buffer, offset, stereoCount, out float inL, out float inR);
        _inputPeakL = MathF.Max(Math.Clamp(inL, 0f, 1f), _inputPeakL * decayFactor);
        _inputPeakR = MathF.Max(Math.Clamp(inR, 0f, 1f), _inputPeakR * decayFactor);
        UpdateClipLatch(MathF.Max(inL, inR) >= 0.99f, frames, ref _inClipHoldFrames, out bool inClip);
        _inputClipping = inClip;

        // Pull secondary Per-App Process Loopback audio if active
        float appVol = Math.Clamp(p.AppLoopbackVolume, 0f, 1.5f);
        bool hasAppAudio = false;
        if (_appLoopbackSource != null && appVol > 0.001f)
        {
            if (_appScratch.Length < stereoCount) _appScratch = new float[stereoCount];
            int appRead = _appLoopbackSource.Read(_appScratch, 0, stereoCount);
            if (appRead > 0)
            {
                if (appRead < stereoCount) Array.Clear(_appScratch, appRead, stereoCount - appRead);
                hasAppAudio = true;
            }
        }

        // Pull SFX into dedicated scratch if monitor wants separate SFX routing, else direct mix
        float sfxVol = Math.Clamp(p.SfxVolume, 0f, 1.5f);
        bool needMonitorTap = _monitorTap != null && p.MonitorEnabled && (p.MonitorVoice || p.MonitorSfx);
        bool separateSfxForMonitor = needMonitorTap && !p.MonitorVoice && p.MonitorSfx;

        if (separateSfxForMonitor)
        {
            if (_sfxMonitorScratch.Length < stereoCount) _sfxMonitorScratch = new float[stereoCount];
            Array.Clear(_sfxMonitorScratch, 0, stereoCount);
            _soundboard.MixInto(_sfxMonitorScratch, 0, stereoCount, sfxVol);
        }

        if (hasAppAudio && p.RouteAppThroughFx)
        {
            MixScaled(buffer, offset, _appScratch, stereoCount, appVol);
        }

        if (p.RouteSfxThroughFx)
        {
            if (separateSfxForMonitor)
                MixScaled(buffer, offset, _sfxMonitorScratch, stereoCount, 1f);
            else
                _soundboard.MixInto(buffer, offset, stereoCount, sfxVol);
        }

        // Smooth input gain
        float targetInGain = p.MasterBypass ? 1f : DbToLinear(p.InputGainDb);
        ApplySmoothedGain(buffer, offset, stereoCount, ref _smoothInGain, targetInGain);

        if (!p.MasterBypass)
        {
            // 1. Broadcast Voice Strip
            if (p.HighPassEnabled)
            {
                _highPass.Process(buffer, offset, stereoCount, p.HighPassHz);
            }

            if (p.DenoiseEnabled)
            {
                if (!_prevDenoiseEnabled) _denoiser.Reset();
                _denoiser.Process(buffer, offset, stereoCount, p.DenoiseStrength);
            }
            _prevDenoiseEnabled = p.DenoiseEnabled;

            if (p.NoiseGateEnabled)
            {
                if (!_prevGateEnabled) _noiseGate.Reset();
                _noiseGate.Process(buffer, offset, stereoCount, p.NoiseGateThresholdDb);
            }
            _prevGateEnabled = p.NoiseGateEnabled;

            if (p.CompressorEnabled)
            {
                _compressor.Process(buffer, offset, stereoCount, p.CompressorThresholdDb, p.CompressorRatio, p.CompressorMakeupDb);
            }

            // 2. Pitch, Formant & Auto-Tune (shared single phase vocoder -> low latency & half CPU)
            float autoTuneCorr = 0f;
            if (p.AutoTuneEnabled)
            {
                autoTuneCorr = _autoTune.AnalyzeAndComputeCorrection(buffer, offset, stereoCount, p.AutoTuneKey, p.AutoTuneScale, p.AutoTuneStrength);
            }

            bool pitchActive = p.PitchEnabled || p.AutoTuneEnabled;
            if (pitchActive)
            {
                if (!_prevPitchActive) _pitchShifter.Reset();
                float totalSt = (p.PitchEnabled ? p.PitchSemitones : 0f) + autoTuneCorr;
                float effectiveMix = p.PitchEnabled ? p.PitchMix : 1.0f;
                bool preserveFormants = p.PitchEnabled ? p.PreserveFormants : true;
                float formantSt = p.PitchEnabled ? p.FormantSemitones : 0f;
                _pitchShifter.Process(buffer, offset, stereoCount, totalSt, effectiveMix, preserveFormants, formantSt);
            }
            _prevPitchActive = pitchActive;

            // 3. 3-Band EQ
            if (p.EqEnabled)
            {
                _eq.Process(buffer, offset, stereoCount, p.BassDb, p.MidDb, p.TrebleDb);
            }

            // 4. Creative FX Chain
            if (p.ChorusEnabled)
            {
                if (!_prevChorusEnabled) _chorus.Reset();
                _chorus.Process(buffer, offset, stereoCount, p.ChorusRateHz, p.ChorusDepthMs, p.ChorusMix);
            }
            _prevChorusEnabled = p.ChorusEnabled;

            if (p.RobotEnabled)
            {
                _robot.Process(buffer, offset, stereoCount, p.RobotFreqHz, p.RobotMix);
            }

            if (p.RadioEnabled)
            {
                _radio.Process(buffer, offset, stereoCount, p.RadioDrive, p.RadioStatic, p.RadioMix, p.RadioSquelch);
            }

            if (p.BitcrushEnabled)
            {
                _bitcrusher.Process(buffer, offset, stereoCount, p.BitcrushBits, p.BitcrushDownsample, p.BitcrushMix);
            }

            // Echo & Reverb naturally ring out tails when disabled instead of holding frozen ghost audio
            if (p.EchoEnabled || _echo.HasTailEnergy)
            {
                _echo.Process(buffer, offset, stereoCount, p.EchoDelayMs, p.EchoFeedback, p.EchoMix, inputEnabled: p.EchoEnabled);
            }

            if (p.ReverbEnabled || _reverb.HasTailEnergy)
            {
                _reverb.Process(buffer, offset, stereoCount, p.ReverbRoomSize, p.ReverbDamping, p.ReverbMix, inputEnabled: p.ReverbEnabled);
            }
        }
        else
        {
            _prevDenoiseEnabled = false;
            _prevPitchActive = false;
            _prevChorusEnabled = false;
            _prevGateEnabled = false;
        }

        // Hold-to-Bleep: smoothly ducks voice and injects broadcast 1000 Hz censor tone
        if (p.HoldToBleep || _smoothBleepMix > 0.0005f)
        {
            ApplyHoldToBleep(buffer, offset, stereoCount, p.HoldToBleep);
        }

        if (hasAppAudio && !p.RouteAppThroughFx)
        {
            MixScaled(buffer, offset, _appScratch, stereoCount, appVol);
        }

        if (!p.RouteSfxThroughFx)
        {
            if (separateSfxForMonitor)
                MixScaled(buffer, offset, _sfxMonitorScratch, stereoCount, 1f);
            else
                _soundboard.MixInto(buffer, offset, stereoCount, sfxVol);
        }

        // Smooth output gain
        ApplySmoothedGain(buffer, offset, stereoCount, ref _smoothOutGain, DbToLinear(p.OutputGainDb));

        // Pre-limiter clip check
        ComputePeak(buffer, offset, stereoCount, out float preLimL, out float preLimR);
        SoftLimiterEffect.Process(buffer, offset, stereoCount, p.SoftLimiterEnabled);

        // Feed Local Monitor output ("Hear Myself" / SFX preview) BEFORE primary output mute
        if (needMonitorTap && _monitorTap != null)
        {
            if (_monitorBuf.Length < stereoCount) _monitorBuf = new float[stereoCount];
            float monVol = Math.Clamp(p.MonitorVolume, 0f, 1.5f);

            if (separateSfxForMonitor)
            {
                for (int i = 0; i < stereoCount; i++)
                    _monitorBuf[i] = Math.Clamp(_sfxMonitorScratch[i] * monVol, -0.98f, 0.98f);
            }
            else
            {
                for (int i = 0; i < stereoCount; i++)
                    _monitorBuf[i] = Math.Clamp(buffer[offset + i] * monVol, -0.98f, 0.98f);
            }

            _monitorTap(_monitorBuf, 0, stereoCount);
        }

        // Clickless output mute
        ApplySmoothedGain(buffer, offset, stereoCount, ref _smoothMuteGain, p.MuteOutput ? 0f : 1f);

        // Output peak metering + live waveform scope feed
        ComputePeak(buffer, offset, stereoCount, out float outL, out float outR);
        _outputPeakL = MathF.Max(Math.Clamp(outL, 0f, 1f), _outputPeakL * decayFactor);
        _outputPeakR = MathF.Max(Math.Clamp(outR, 0f, 1f), _outputPeakR * decayFactor);
        UpdateClipLatch(!p.MuteOutput && MathF.Max(preLimL, preLimR) >= 0.98f, frames, ref _outClipHoldFrames, out bool outClip);
        _outputClipping = outClip;

        FeedWaveformScope(buffer, offset, frames);
    }

    private void ApplyHoldToBleep(float[] buffer, int offset, int stereoCount, bool active)
    {
        float target = active ? 1f : 0f;
        double step = 2.0 * Math.PI * 1000.0 / _sampleRate;
        int frames = stereoCount / 2;

        for (int f = 0; f < frames; f++)
        {
            _smoothBleepMix += (target - _smoothBleepMix) * _gainSmoothAlpha;
            float beep = 0.40f * MathF.Sin((float)_bleepPhase);
            _bleepPhase += step;
            if (_bleepPhase >= 2.0 * Math.PI) _bleepPhase -= 2.0 * Math.PI;

            int idx = offset + f * 2;
            float inv = 1f - _smoothBleepMix;
            buffer[idx] = buffer[idx] * inv + beep * _smoothBleepMix;
            buffer[idx + 1] = buffer[idx + 1] * inv + beep * _smoothBleepMix;
        }
    }

    private void ApplySmoothedGain(float[] buffer, int offset, int stereoCount, ref float current, float target)
    {
        if (MathF.Abs(current - target) < 0.0005f)
        {
            current = target;
            if (MathF.Abs(target - 1f) < 0.0005f) return;
            if (target <= 0.0001f)
            {
                Array.Clear(buffer, offset, stereoCount);
                return;
            }
            for (int i = offset; i < offset + stereoCount; i++)
                buffer[i] *= target;
            return;
        }

        int frames = stereoCount / 2;
        for (int f = 0; f < frames; f++)
        {
            current += (target - current) * _gainSmoothAlpha;
            int idx = offset + f * 2;
            buffer[idx] *= current;
            buffer[idx + 1] *= current;
        }
    }

    private void FeedWaveformScope(float[] buffer, int offset, int frames)
    {
        int idx = _waveWriteIdx;
        for (int f = 0; f < frames; f++)
        {
            _waveDecimCounter++;
            if (_waveDecimCounter >= _waveDecimStride)
            {
                _waveDecimCounter = 0;
                int s = offset + f * 2;
                _waveRing[idx] = 0.5f * (buffer[s] + buffer[s + 1]);
                idx = (idx + 1) % _waveRing.Length;
            }
        }
        Volatile.Write(ref _waveWriteIdx, idx);
    }

    private void UpdateClipLatch(bool clippedNow, int frames, ref int holdFrames, out bool isLatched)
    {
        if (clippedNow)
        {
            holdFrames = (int)(_sampleRate * 1.2f); // Hold clip LED for 1.2s
        }
        else if (holdFrames > 0)
        {
            holdFrames = Math.Max(0, holdFrames - frames);
        }
        isLatched = holdFrames > 0;
    }

    private static void MixScaled(float[] dst, int dstOffset, float[] src, int count, float scale)
    {
        for (int i = 0; i < count; i++)
        {
            dst[dstOffset + i] += src[i] * scale;
        }
    }

    private static float DbToLinear(float db) => MathF.Pow(10f, Math.Clamp(db, -36f, 36f) / 20f);

    private static void ComputePeak(float[] buffer, int offset, int count, out float peakL, out float peakR)
    {
        float maxL = 0f;
        float maxR = 0f;
        for (int i = offset; i < offset + count; i += 2)
        {
            float l = MathF.Abs(buffer[i]);
            float r = MathF.Abs(buffer[i + 1]);
            if (float.IsFinite(l) && l > maxL) maxL = l;
            if (float.IsFinite(r) && r > maxR) maxR = r;
        }
        peakL = maxL;
        peakR = maxR;
    }
}
