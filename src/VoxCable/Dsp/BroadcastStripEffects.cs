namespace VoxCable.Dsp;

/// <summary>
/// Zero-allocation Transposed Direct Form II Biquad filter with Nyquist-safe cutoff clamping
/// and anti-denormal flushing. Updating coefficients preserves delay state (zero zipper clicks).
/// </summary>
public struct BiquadState
{
    private float _b0, _b1, _b2, _a1, _a2;
    private float _z1, _z2;

    public static BiquadState CreateBypass() => new() { _b0 = 1f };

    public void ResetState()
    {
        _z1 = 0f;
        _z2 = 0f;
    }

    public void SetHighPass(int sampleRate, float freqHz, float q = 0.7071f)
    {
        float sr = Math.Max(8000, sampleRate);
        float f = Math.Clamp(freqHz, 20f, sr * 0.44f);
        float w0 = 2f * MathF.PI * f / sr;
        float cosW = MathF.Cos(w0);
        float sinW = MathF.Sin(w0);
        float alpha = sinW / (2f * Math.Max(0.1f, q));
        float a0 = 1f + alpha;

        _b0 = ((1f + cosW) * 0.5f) / a0;
        _b1 = -(1f + cosW) / a0;
        _b2 = ((1f + cosW) * 0.5f) / a0;
        _a1 = (-2f * cosW) / a0;
        _a2 = (1f - alpha) / a0;
    }

    public void SetLowPass(int sampleRate, float freqHz, float q = 0.7071f)
    {
        float sr = Math.Max(8000, sampleRate);
        float f = Math.Clamp(freqHz, 40f, sr * 0.44f);
        float w0 = 2f * MathF.PI * f / sr;
        float cosW = MathF.Cos(w0);
        float sinW = MathF.Sin(w0);
        float alpha = sinW / (2f * Math.Max(0.1f, q));
        float a0 = 1f + alpha;

        _b0 = ((1f - cosW) * 0.5f) / a0;
        _b1 = (1f - cosW) / a0;
        _b2 = ((1f - cosW) * 0.5f) / a0;
        _a1 = (-2f * cosW) / a0;
        _a2 = (1f - alpha) / a0;
    }

    public void SetLowShelf(int sampleRate, float freqHz, float q, float gainDb)
    {
        float sr = Math.Max(8000, sampleRate);
        float f = Math.Clamp(freqHz, 20f, sr * 0.44f);
        float a = MathF.Pow(10f, Math.Clamp(gainDb, -24f, 24f) / 40f);
        float w0 = 2f * MathF.PI * f / sr;
        float cosW = MathF.Cos(w0);
        float sinW = MathF.Sin(w0);
        float alpha = sinW / (2f * Math.Max(0.1f, q));
        float twoSqrtAAlpha = 2f * MathF.Sqrt(a) * alpha;

        float a0 = (a + 1f) + (a - 1f) * cosW + twoSqrtAAlpha;
        _b0 = (a * ((a + 1f) - (a - 1f) * cosW + twoSqrtAAlpha)) / a0;
        _b1 = (2f * a * ((a - 1f) - (a + 1f) * cosW)) / a0;
        _b2 = (a * ((a + 1f) - (a - 1f) * cosW - twoSqrtAAlpha)) / a0;
        _a1 = (-2f * ((a - 1f) + (a + 1f) * cosW)) / a0;
        _a2 = ((a + 1f) + (a - 1f) * cosW - twoSqrtAAlpha) / a0;
    }

    public void SetHighShelf(int sampleRate, float freqHz, float q, float gainDb)
    {
        float sr = Math.Max(8000, sampleRate);
        float f = Math.Clamp(freqHz, 20f, sr * 0.44f);
        float a = MathF.Pow(10f, Math.Clamp(gainDb, -24f, 24f) / 40f);
        float w0 = 2f * MathF.PI * f / sr;
        float cosW = MathF.Cos(w0);
        float sinW = MathF.Sin(w0);
        float alpha = sinW / (2f * Math.Max(0.1f, q));
        float twoSqrtAAlpha = 2f * MathF.Sqrt(a) * alpha;

        float a0 = (a + 1f) - (a - 1f) * cosW + twoSqrtAAlpha;
        _b0 = (a * ((a + 1f) + (a - 1f) * cosW + twoSqrtAAlpha)) / a0;
        _b1 = (-2f * a * ((a - 1f) + (a + 1f) * cosW)) / a0;
        _b2 = (a * ((a + 1f) + (a - 1f) * cosW - twoSqrtAAlpha)) / a0;
        _a1 = (2f * ((a - 1f) - (a + 1f) * cosW)) / a0;
        _a2 = ((a + 1f) - (a - 1f) * cosW - twoSqrtAAlpha) / a0;
    }

    public void SetPeakingEq(int sampleRate, float freqHz, float q, float gainDb)
    {
        float sr = Math.Max(8000, sampleRate);
        float f = Math.Clamp(freqHz, 20f, sr * 0.44f);
        float a = MathF.Pow(10f, Math.Clamp(gainDb, -24f, 24f) / 40f);
        float w0 = 2f * MathF.PI * f / sr;
        float cosW = MathF.Cos(w0);
        float sinW = MathF.Sin(w0);
        float alpha = sinW / (2f * Math.Max(0.1f, q));

        float a0 = 1f + alpha / a;
        _b0 = (1f + alpha * a) / a0;
        _b1 = (-2f * cosW) / a0;
        _b2 = (1f - alpha * a) / a0;
        _a1 = (-2f * cosW) / a0;
        _a2 = (1f - alpha / a) / a0;
    }

    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;

        // Flush subnormals to prevent x86 denormal CPU stalls during silence
        if (MathF.Abs(_z1) < 1e-20f) _z1 = 0f;
        if (MathF.Abs(_z2) < 1e-20f) _z2 = 0f;
        return y;
    }
}

/// <summary>
/// 2nd-order Butterworth High-Pass Rumble Filter (40–220 Hz) for killing desk thumps and AC hum.
/// </summary>
public sealed class HighPassFilterEffect
{
    private int _sampleRate = 48000;
    private float _lastHz = float.NaN;
    private BiquadState _hpL = BiquadState.CreateBypass();
    private BiquadState _hpR = BiquadState.CreateBypass();

    public HighPassFilterEffect(int sampleRate = 48000) => SetSampleRate(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _lastHz = float.NaN;
    }

    public void Reset()
    {
        _hpL.ResetState();
        _hpR.ResetState();
    }

    public void Process(float[] buffer, int offset, int count, float cutoffHz)
    {
        cutoffHz = Math.Clamp(cutoffHz, 40f, 220f);
        if (float.IsNaN(_lastHz) || MathF.Abs(cutoffHz - _lastHz) > 0.5f)
        {
            _hpL.SetHighPass(_sampleRate, cutoffHz, 0.7071f);
            _hpR.SetHighPass(_sampleRate, cutoffHz, 0.7071f);
            _lastHz = cutoffHz;
        }

        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            buffer[idx] = _hpL.Process(buffer[idx]);
            buffer[idx + 1] = _hpR.Process(buffer[idx + 1]);
        }
    }
}

/// <summary>
/// Real-time Decision-Directed Spectral Wiener Noise Suppressor (zero per-buffer allocations,
/// stereo-packed 512-pt STFT @ 48 kHz with adaptive noise-floor tracking and musical-noise guard).
/// </summary>
public sealed class SpectralDenoiserEffect
{
    private int _sampleRate;
    private int _fftSize;
    private int _halfFft;
    private int _hop;

    private float[] _sqrtHann = Array.Empty<float>();
    private int[] _bitRev = Array.Empty<int>();
    private float[] _twCos = Array.Empty<float>();
    private float[] _twSin = Array.Empty<float>();

    private float[] _inL = Array.Empty<float>();
    private float[] _inR = Array.Empty<float>();
    private float[] _outL = Array.Empty<float>();
    private float[] _outR = Array.Empty<float>();
    private float[] _olaL = Array.Empty<float>();
    private float[] _olaR = Array.Empty<float>();

    private float[] _fftRe = Array.Empty<float>();
    private float[] _fftIm = Array.Empty<float>();

    private float[] _noisePsd = Array.Empty<float>();
    private float[] _prevGain = Array.Empty<float>();
    private float[] _prevPostSnr = Array.Empty<float>();

    private int _rover;
    private int _initFrames;

    public SpectralDenoiserEffect(int sampleRate = 48000) => Configure(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        sampleRate = Math.Max(8000, sampleRate);
        if (sampleRate == _sampleRate) return;
        Configure(sampleRate);
    }

    private void Configure(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _fftSize = _sampleRate switch
        {
            <= 24000 => 256,
            <= 56000 => 512,
            _ => 1024
        };
        _halfFft = _fftSize / 2;
        _hop = _fftSize / 2; // 50% overlap with sqrt(Hann) analysis/synthesis -> exact unity COLA

        _sqrtHann = new float[_fftSize];
        for (int i = 0; i < _fftSize; i++)
        {
            float hann = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / _fftSize));
            _sqrtHann[i] = MathF.Sqrt(MathF.Max(0f, hann));
        }

        _bitRev = new int[_fftSize];
        int logN = (int)Math.Log2(_fftSize);
        for (int i = 0; i < _fftSize; i++)
        {
            int x = i, rev = 0;
            for (int b = 0; b < logN; b++)
            {
                rev = (rev << 1) | (x & 1);
                x >>= 1;
            }
            _bitRev[i] = rev;
        }

        _twCos = new float[_halfFft];
        _twSin = new float[_halfFft];
        for (int k = 0; k < _halfFft; k++)
        {
            double ang = -2.0 * Math.PI * k / _fftSize;
            _twCos[k] = (float)Math.Cos(ang);
            _twSin[k] = (float)Math.Sin(ang);
        }

        _inL = new float[_fftSize];
        _inR = new float[_fftSize];
        _outL = new float[_hop];
        _outR = new float[_hop];
        _olaL = new float[_fftSize];
        _olaR = new float[_fftSize];
        _fftRe = new float[_fftSize];
        _fftIm = new float[_fftSize];

        int bins = _halfFft + 1;
        _noisePsd = new float[bins];
        _prevGain = new float[bins];
        _prevPostSnr = new float[bins];
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_inL);
        Array.Clear(_inR);
        Array.Clear(_outL);
        Array.Clear(_outR);
        Array.Clear(_olaL);
        Array.Clear(_olaR);
        Array.Fill(_noisePsd, 1e-6f);
        Array.Fill(_prevGain, 1f);
        Array.Fill(_prevPostSnr, 1f);
        _rover = 0;
        _initFrames = 0;
    }

    public void Process(float[] buffer, int offset, int count, float strength)
    {
        strength = Math.Clamp(strength, 0f, 1f);
        if (strength <= 0.01f) return;

        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            _inL[_hop + _rover] = buffer[idx];
            _inR[_hop + _rover] = buffer[idx + 1];

            buffer[idx] = _outL[_rover];
            buffer[idx + 1] = _outR[_rover];

            _rover++;
            if (_rover >= _hop)
            {
                _rover = 0;
                ProcessFrame(strength);
            }
        }
    }

    private void ProcessFrame(float strength)
    {
        for (int i = 0; i < _fftSize; i++)
        {
            float w = _sqrtHann[i];
            _fftRe[i] = _inL[i] * w;
            _fftIm[i] = _inR[i] * w;
        }

        FftInPlace(_fftRe, _fftIm, forward: true);

        float gainFloor = 1f - 0.88f * strength; // Up to ~18 dB clean attenuation
        float overSub = 1.0f + 1.2f * strength;
        float invN2 = 1f / (_fftSize * (float)_fftSize);

        // Estimate frame broadband SNR for speech-presence guard
        float totalSig = 1e-12f;
        float totalNoise = 1e-12f;
        for (int k = 1; k < _halfFft; k++)
        {
            int nk = _fftSize - k;
            float lr = 0.5f * (_fftRe[k] + _fftRe[nk]);
            float li = 0.5f * (_fftIm[k] - _fftIm[nk]);
            float rr = 0.5f * (_fftIm[k] + _fftIm[nk]);
            float ri = 0.5f * (_fftRe[nk] - _fftRe[k]);
            float psd = 0.5f * (lr * lr + li * li + rr * rr + ri * ri) * invN2;
            totalSig += psd;
            totalNoise += _noisePsd[k];
        }

        bool speechLikely = _initFrames >= 12 && (totalSig > totalNoise * 2.8f);
        if (_initFrames < 20) _initFrames++;

        // Apply Wiener suppression symmetrically to unpacked L & R spectra
        for (int k = 0; k <= _halfFft; k++)
        {
            float lr, li, rr, ri;
            if (k == 0 || k == _halfFft)
            {
                lr = _fftRe[k]; li = 0f;
                rr = _fftIm[k]; ri = 0f;
            }
            else
            {
                int nk = _fftSize - k;
                lr = 0.5f * (_fftRe[k] + _fftRe[nk]);
                li = 0.5f * (_fftIm[k] - _fftIm[nk]);
                rr = 0.5f * (_fftIm[k] + _fftIm[nk]);
                ri = 0.5f * (_fftRe[nk] - _fftRe[k]);
            }

            float psd = 0.5f * (lr * lr + li * li + rr * rr + ri * ri) * invN2;

            // Adaptive minimum-statistics noise floor tracker
            if (_initFrames <= 12)
            {
                _noisePsd[k] = _initFrames == 1 ? MathF.Max(psd, 1e-9f) : _noisePsd[k] * 0.85f + psd * 0.15f;
            }
            else if (psd < _noisePsd[k])
            {
                _noisePsd[k] = _noisePsd[k] * 0.92f + psd * 0.08f;
            }
            else if (!speechLikely && psd < _noisePsd[k] * 2.5f)
            {
                _noisePsd[k] = _noisePsd[k] * 0.996f + psd * 0.004f;
            }

            float noise = MathF.Max(_noisePsd[k] * overSub, 1e-11f);
            float postSnr = Math.Clamp(psd / noise, 0f, 50f);
            float priorSnr = 0.88f * (_prevGain[k] * _prevGain[k] * _prevPostSnr[k])
                           + 0.12f * MathF.Max(postSnr - 1f, 0f);

            float wiener = priorSnr / (1f + priorSnr);
            float gain = Math.Clamp(1f - strength * (1f - wiener), gainFloor, 1f);

            _prevGain[k] = gain;
            _prevPostSnr[k] = postSnr;

            lr *= gain; li *= gain;
            rr *= gain; ri *= gain;

            if (k == 0 || k == _halfFft)
            {
                _fftRe[k] = lr;
                _fftIm[k] = rr;
            }
            else
            {
                int nk = _fftSize - k;
                _fftRe[k] = lr - ri;
                _fftIm[k] = li + rr;
                _fftRe[nk] = lr + ri;
                _fftIm[nk] = rr - li;
            }
        }

        FftInPlace(_fftRe, _fftIm, forward: false);

        for (int i = 0; i < _fftSize; i++)
        {
            float w = _sqrtHann[i];
            _olaL[i] += _fftRe[i] * w;
            _olaR[i] += _fftIm[i] * w;
        }

        Array.Copy(_olaL, 0, _outL, 0, _hop);
        Array.Copy(_olaR, 0, _outR, 0, _hop);
        Array.Copy(_olaL, _hop, _olaL, 0, _hop);
        Array.Copy(_olaR, _hop, _olaR, 0, _hop);
        Array.Clear(_olaL, _hop, _hop);
        Array.Clear(_olaR, _hop, _hop);

        Array.Copy(_inL, _hop, _inL, 0, _hop);
        Array.Copy(_inR, _hop, _inR, 0, _hop);
    }

    private void FftInPlace(float[] re, float[] im, bool forward)
    {
        int n = _fftSize;
        for (int i = 0; i < n; i++)
        {
            int j = _bitRev[i];
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        float sinSign = forward ? 1f : -1f;
        for (int len = 2; len <= n; len <<= 1)
        {
            int halfLen = len >> 1;
            int stride = n / len;
            for (int i = 0; i < n; i += len)
            {
                int tw = 0;
                for (int k = 0; k < halfLen; k++)
                {
                    float wRe = _twCos[tw];
                    float wIm = _twSin[tw] * sinSign;
                    tw += stride;

                    int even = i + k;
                    int odd = even + halfLen;
                    float tRe = re[odd] * wRe - im[odd] * wIm;
                    float tIm = re[odd] * wIm + im[odd] * wRe;

                    re[odd] = re[even] - tRe;
                    im[odd] = im[even] - tIm;
                    re[even] += tRe;
                    im[even] += tIm;
                }
            }
        }

        if (!forward)
        {
            float invN = 1f / n;
            for (int i = 0; i < n; i++)
            {
                re[i] *= invN;
                im[i] *= invN;
            }
        }
    }
}

/// <summary>
/// Smooth RMS broadcast vocal compressor with soft knee (6 dB) and live gain-reduction metering.
/// </summary>
public sealed class CompressorEffect
{
    private int _sampleRate;
    private float _attackCoeff;
    private float _releaseCoeff;
    private float _envDb = -96f;
    private volatile float _gainReductionDb;

    public float GainReductionDb => _gainReductionDb;

    public CompressorEffect(int sampleRate = 48000) => SetSampleRate(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _attackCoeff = MathF.Exp(-1f / (0.008f * _sampleRate));  // 8 ms attack
        _releaseCoeff = MathF.Exp(-1f / (0.140f * _sampleRate)); // 140 ms release
    }

    public void Reset()
    {
        _envDb = -96f;
        _gainReductionDb = 0f;
    }

    public void Process(float[] buffer, int offset, int count, float thresholdDb, float ratio, float makeupDb)
    {
        thresholdDb = Math.Clamp(thresholdDb, -45f, -3f);
        ratio = Math.Clamp(ratio, 1f, 12f);
        float makeupLin = MathF.Pow(10f, Math.Clamp(makeupDb, -6f, 18f) / 20f);
        const float kneeWidth = 6f;
        float halfKnee = kneeWidth * 0.5f;
        float slope = 1f - 1f / ratio;

        float maxGr = 0f;
        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            float l = buffer[idx];
            float r = buffer[idx + 1];
            float det = MathF.Sqrt(0.5f * (l * l + r * r) + 1e-12f);
            float levelDb = 20f * MathF.Log10(det);

            float coeff = levelDb > _envDb ? _attackCoeff : _releaseCoeff;
            _envDb = coeff * _envDb + (1f - coeff) * levelDb;

            float over = _envDb - thresholdDb;
            float grDb;
            if (over <= -halfKnee)
            {
                grDb = 0f;
            }
            else if (over < halfKnee)
            {
                float x = over + halfKnee;
                grDb = slope * (x * x) / (2f * kneeWidth);
            }
            else
            {
                grDb = slope * over;
            }

            if (grDb > maxGr) maxGr = grDb;
            float gain = MathF.Pow(10f, -grDb / 20f) * makeupLin;

            buffer[idx] = l * gain;
            buffer[idx + 1] = r * gain;
        }

        _gainReductionDb = MathF.Max(maxGr, _gainReductionDb * 0.85f);
    }
}

/// <summary>
/// Real-time autocorrelation pitch tracker + musical scale quantizer producing smooth
/// semitone corrections to feed the phase vocoder (Auto-Tune).
/// </summary>
public sealed class AutoTuneTracker
{
    private static readonly int[] ChromaticNotes = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
    private static readonly int[] MajorNotes = { 0, 2, 4, 5, 7, 9, 11 };
    private static readonly int[] MinorNotes = { 0, 2, 3, 5, 7, 8, 10 };
    private static readonly int[] PentMajorNotes = { 0, 2, 4, 7, 9 };
    private static readonly int[] PentMinorNotes = { 0, 3, 5, 7, 10 };

    private int _sampleRate;
    private int _winSize;
    private int _minLag;
    private int _maxLag;
    private float[] _ring = Array.Empty<float>();
    private int _writePos;
    private int _samplesSinceEstimate;
    private float _smoothedCorrectionSt;

    public float CurrentCorrectionSemitones => _smoothedCorrectionSt;

    public AutoTuneTracker(int sampleRate = 48000) => SetSampleRate(sampleRate);

    public void SetSampleRate(int sampleRate)
    {
        _sampleRate = Math.Max(8000, sampleRate);
        _winSize = Math.Clamp(_sampleRate / 32, 256, 2048); // ~31 ms analysis window
        _minLag = Math.Max(16, _sampleRate / 900);  // ~900 Hz upper vocal pitch
        _maxLag = Math.Min(_winSize - 16, _sampleRate / 75); // ~75 Hz lower vocal pitch
        _ring = new float[_winSize];
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_ring);
        _writePos = 0;
        _samplesSinceEstimate = 0;
        _smoothedCorrectionSt = 0f;
    }

    /// <summary>
    /// Feeds stereo samples and computes a smoothed semitone correction toward the selected scale.
    /// </summary>
    public float AnalyzeAndComputeCorrection(float[] buffer, int offset, int count, int key, AutoTuneScale scale, float strength)
    {
        strength = Math.Clamp(strength, 0f, 1f);
        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            _ring[_writePos] = 0.5f * (buffer[idx] + buffer[idx + 1]);
            _writePos++;
            if (_writePos >= _winSize) _writePos = 0;
            _samplesSinceEstimate++;
        }

        int hop = Math.Max(128, _winSize / 4);
        if (_samplesSinceEstimate >= hop)
        {
            _samplesSinceEstimate = 0;
            float detectedHz = DetectPitchHz();
            float targetCorr = 0f;

            if (detectedHz >= 75f && detectedHz <= 900f)
            {
                float midi = 69f + 12f * MathF.Log2(detectedHz / 440f);
                float nearestMidi = QuantizeToScale(midi, ((key % 12) + 12) % 12, scale);
                targetCorr = Math.Clamp((nearestMidi - midi) * strength, -6f, 6f);
            }

            // Smooth retune speed (tighter at higher strength)
            float alpha = 0.25f + 0.55f * strength;
            _smoothedCorrectionSt += (targetCorr - _smoothedCorrectionSt) * alpha;
        }

        return _smoothedCorrectionSt;
    }

    private float DetectPitchHz()
    {
        int n = _winSize;
        float energy = 0f;
        for (int i = 0; i < n; i++)
        {
            float s = _ring[i];
            energy += s * s;
        }

        if (energy / n < 0.00025f) return 0f; // Unvoiced / silence

        float bestCorr = 0f;
        int bestLag = -1;
        int step = _sampleRate >= 44100 ? 2 : 1; // Decimated inner loop keeps CPU tiny

        for (int lag = _minLag; lag <= _maxLag; lag++)
        {
            float sum = 0f;
            float normA = 1e-9f;
            float normB = 1e-9f;
            int limit = n - lag;
            for (int i = 0; i < limit; i += step)
            {
                int i0 = (_writePos + i) % n;
                int i1 = (_writePos + i + lag) % n;
                float a = _ring[i0];
                float b = _ring[i1];
                sum += a * b;
                normA += a * a;
                normB += b * b;
            }

            float corr = sum / MathF.Sqrt(normA * normB);
            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestLag = lag;
            }
        }

        return (bestCorr >= 0.62f && bestLag > 0) ? _sampleRate / (float)bestLag : 0f;
    }

    private static float QuantizeToScale(float midi, int key, AutoTuneScale scale)
    {
        int[] notes = scale switch
        {
            AutoTuneScale.Major => MajorNotes,
            AutoTuneScale.Minor => MinorNotes,
            AutoTuneScale.PentatonicMajor => PentMajorNotes,
            AutoTuneScale.PentatonicMinor => PentMinorNotes,
            _ => ChromaticNotes
        };

        int baseOctave = (int)MathF.Floor(midi / 12f);
        float bestNote = MathF.Round(midi);
        float bestDist = float.MaxValue;

        for (int oct = baseOctave - 1; oct <= baseOctave + 1; oct++)
        {
            for (int i = 0; i < notes.Length; i++)
            {
                float candidate = oct * 12 + key + notes[i];
                float dist = MathF.Abs(candidate - midi);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestNote = candidate;
                }
            }
        }

        return bestNote;
    }
}
