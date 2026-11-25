namespace VoxCable.Dsp;

/// <summary>
/// High-performance real-time stereo Phase Vocoder pitch & formant shifter with:
/// - Sample-rate-adaptive FFT size (512 @ &lt;=24 kHz, 1024 @ 44.1/48 kHz, 2048 @ 88.2/96 kHz, 4096 @ 192 kHz)
/// - Precomputed bit-reversal & twiddle LUTs + stereo-packed single complex FFT (~2-4x faster)
/// - Dominant-bin frequency preservation on downward shifts (no noise overwrite warble)
/// - Identity Phase Locking (IPL) around spectral peaks for cleaner speech
/// - Optional spectral-envelope formant preservation & formant shifting
/// - Exact algorithmic dry-delay alignment so partial wet/dry mixes (Harmonizer) have zero comb-filter lag
/// </summary>
public sealed class PhaseVocoderPitchShifter
{
    private const int Oversample = 4;
    private const float OlaGain = 1.0f / 1.5f; // Hann^2 at 4x overlap -> sum(w^2) = 1.5

    private int _sampleRate;
    private int _fftSize;
    private int _halfFft;
    private int _stepSize;
    private int _latency;
    private float _expct;
    private float _freqPerBin;

    private float[] _window = Array.Empty<float>();
    private int[] _bitRev = Array.Empty<int>();
    private float[] _twiddleCos = Array.Empty<float>();
    private float[] _twiddleSin = Array.Empty<float>();

    // Stereo input/output FIFOs & overlap-add accumulators
    private float[] _inFifoL = Array.Empty<float>();
    private float[] _inFifoR = Array.Empty<float>();
    private float[] _outFifoL = Array.Empty<float>();
    private float[] _outFifoR = Array.Empty<float>();
    private float[] _accumL = Array.Empty<float>();
    private float[] _accumR = Array.Empty<float>();

    // Latency-aligned dry delay line for wet/dry mixing
    private float[] _dryDelayL = Array.Empty<float>();
    private float[] _dryDelayR = Array.Empty<float>();
    private int _dryDelayPos;

    // Shared stereo-packed FFT workspace
    private float[] _fftRe = Array.Empty<float>();
    private float[] _fftIm = Array.Empty<float>();

    // Per-channel vocoder analysis/synthesis buffers
    private ChannelState _left = null!;
    private ChannelState _right = null!;

    private int _rover;

    public PhaseVocoderPitchShifter(int sampleRate = 48000)
    {
        Configure(sampleRate);
    }

    public int LatencySamples => _latency;

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
            <= 24000 => 512,
            <= 56000 => 1024,
            <= 112000 => 2048,
            _ => 4096
        };
        _halfFft = _fftSize / 2;
        _stepSize = _fftSize / Oversample;
        _latency = _fftSize - _stepSize;
        _expct = 2.0f * MathF.PI * _stepSize / _fftSize;
        _freqPerBin = _sampleRate / (float)_fftSize;

        _window = new float[_fftSize];
        for (int i = 0; i < _fftSize; i++)
        {
            _window[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / _fftSize));
        }

        // Precompute bit-reversal table
        _bitRev = new int[_fftSize];
        int logN = (int)Math.Log2(_fftSize);
        for (int i = 0; i < _fftSize; i++)
        {
            int x = i;
            int rev = 0;
            for (int b = 0; b < logN; b++)
            {
                rev = (rev << 1) | (x & 1);
                x >>= 1;
            }
            _bitRev[i] = rev;
        }

        // Precompute twiddle factors (N/2 entries for forward angle -2*pi*k/N)
        _twiddleCos = new float[_halfFft];
        _twiddleSin = new float[_halfFft];
        for (int k = 0; k < _halfFft; k++)
        {
            double ang = -2.0 * Math.PI * k / _fftSize;
            _twiddleCos[k] = (float)Math.Cos(ang);
            _twiddleSin[k] = (float)Math.Sin(ang);
        }

        _inFifoL = new float[_fftSize];
        _inFifoR = new float[_fftSize];
        _outFifoL = new float[_fftSize];
        _outFifoR = new float[_fftSize];
        _accumL = new float[_fftSize * 2];
        _accumR = new float[_fftSize * 2];

        _dryDelayL = new float[_latency];
        _dryDelayR = new float[_latency];
        _dryDelayPos = 0;

        _fftRe = new float[_fftSize];
        _fftIm = new float[_fftSize];

        _left = new ChannelState(_halfFft);
        _right = new ChannelState(_halfFft);
        _rover = _latency;
    }

    public void Reset()
    {
        Array.Clear(_inFifoL);
        Array.Clear(_inFifoR);
        Array.Clear(_outFifoL);
        Array.Clear(_outFifoR);
        Array.Clear(_accumL);
        Array.Clear(_accumR);
        Array.Clear(_dryDelayL);
        Array.Clear(_dryDelayR);
        _dryDelayPos = 0;
        _left.Reset();
        _right.Reset();
        _rover = _latency;
    }

    public void Process(
        float[] interleavedStereo,
        int offset,
        int count,
        float semitones,
        float mix,
        bool preserveFormants = false,
        float formantSemitones = 0f)
    {
        bool hasPitch = MathF.Abs(semitones) >= 0.05f;
        bool hasFormant = preserveFormants && MathF.Abs(formantSemitones) >= 0.05f;
        if ((!hasPitch && !hasFormant) || mix <= 0.001f)
            return;

        float pitchFactor = Math.Clamp(MathF.Pow(2f, Math.Clamp(semitones, -12f, 12f) / 12f), 0.5f, 2.0f);
        float formantFactor = preserveFormants
            ? Math.Clamp(MathF.Pow(2f, Math.Clamp(formantSemitones, -12f, 12f) / 12f), 0.5f, 2.0f)
            : pitchFactor;
        mix = Math.Clamp(mix, 0f, 1f);

        int frames = count / 2;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            float inL = interleavedStereo[idx];
            float inR = interleavedStereo[idx + 1];

            // Align dry signal to exact vocoder window latency for phase-tight Harmonizer mixes
            float dryL = _dryDelayL[_dryDelayPos];
            float dryR = _dryDelayR[_dryDelayPos];
            _dryDelayL[_dryDelayPos] = inL;
            _dryDelayR[_dryDelayPos] = inR;
            _dryDelayPos++;
            if (_dryDelayPos >= _latency) _dryDelayPos = 0;

            _inFifoL[_rover] = inL;
            _inFifoR[_rover] = inR;

            float wetL = _outFifoL[_rover - _latency];
            float wetR = _outFifoR[_rover - _latency];
            _rover++;

            if (_rover >= _fftSize)
            {
                _rover = _latency;
                ProcessStereoFrame(pitchFactor, preserveFormants, formantFactor);
            }

            interleavedStereo[idx] = dryL + (wetL - dryL) * mix;
            interleavedStereo[idx + 1] = dryR + (wetR - dryR) * mix;
        }
    }

    private void ProcessStereoFrame(float pitchShift, bool preserveFormants, float formantShift)
    {
        // Pack L (real) + R (imag) into ONE forward complex FFT
        for (int k = 0; k < _fftSize; k++)
        {
            float w = _window[k];
            _fftRe[k] = _inFifoL[k] * w;
            _fftIm[k] = _inFifoR[k] * w;
        }

        FftInPlace(_fftRe, _fftIm, forward: true);

        // Unpack Hermitian-symmetric spectra for L and R simultaneously
        _left.SpecRe[0] = _fftRe[0];
        _left.SpecIm[0] = 0f;
        _right.SpecRe[0] = _fftIm[0];
        _right.SpecIm[0] = 0f;

        _left.SpecRe[_halfFft] = _fftRe[_halfFft];
        _left.SpecIm[_halfFft] = 0f;
        _right.SpecRe[_halfFft] = _fftIm[_halfFft];
        _right.SpecIm[_halfFft] = 0f;

        for (int k = 1; k < _halfFft; k++)
        {
            int nk = _fftSize - k;
            float zr = _fftRe[k];
            float zi = _fftIm[k];
            float zrn = _fftRe[nk];
            float zin = _fftIm[nk];

            _left.SpecRe[k] = 0.5f * (zr + zrn);
            _left.SpecIm[k] = 0.5f * (zi - zin);
            _right.SpecRe[k] = 0.5f * (zi + zin);
            _right.SpecIm[k] = 0.5f * (zrn - zr);
        }

        ProcessChannelSpectrum(_left, pitchShift, preserveFormants, formantShift);
        ProcessChannelSpectrum(_right, pitchShift, preserveFormants, formantShift);

        // Repack L + i*R back into ONE inverse complex FFT
        _fftRe[0] = _left.SpecRe[0];
        _fftIm[0] = _right.SpecRe[0];
        _fftRe[_halfFft] = _left.SpecRe[_halfFft];
        _fftIm[_halfFft] = _right.SpecRe[_halfFft];

        for (int k = 1; k < _halfFft; k++)
        {
            float lr = _left.SpecRe[k];
            float li = _left.SpecIm[k];
            float rr = _right.SpecRe[k];
            float ri = _right.SpecIm[k];

            // Z[k] = L[k] + i * R[k] = (lr - ri) + i*(li + rr)
            _fftRe[k] = lr - ri;
            _fftIm[k] = li + rr;

            // Z[N-k] = conj(L[k]) + i * conj(R[k]) = (lr + ri) + i*(rr - li)
            int nk = _fftSize - k;
            _fftRe[nk] = lr + ri;
            _fftIm[nk] = rr - li;
        }

        FftInPlace(_fftRe, _fftIm, forward: false);

        // Window + Overlap-Add for both channels
        for (int k = 0; k < _fftSize; k++)
        {
            float wg = _window[k] * OlaGain;
            _accumL[k] += _fftRe[k] * wg;
            _accumR[k] += _fftIm[k] * wg;
        }

        Array.Copy(_accumL, 0, _outFifoL, 0, _stepSize);
        Array.Copy(_accumR, 0, _outFifoR, 0, _stepSize);

        Array.Copy(_accumL, _stepSize, _accumL, 0, _fftSize);
        Array.Copy(_accumR, _stepSize, _accumR, 0, _fftSize);
        Array.Clear(_accumL, _fftSize, _stepSize);
        Array.Clear(_accumR, _fftSize, _stepSize);

        Array.Copy(_inFifoL, _stepSize, _inFifoL, 0, _latency);
        Array.Copy(_inFifoR, _stepSize, _inFifoR, 0, _latency);
    }

    private void ProcessChannelSpectrum(ChannelState ch, float pitchShift, bool preserveFormants, float formantShift)
    {
        const float twoPi = 2.0f * MathF.PI;
        const float invTwoPi = 1.0f / twoPi;

        // 1. Analysis: magnitude, phase, true instantaneous frequency
        for (int k = 0; k <= _halfFft; k++)
        {
            float re = ch.SpecRe[k];
            float im = ch.SpecIm[k];
            float magn = MathF.Sqrt(re * re + im * im);
            float phase = MathF.Atan2(im, re);

            float delta = phase - ch.LastPhase[k];
            ch.LastPhase[k] = phase;

            delta -= k * _expct;
            delta -= twoPi * MathF.Round(delta * invTwoPi);
            float dev = Oversample * delta * invTwoPi;

            ch.AnaMagn[k] = magn;
            ch.AnaPhase[k] = phase;
            ch.AnaFreq[k] = (k + dev) * _freqPerBin;
        }

        // Optional spectral-envelope extraction for Formant Preservation / Formant Shift
        if (preserveFormants)
        {
            ComputeSpectralEnvelope(ch.AnaMagn, ch.EnvOrig, _halfFft);
        }

        // 2. Pitch shifting via bin remapping with dominant-bin frequency preservation
        Array.Clear(ch.SynMagn);
        Array.Clear(ch.SynFreq);
        Array.Clear(ch.SynMaxContrib);

        for (int k = 0; k <= _halfFft; k++)
        {
            int dst = (int)(k * pitchShift + 0.5f);
            if (dst < 0 || dst > _halfFft) continue;

            float m = ch.AnaMagn[k];
            ch.SynMagn[dst] += m;
            if (m >= ch.SynMaxContrib[dst])
            {
                ch.SynMaxContrib[dst] = m;
                ch.SynFreq[dst] = ch.AnaFreq[k] * pitchShift;
            }
        }

        // Apply spectral envelope ratio so vocal formants remain natural or shift independently
        if (preserveFormants)
        {
            ComputeSpectralEnvelope(ch.SynMagn, ch.EnvShifted, _halfFft);
            for (int k = 1; k < _halfFft; k++)
            {
                float srcBinF = k / formantShift;
                int s0 = Math.Clamp((int)srcBinF, 1, _halfFft - 1);
                int s1 = Math.Min(s0 + 1, _halfFft - 1);
                float frac = srcBinF - s0;
                float targetEnv = ch.EnvOrig[s0] + (ch.EnvOrig[s1] - ch.EnvOrig[s0]) * frac;
                float curEnv = ch.EnvShifted[k];

                if (curEnv > 1e-6f)
                {
                    float ratio = Math.Clamp(targetEnv / curEnv, 0.20f, 5.0f);
                    ch.SynMagn[k] *= ratio;
                }
            }
        }

        // 3. Identity Phase Locking (IPL) around local spectral peaks + synthesis
        for (int k = 0; k <= _halfFft; k++)
        {
            float magn = ch.SynMagn[k];
            if (magn <= 1e-8f)
            {
                ch.SpecRe[k] = 0f;
                ch.SpecIm[k] = 0f;
                continue;
            }

            float freq = ch.SynFreq[k];
            float binDelta = (freq - k * _freqPerBin) / _freqPerBin;
            float phaseStep = twoPi * binDelta / Oversample + k * _expct;

            float phase = ch.SumPhase[k] + phaseStep;
            phase -= twoPi * MathF.Round(phase * invTwoPi);
            ch.SumPhase[k] = phase;
        }

        // Loose phase-lock non-peak neighbors to nearest local peak to cut vocoder phasiness
        for (int k = 0; k <= _halfFft; k++)
        {
            float magn = ch.SynMagn[k];
            if (magn <= 1e-8f)
            {
                ch.SpecRe[k] = 0f;
                ch.SpecIm[k] = 0f;
                continue;
            }

            float phase = ch.SumPhase[k];
            if (k > 1 && k < _halfFft - 1)
            {
                // If an immediate neighbor is a stronger spectral peak, gently lock phase coherence
                if (ch.SynMagn[k - 1] > magn * 1.4f && ch.SynMagn[k - 1] >= ch.SynMagn[k - 2])
                {
                    float srcBin = (k - 1) / pitchShift;
                    int sb = Math.Clamp((int)(srcBin + 0.5f), 1, _halfFft - 1);
                    float rel = ch.AnaPhase[Math.Min(sb + 1, _halfFft)] - ch.AnaPhase[sb];
                    phase = ch.SumPhase[k - 1] + rel;
                }
                else if (ch.SynMagn[k + 1] > magn * 1.4f && ch.SynMagn[k + 1] >= ch.SynMagn[k + 2])
                {
                    float srcBin = (k + 1) / pitchShift;
                    int sb = Math.Clamp((int)(srcBin + 0.5f), 1, _halfFft - 1);
                    float rel = ch.AnaPhase[Math.Max(sb - 1, 0)] - ch.AnaPhase[sb];
                    phase = ch.SumPhase[k + 1] + rel;
                }
            }

            if (k == 0 || k == _halfFft)
            {
                ch.SpecRe[k] = magn * MathF.Cos(phase);
                ch.SpecIm[k] = 0f;
            }
            else
            {
                ch.SpecRe[k] = magn * MathF.Cos(phase);
                ch.SpecIm[k] = magn * MathF.Sin(phase);
            }
        }
    }

    private static void ComputeSpectralEnvelope(float[] magn, float[] env, int halfFft)
    {
        // Fast moving-average log-magnitude liftering (O(N)) captures vocal tract formants smoothly
        const int radius = 7;
        for (int k = 0; k <= halfFft; k++)
        {
            int lo = Math.Max(1, k - radius);
            int hi = Math.Min(halfFft - 1, k + radius);
            float sum = 0f;
            int count = hi - lo + 1;
            for (int j = lo; j <= hi; j++)
            {
                sum += MathF.Log(magn[j] + 1e-6f);
            }
            env[k] = MathF.Exp(sum / count);
        }
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

        float sinSign = forward ? 1.0f : -1.0f;
        for (int len = 2; len <= n; len <<= 1)
        {
            int halfLen = len >> 1;
            int twiddleStride = n / len;

            for (int i = 0; i < n; i += len)
            {
                int twIdx = 0;
                for (int k = 0; k < halfLen; k++)
                {
                    float wRe = _twiddleCos[twIdx];
                    float wIm = _twiddleSin[twIdx] * sinSign;
                    twIdx += twiddleStride;

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
            float invN = 1.0f / n;
            for (int i = 0; i < n; i++)
            {
                re[i] *= invN;
                im[i] *= invN;
            }
        }
    }

    private sealed class ChannelState
    {
        public readonly float[] SpecRe;
        public readonly float[] SpecIm;
        public readonly float[] LastPhase;
        public readonly float[] SumPhase;
        public readonly float[] AnaMagn;
        public readonly float[] AnaPhase;
        public readonly float[] AnaFreq;
        public readonly float[] SynMagn;
        public readonly float[] SynFreq;
        public readonly float[] SynMaxContrib;
        public readonly float[] EnvOrig;
        public readonly float[] EnvShifted;

        public ChannelState(int halfFft)
        {
            int bins = halfFft + 1;
            SpecRe = new float[bins];
            SpecIm = new float[bins];
            LastPhase = new float[bins];
            SumPhase = new float[bins];
            AnaMagn = new float[bins];
            AnaPhase = new float[bins];
            AnaFreq = new float[bins];
            SynMagn = new float[bins];
            SynFreq = new float[bins];
            SynMaxContrib = new float[bins];
            EnvOrig = new float[bins];
            EnvShifted = new float[bins];
        }

        public void Reset()
        {
            Array.Clear(LastPhase);
            Array.Clear(SumPhase);
        }
    }
}
