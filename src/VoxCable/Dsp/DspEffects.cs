namespace VoxCable.Dsp;

/// <summary>
/// Studio noise gate with sample-rate-accurate attack/release, 6 dB hysteresis,
/// 120 ms hold timer, -45 dB gentle floor, and live open/closed status for UI LEDs.
/// </summary>
public sealed class NoiseGateEffect
{
    private int _sampleRate;
    private float _envAttack;
    private float _envRelease;
    private float _gainOpenAlpha;
    private float _gainCloseAlpha;
    private int _holdSamples;

    private float _envelope;
    private float _gain = 1f;
    private int _holdCounter;
    private volatile bool _isOpen = true;

    public bool IsOpen => _isOpen;

    public NoiseGateEffect(int sampleRate = 48000) => SetSampleRate(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _envAttack = MathF.Exp(-1f / (0.002f * _sampleRate));
        _envRelease = MathF.Exp(-1f / (0.060f * _sampleRate));
        _gainOpenAlpha = 1f - MathF.Exp(-1f / (0.0025f * _sampleRate));  // ~2.5 ms crisp vocal attack
        _gainCloseAlpha = 1f - MathF.Exp(-1f / (0.140f * _sampleRate));  // ~140 ms smooth fade
        _holdSamples = (int)(0.120f * _sampleRate);                      // 120 ms hold prevents word chopping
    }

    public void Reset()
    {
        _envelope = 0f;
        _gain = 1f;
        _holdCounter = 0;
        _isOpen = true;
    }

    public void Process(float[] buffer, int offset, int count, float thresholdDb)
    {
        float openThresh = MathF.Pow(10f, Math.Clamp(thresholdDb, -70f, -5f) / 20f);
        float closeThresh = openThresh * 0.5012f; // -6 dB hysteresis prevents chattering
        const float floorGain = 0.0056f;          // -45 dB natural room floor

        bool gateOpen = _isOpen;
        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            float l = buffer[idx];
            float r = buffer[idx + 1];
            float peak = MathF.Max(MathF.Abs(l), MathF.Abs(r));

            float coeff = peak > _envelope ? _envAttack : _envRelease;
            _envelope = coeff * _envelope + (1f - coeff) * peak;
            if (_envelope < 1e-20f) _envelope = 0f;

            if (gateOpen)
            {
                if (_envelope >= closeThresh)
                {
                    _holdCounter = _holdSamples;
                }
                else if (_holdCounter > 0)
                {
                    _holdCounter--;
                }
                else
                {
                    gateOpen = false;
                }
            }
            else if (_envelope >= openThresh)
            {
                gateOpen = true;
                _holdCounter = _holdSamples;
            }

            float targetGain = gateOpen ? 1f : floorGain;
            float alpha = targetGain > _gain ? _gainOpenAlpha : _gainCloseAlpha;
            _gain += (targetGain - _gain) * alpha;

            buffer[idx] = l * _gain;
            buffer[idx + 1] = r * _gain;
        }

        _isOpen = gateOpen;
    }
}

/// <summary>
/// Zero-allocation, Nyquist-safe 3-Band EQ (Low Shelf 180 Hz, Mid Peak 1 kHz, High Shelf 4.5 kHz)
/// preserving filter delay state across slider drags to eliminate zipper clicks.
/// </summary>
public sealed class ThreeBandEqEffect
{
    private int _sampleRate = 48000;
    private float _lastBass = float.NaN;
    private float _lastMid = float.NaN;
    private float _lastTreble = float.NaN;

    private BiquadState _lowL = BiquadState.CreateBypass();
    private BiquadState _lowR = BiquadState.CreateBypass();
    private BiquadState _midL = BiquadState.CreateBypass();
    private BiquadState _midR = BiquadState.CreateBypass();
    private BiquadState _highL = BiquadState.CreateBypass();
    private BiquadState _highR = BiquadState.CreateBypass();

    public ThreeBandEqEffect(int sampleRate = 48000) => SetSampleRate(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        sampleRate = Math.Max(8000, sampleRate);
        if (sampleRate == _sampleRate) return;
        _sampleRate = sampleRate;
        _lastBass = float.NaN;
        _lastMid = float.NaN;
        _lastTreble = float.NaN;
    }

    public void Reset()
    {
        _lowL.ResetState(); _lowR.ResetState();
        _midL.ResetState(); _midR.ResetState();
        _highL.ResetState(); _highR.ResetState();
        _lastBass = float.NaN;
        _lastMid = float.NaN;
        _lastTreble = float.NaN;
    }

    public void Process(float[] buffer, int offset, int count, float bassDb, float midDb, float trebleDb)
    {
        bassDb = Math.Clamp(bassDb, -15f, 15f);
        midDb = Math.Clamp(midDb, -15f, 15f);
        trebleDb = Math.Clamp(trebleDb, -15f, 15f);

        if (float.IsNaN(_lastBass) || MathF.Abs(bassDb - _lastBass) > 0.02f)
        {
            _lowL.SetLowShelf(_sampleRate, 180f, 0.707f, bassDb);
            _lowR.SetLowShelf(_sampleRate, 180f, 0.707f, bassDb);
            _lastBass = bassDb;
        }

        if (float.IsNaN(_lastMid) || MathF.Abs(midDb - _lastMid) > 0.02f)
        {
            _midL.SetPeakingEq(_sampleRate, 1000f, 0.8f, midDb);
            _midR.SetPeakingEq(_sampleRate, 1000f, 0.8f, midDb);
            _lastMid = midDb;
        }

        if (float.IsNaN(_lastTreble) || MathF.Abs(trebleDb - _lastTreble) > 0.02f)
        {
            _highL.SetHighShelf(_sampleRate, 4500f, 0.707f, trebleDb);
            _highR.SetHighShelf(_sampleRate, 4500f, 0.707f, trebleDb);
            _lastTreble = trebleDb;
        }

        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            buffer[idx] = _highL.Process(_midL.Process(_lowL.Process(buffer[idx])));
            buffer[idx + 1] = _highR.Process(_midR.Process(_lowR.Process(buffer[idx + 1])));
        }
    }
}

/// <summary>
/// Stereo quadrature-LFO modulated fractional-delay Chorus for lush alien/ethereal voice textures.
/// </summary>
public sealed class ChorusEffect
{
    private int _sampleRate = 48000;
    private float[] _delayL = Array.Empty<float>();
    private float[] _delayR = Array.Empty<float>();
    private int _writeIdx;
    private double _lfoPhase;

    public ChorusEffect(int sampleRate = 48000) => SetSampleRate(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        int bufLen = Math.Max(256, (int)(_sampleRate * 0.060f)); // 60 ms headroom
        if (_delayL.Length != bufLen)
        {
            _delayL = new float[bufLen];
            _delayR = new float[bufLen];
        }
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_delayL);
        Array.Clear(_delayR);
        _writeIdx = 0;
        _lfoPhase = 0;
    }

    public void Process(float[] buffer, int offset, int count, float rateHz, float depthMs, float mix)
    {
        int len = _delayL.Length;
        if (len == 0) return;

        rateHz = Math.Clamp(rateHz, 0.1f, 8f);
        depthMs = Math.Clamp(depthMs, 0.5f, 18f);
        mix = Math.Clamp(mix, 0f, 1f);

        float baseDelaySamples = _sampleRate * 0.012f; // 12 ms center
        float depthSamples = _sampleRate * (depthMs * 0.001f);
        double phaseStep = 2.0 * Math.PI * rateHz / _sampleRate;

        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            float dryL = buffer[idx];
            float dryR = buffer[idx + 1];

            _delayL[_writeIdx] = dryL;
            _delayR[_writeIdx] = dryR;

            float lfoL = MathF.Sin((float)_lfoPhase);
            float lfoR = MathF.Cos((float)_lfoPhase); // 90 deg quadrature stereo spread
            _lfoPhase += phaseStep;
            if (_lfoPhase >= 2.0 * Math.PI) _lfoPhase -= 2.0 * Math.PI;

            float dL = Math.Clamp(baseDelaySamples + lfoL * depthSamples, 1f, len - 3);
            float dR = Math.Clamp(baseDelaySamples + lfoR * depthSamples, 1f, len - 3);

            float wetL = ReadInterpolated(_delayL, _writeIdx, dL, len);
            float wetR = ReadInterpolated(_delayR, _writeIdx, dR, len);

            _writeIdx++;
            if (_writeIdx >= len) _writeIdx = 0;

            buffer[idx] = dryL + (wetL - dryL) * mix;
            buffer[idx + 1] = dryR + (wetR - dryR) * mix;
        }
    }

    private static float ReadInterpolated(float[] buf, int writeIdx, float delaySamples, int len)
    {
        float readPos = writeIdx - delaySamples;
        if (readPos < 0f) readPos += len;
        int i0 = (int)readPos;
        int i1 = i0 + 1;
        if (i1 >= len) i1 = 0;
        float frac = readPos - i0;
        return buf[i0] + (buf[i1] - buf[i0]) * frac;
    }
}

public sealed class RobotModEffect
{
    private int _sampleRate = 48000;
    private double _phase;

    public RobotModEffect(int sampleRate = 48000) => SetSampleRate(sampleRate);
    public void SetSampleRate(int sampleRate) => _sampleRate = Math.Max(8000, sampleRate);
    public void Reset() => _phase = 0;

    public void Process(float[] buffer, int offset, int count, float freqHz, float mix)
    {
        freqHz = Math.Clamp(freqHz, 15f, 500f);
        mix = Math.Clamp(mix, 0f, 1f);
        double phaseStep = 2.0 * Math.PI * freqHz / _sampleRate;

        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            float carrier = 0.85f * MathF.Sin((float)_phase) + 0.15f * MathF.Sin((float)(2.0 * _phase));
            _phase += phaseStep;
            if (_phase >= 2.0 * Math.PI) _phase -= 2.0 * Math.PI;

            int idx = offset + f * 2;
            float dryL = buffer[idx];
            float dryR = buffer[idx + 1];

            float wetL = dryL * carrier * 1.25f;
            float wetR = dryR * carrier * 1.25f;

            buffer[idx] = dryL + (wetL - dryL) * mix;
            buffer[idx + 1] = dryR + (wetR - dryR) * mix;
        }
    }
}

/// <summary>
/// Walkie-Talkie / Radio distortion with Nyquist-safe bandpass and voice-envelope squelch
/// (prevents continuous static hiss from breaking Discord Voice Activity Detection) + authentic squelch tail.
/// </summary>
public sealed class RadioEffect
{
    private int _sampleRate = 48000;
    private BiquadState _hpL, _hpR, _lpL, _lpR;
    private uint _rng = 0x13579BDFu;
    private float _voiceEnv;
    private float _envAttack;
    private float _envRelease;
    private int _squelchTailSamples;
    private int _squelchTailTotal;
    private bool _wasTalking;

    public RadioEffect(int sampleRate = 48000) => SetSampleRate(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _envAttack = MathF.Exp(-1f / (0.005f * _sampleRate));
        _envRelease = MathF.Exp(-1f / (0.090f * _sampleRate));
        _squelchTailTotal = (int)(0.095f * _sampleRate);
        Reset();
    }

    public void Reset()
    {
        _hpL = BiquadState.CreateBypass();
        _hpR = BiquadState.CreateBypass();
        _lpL = BiquadState.CreateBypass();
        _lpR = BiquadState.CreateBypass();
        _hpL.SetHighPass(_sampleRate, 380f, 0.8f);
        _hpR.SetHighPass(_sampleRate, 380f, 0.8f);
        _lpL.SetLowPass(_sampleRate, 3100f, 0.8f);
        _lpR.SetLowPass(_sampleRate, 3100f, 0.8f);
        _voiceEnv = 0f;
        _squelchTailSamples = 0;
        _wasTalking = false;
    }

    public void Process(float[] buffer, int offset, int count, float drive, float staticLevel, float mix, bool squelch = true)
    {
        drive = Math.Clamp(drive, 1f, 20f);
        staticLevel = Math.Clamp(staticLevel, 0f, 0.35f);
        mix = Math.Clamp(mix, 0f, 1f);
        float norm = 1f / MathF.Tanh(drive * 0.5f);

        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            float dryL = buffer[idx];
            float dryR = buffer[idx + 1];

            float peak = MathF.Max(MathF.Abs(dryL), MathF.Abs(dryR));
            float coeff = peak > _voiceEnv ? _envAttack : _envRelease;
            _voiceEnv = coeff * _voiceEnv + (1f - coeff) * peak;
            if (_voiceEnv < 1e-20f) _voiceEnv = 0f;

            float noiseGate = 1f;
            if (squelch)
            {
                bool talking = _voiceEnv > 0.008f;
                if (_wasTalking && !talking)
                {
                    _squelchTailSamples = _squelchTailTotal;
                }
                _wasTalking = talking;

                if (talking)
                {
                    noiseGate = Math.Clamp(_voiceEnv * 18f, 0.25f, 1f);
                }
                else if (_squelchTailSamples > 0)
                {
                    float tailRatio = _squelchTailSamples / (float)_squelchTailTotal;
                    noiseGate = tailRatio * 1.15f; // Classic short walkie "kshhk" release burst
                    _squelchTailSamples--;
                }
                else
                {
                    noiseGate = 0f;
                }
            }

            float bpL = _lpL.Process(_hpL.Process(dryL));
            float bpR = _lpR.Process(_hpR.Process(dryR));

            // Keep static amplitude independent of drive saturation blowup
            float noise = NextWhiteNoise() * staticLevel * 0.22f * noiseGate;
            float wetL = MathF.Tanh(bpL * drive) * norm * 0.72f + noise;
            float wetR = MathF.Tanh(bpR * drive) * norm * 0.72f + noise;

            buffer[idx] = dryL + (wetL - dryL) * mix;
            buffer[idx + 1] = dryR + (wetR - dryR) * mix;
        }
    }

    private float NextWhiteNoise()
    {
        _rng ^= _rng << 13;
        _rng ^= _rng >> 17;
        _rng ^= _rng << 5;
        return ((_rng & 0xFFFF) / 32768f) - 1f;
    }
}

public sealed class BitcrusherEffect
{
    private int _holdCounter;
    private float _heldL;
    private float _heldR;

    public void Reset()
    {
        _holdCounter = 0;
        _heldL = 0f;
        _heldR = 0f;
    }

    public void Process(float[] buffer, int offset, int count, float bits, float downsample, float mix)
    {
        int qBits = Math.Clamp((int)MathF.Round(bits), 2, 16);
        int ds = Math.Clamp((int)MathF.Round(downsample), 1, 32);
        mix = Math.Clamp(mix, 0f, 1f);

        float levels = (1 << (qBits - 1));

        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            float dryL = buffer[idx];
            float dryR = buffer[idx + 1];

            if (_holdCounter <= 0)
            {
                _heldL = MathF.Round(Math.Clamp(dryL, -1f, 1f) * levels) / levels;
                _heldR = MathF.Round(Math.Clamp(dryR, -1f, 1f) * levels) / levels;
                _holdCounter = ds;
            }
            _holdCounter--;

            buffer[idx] = dryL + (_heldL - dryL) * mix;
            buffer[idx + 1] = dryR + (_heldR - dryR) * mix;
        }
    }
}

/// <summary>
/// Stereo Echo with smoothed fractional delay interpolation (tape-echo glide, zero slider clicks),
/// sample-rate-invariant feedback damping, anti-denormal flushing, and natural tail ring-out.
/// </summary>
public sealed class EchoEffect
{
    private int _sampleRate = 48000;
    private float[] _delayL = Array.Empty<float>();
    private float[] _delayR = Array.Empty<float>();
    private int _writeIndex;
    private float _dampL;
    private float _dampR;
    private float _dampCoeff;
    private float _smoothedDelayL;
    private float _delaySmoothAlpha;
    private bool _delayInitialized;
    private bool _hasTailEnergy;

    public bool HasTailEnergy => _hasTailEnergy;

    public EchoEffect(int sampleRate = 48000) => SetSampleRate(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        int maxSamples = _sampleRate + 2048;
        if (_delayL.Length != maxSamples)
        {
            _delayL = new float[maxSamples];
            _delayR = new float[maxSamples];
        }
        // ~4.5 kHz sample-rate-invariant LP damping in echo feedback
        _dampCoeff = MathF.Exp(-2f * MathF.PI * MathF.Min(4500f, _sampleRate * 0.4f) / _sampleRate);
        _delaySmoothAlpha = 1f - MathF.Exp(-1f / (0.035f * _sampleRate));
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_delayL);
        Array.Clear(_delayR);
        _writeIndex = 0;
        _dampL = 0f;
        _dampR = 0f;
        _smoothedDelayL = _sampleRate * 0.260f;
        _delayInitialized = false;
        _hasTailEnergy = false;
    }

    public void Process(float[] buffer, int offset, int count, float delayMs, float feedback, float mix, bool inputEnabled = true)
    {
        int len = _delayL.Length;
        if (len == 0) return;

        delayMs = Math.Clamp(delayMs, 20f, 950f);
        feedback = Math.Clamp(feedback, 0f, 0.85f);
        mix = Math.Clamp(mix, 0f, 1f);

        float targetDelayL = Math.Clamp(_sampleRate * delayMs / 1000f, 2f, len - 4);
        if (!_delayInitialized)
        {
            _smoothedDelayL = targetDelayL;
            _delayInitialized = true;
        }
        float maxWet = 0f;

        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            float dryL = buffer[idx];
            float dryR = buffer[idx + 1];

            _smoothedDelayL += (targetDelayL - _smoothedDelayL) * _delaySmoothAlpha;
            float delayR = Math.Clamp(_smoothedDelayL * 1.03f, 2f, len - 4);

            float wetL = ReadFrac(_delayL, _writeIndex, _smoothedDelayL, len);
            float wetR = ReadFrac(_delayR, _writeIndex, delayR, len);

            _dampL = _dampL * _dampCoeff + wetL * (1f - _dampCoeff);
            _dampR = _dampR * _dampCoeff + wetR * (1f - _dampCoeff);
            if (MathF.Abs(_dampL) < 1e-20f) _dampL = 0f;
            if (MathF.Abs(_dampR) < 1e-20f) _dampR = 0f;

            float feedL = (inputEnabled ? dryL : 0f) + _dampL * feedback;
            float feedR = (inputEnabled ? dryR : 0f) + _dampR * feedback;
            _delayL[_writeIndex] = MathF.Abs(feedL) < 1e-20f ? 0f : feedL;
            _delayR[_writeIndex] = MathF.Abs(feedR) < 1e-20f ? 0f : feedR;

            _writeIndex++;
            if (_writeIndex >= len) _writeIndex = 0;

            float peak = MathF.Max(MathF.Abs(wetL), MathF.Abs(wetR));
            if (peak > maxWet) maxWet = peak;

            buffer[idx] = dryL + wetL * mix;
            buffer[idx + 1] = dryR + wetR * mix;
        }

        _hasTailEnergy = inputEnabled || maxWet > 0.0002f;
    }

    private static float ReadFrac(float[] buf, int writeIdx, float delaySamples, int len)
    {
        float readPos = writeIdx - delaySamples;
        if (readPos < 0f) readPos += len;
        int i0 = (int)readPos;
        int i1 = i0 + 1;
        if (i1 >= len) i1 = 0;
        float frac = readPos - i0;
        return buf[i0] + (buf[i1] - buf[i0]) * frac;
    }
}

/// <summary>
/// Lush 6-comb + 3 true unity-gain all-pass stereo Freeverb with anti-denormal flushing and natural tail decay.
/// </summary>
public sealed class ReverbEffect
{
    private static readonly int[] CombTunings = { 1116, 1188, 1277, 1356, 1422, 1491 };
    private static readonly int[] AllPassTunings = { 556, 441, 341 };
    private const int StereoSpread = 23;

    private int _sampleRate = 48000;
    private CombFilter[] _combsL = Array.Empty<CombFilter>();
    private CombFilter[] _combsR = Array.Empty<CombFilter>();
    private AllPassFilter[] _allPassL = Array.Empty<AllPassFilter>();
    private AllPassFilter[] _allPassR = Array.Empty<AllPassFilter>();
    private bool _hasTailEnergy;

    public bool HasTailEnergy => _hasTailEnergy;

    public ReverbEffect(int sampleRate = 48000) => SetSampleRate(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        double scale = _sampleRate / 44100.0;

        _combsL = CombTunings.Select(t => new CombFilter(Math.Max(64, (int)(t * scale)))).ToArray();
        _combsR = CombTunings.Select(t => new CombFilter(Math.Max(64, (int)((t + StereoSpread) * scale)))).ToArray();
        _allPassL = AllPassTunings.Select(t => new AllPassFilter(Math.Max(32, (int)(t * scale)))).ToArray();
        _allPassR = AllPassTunings.Select(t => new AllPassFilter(Math.Max(32, (int)((t + StereoSpread) * scale)))).ToArray();
        _hasTailEnergy = false;
    }

    public void Reset()
    {
        foreach (var c in _combsL) c.Clear();
        foreach (var c in _combsR) c.Clear();
        foreach (var a in _allPassL) a.Clear();
        foreach (var a in _allPassR) a.Clear();
        _hasTailEnergy = false;
    }

    public void Process(float[] buffer, int offset, int count, float roomSize, float damping, float mix, bool inputEnabled = true)
    {
        float fb = Math.Clamp(0.68f + Math.Clamp(roomSize, 0f, 1f) * 0.25f, 0.50f, 0.93f);
        float damp = Math.Clamp(damping * 0.70f, 0.05f, 0.70f);
        mix = Math.Clamp(mix, 0f, 1f);
        float dryWeight = inputEnabled ? (1f - mix * 0.35f) : 1f;

        float maxWet = 0f;
        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            float dryL = buffer[idx];
            float dryR = buffer[idx + 1];
            float input = inputEnabled ? (dryL + dryR) * 0.085f : 0f;

            float outL = 0f;
            float outR = 0f;

            for (int c = 0; c < _combsL.Length; c++)
            {
                outL += _combsL[c].Process(input, fb, damp);
                outR += _combsR[c].Process(input, fb, damp);
            }

            for (int a = 0; a < _allPassL.Length; a++)
            {
                outL = _allPassL[a].Process(outL);
                outR = _allPassR[a].Process(outR);
            }

            float peak = MathF.Max(MathF.Abs(outL), MathF.Abs(outR));
            if (peak > maxWet) maxWet = peak;

            buffer[idx] = dryL * dryWeight + outL * mix;
            buffer[idx + 1] = dryR * dryWeight + outR * mix;
        }

        _hasTailEnergy = inputEnabled || maxWet > 0.0002f;
    }

    private sealed class CombFilter
    {
        private readonly float[] _buf;
        private int _idx;
        private float _filterStore;

        public CombFilter(int size) => _buf = new float[size];

        public void Clear()
        {
            Array.Clear(_buf);
            _idx = 0;
            _filterStore = 0f;
        }

        public float Process(float input, float feedback, float damp)
        {
            float output = _buf[_idx];
            _filterStore = output * (1f - damp) + _filterStore * damp;
            if (MathF.Abs(_filterStore) < 1e-20f) _filterStore = 0f;
            float next = input + _filterStore * feedback;
            _buf[_idx] = MathF.Abs(next) < 1e-20f ? 0f : next;
            _idx++;
            if (_idx >= _buf.Length) _idx = 0;
            return output;
        }
    }

    private sealed class AllPassFilter
    {
        private readonly float[] _buf;
        private int _idx;

        public AllPassFilter(int size) => _buf = new float[size];

        public void Clear()
        {
            Array.Clear(_buf);
            _idx = 0;
        }

        /// <summary>
        /// True unity-gain Schroeder all-pass: H(z) = (-g + z^-D) / (1 - g*z^-D), g = 0.5
        /// </summary>
        public float Process(float input)
        {
            float bufOut = _buf[_idx];
            float v = input + 0.5f * bufOut;
            if (MathF.Abs(v) < 1e-20f) v = 0f;
            _buf[_idx] = v;
            _idx++;
            if (_idx >= _buf.Length) _idx = 0;
            return bufOut - 0.5f * v;
        }
    }
}

public static class SoftLimiterEffect
{
    public static void Process(float[] buffer, int offset, int count, bool enabled)
    {
        for (int i = offset; i < offset + count; i++)
        {
            float s = buffer[i];
            if (!float.IsFinite(s))
            {
                buffer[i] = 0f;
                continue;
            }

            if (enabled)
            {
                const float knee = 0.82f;
                float abs = MathF.Abs(s);
                if (abs > knee)
                {
                    float sign = MathF.Sign(s);
                    float excess = (abs - knee) / (1f - knee);
                    s = sign * (knee + (0.98f - knee) * MathF.Tanh(excess));
                }
                buffer[i] = Math.Clamp(s, -0.98f, 0.98f);
            }
            else
            {
                buffer[i] = Math.Clamp(s, -1.0f, 1.0f);
            }
        }
    }
}
