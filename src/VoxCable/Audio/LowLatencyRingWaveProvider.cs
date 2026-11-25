using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VoxCable.Audio;

/// <summary>
/// Thread-safe circular byte buffer implementing <see cref="IWaveProvider"/> with:
/// - Strict BlockAlign enforcement
/// - Jitter-buffer priming (pre-roll to target latency before audio release)
/// - Clickless micro-fade on underruns and startup
/// - Overflow clamping and live drift telemetry
/// </summary>
public sealed class LowLatencyRingWaveProvider : IWaveProvider
{
    private readonly byte[] _buffer;
    private readonly int _capacity;
    private readonly int _blockAlign;
    private readonly int _targetBytes;
    private readonly int _maxBytes;
    private readonly int _primeBytes;
    private readonly bool _isIeeeFloat;
    private readonly int _channels;
    private readonly object _sync = new();

    private int _readPos;
    private int _writePos;
    private int _count;
    private bool _priming = true;
    private bool _needsFadeIn;
    private long _underrunCount;
    private long _overflowDropCount;

    public LowLatencyRingWaveProvider(
        WaveFormat waveFormat,
        int targetLatencyMs = 35,
        int maxLatencyMs = 95,
        bool enablePriming = true)
    {
        WaveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
        _blockAlign = Math.Max(1, waveFormat.BlockAlign);
        _channels = Math.Max(1, waveFormat.Channels);
        _isIeeeFloat = IsIeeeFloatFormat(waveFormat);

        int bytesPerSecond = Math.Max(_blockAlign, waveFormat.AverageBytesPerSecond);
        int bufferMs = Math.Max(500, maxLatencyMs * 4);

        _capacity = AlignDown((int)((long)bytesPerSecond * bufferMs / 1000L), _blockAlign);
        _capacity = Math.Max(_capacity, _blockAlign * 1024);

        _targetBytes = Math.Clamp(
            AlignDown((int)((long)bytesPerSecond * Math.Max(10, targetLatencyMs) / 1000L), _blockAlign),
            _blockAlign,
            _capacity / 2);

        _maxBytes = Math.Clamp(
            AlignDown((int)((long)bytesPerSecond * Math.Max(targetLatencyMs + 25, maxLatencyMs) / 1000L), _blockAlign),
            _targetBytes + _blockAlign * 4,
            _capacity);

        // Prime to ~65% of target so playback starts safely inside the steady window
        _primeBytes = enablePriming
            ? Math.Max(_blockAlign, AlignDown((int)(_targetBytes * 0.65), _blockAlign))
            : 0;
        _priming = enablePriming;

        TargetLatencyMs = bytesPerSecond > 0 ? _targetBytes * 1000.0 / bytesPerSecond : targetLatencyMs;
        _buffer = new byte[_capacity];
    }

    public WaveFormat WaveFormat { get; }
    public double TargetLatencyMs { get; }
    public long UnderrunCount => Interlocked.Read(ref _underrunCount);
    public long OverflowDropCount => Interlocked.Read(ref _overflowDropCount);

    public int BufferedBytes
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public double BufferedMilliseconds
    {
        get
        {
            int bytes = BufferedBytes;
            return WaveFormat.AverageBytesPerSecond > 0
                ? bytes * 1000.0 / WaveFormat.AverageBytesPerSecond
                : 0.0;
        }
    }

    public void Write(byte[] data, int offset, int count)
    {
        count = AlignDown(count, _blockAlign);
        if (count <= 0) return;

        lock (_sync)
        {
            if (count >= _capacity)
            {
                int tail = AlignDown(_capacity - _blockAlign, _blockAlign);
                int skip = AlignDown(count - tail, _blockAlign);
                offset += skip;
                count -= skip;
                _readPos = 0;
                _writePos = 0;
                _count = 0;
                Interlocked.Increment(ref _overflowDropCount);
            }

            int free = _capacity - _count;
            if (count > free)
            {
                int drop = AlignUp(count - free, _blockAlign);
                DropOldestLocked(drop);
                Interlocked.Increment(ref _overflowDropCount);
            }

            int firstPart = Math.Min(count, _capacity - _writePos);
            Buffer.BlockCopy(data, offset, _buffer, _writePos, firstPart);
            int remaining = count - firstPart;
            if (remaining > 0)
            {
                Buffer.BlockCopy(data, offset + firstPart, _buffer, 0, remaining);
            }

            _writePos = (_writePos + count) % _capacity;
            _count += count;

            if (_priming && _count >= _primeBytes)
            {
                _priming = false;
                _needsFadeIn = true;
            }

            // Safety ceiling if drift exceeds resampler compensation envelope
            if (_count > _maxBytes)
            {
                int excess = AlignDown(_count - _targetBytes, _blockAlign);
                if (excess > 0)
                {
                    DropOldestLocked(excess);
                    _needsFadeIn = true;
                    Interlocked.Increment(ref _overflowDropCount);
                }
            }
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int alignedCount = AlignDown(count, _blockAlign);
        if (alignedCount <= 0) return 0;

        int readBytes = 0;
        bool applyFadeIn = false;
        bool underrun = false;

        lock (_sync)
        {
            if (_priming)
            {
                if (_count >= _primeBytes)
                {
                    _priming = false;
                    _needsFadeIn = true;
                }
                else
                {
                    Array.Clear(buffer, offset, alignedCount);
                    return alignedCount;
                }
            }

            readBytes = AlignDown(Math.Min(alignedCount, _count), _blockAlign);
            if (readBytes > 0)
            {
                int firstPart = Math.Min(readBytes, _capacity - _readPos);
                Buffer.BlockCopy(_buffer, _readPos, buffer, offset, firstPart);
                int remaining = readBytes - firstPart;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(_buffer, 0, buffer, offset + firstPart, remaining);
                }

                _readPos = (_readPos + readBytes) % _capacity;
                _count -= readBytes;
            }

            if (_needsFadeIn && readBytes > 0)
            {
                applyFadeIn = true;
                _needsFadeIn = false;
            }

            if (readBytes < alignedCount)
            {
                underrun = true;
                if (_primeBytes > 0)
                {
                    _priming = true;
                }
            }
        }

        if (applyFadeIn && _isIeeeFloat)
        {
            ApplyEdgeFadeFloat(buffer, offset, readBytes, fadeIn: true);
        }

        if (underrun)
        {
            if (readBytes > 0)
            {
                Interlocked.Increment(ref _underrunCount);
                if (_isIeeeFloat)
                {
                    ApplyEdgeFadeFloat(buffer, offset, readBytes, fadeIn: false);
                }
            }
            Array.Clear(buffer, offset + readBytes, alignedCount - readBytes);
        }

        return alignedCount;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _readPos = 0;
            _writePos = 0;
            _count = 0;
            _priming = _primeBytes > 0;
            _needsFadeIn = false;
            Array.Clear(_buffer, 0, _buffer.Length);
        }
    }

    private void ApplyEdgeFadeFloat(byte[] buffer, int offset, int byteCount, bool fadeIn)
    {
        int totalFrames = byteCount / _blockAlign;
        if (totalFrames <= 1) return;

        int fadeFrames = Math.Min(totalFrames, Math.Max(8, WaveFormat.SampleRate / 500)); // ~2 ms
        Span<float> samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, byteCount));

        if (fadeIn)
        {
            for (int f = 0; f < fadeFrames; f++)
            {
                float gain = 0.5f * (1f - MathF.Cos(MathF.PI * f / fadeFrames));
                int baseIdx = f * _channels;
                for (int c = 0; c < _channels; c++)
                {
                    samples[baseIdx + c] *= gain;
                }
            }
        }
        else
        {
            int startFrame = totalFrames - fadeFrames;
            for (int f = 0; f < fadeFrames; f++)
            {
                float gain = 0.5f * (1f + MathF.Cos(MathF.PI * f / fadeFrames));
                int baseIdx = (startFrame + f) * _channels;
                for (int c = 0; c < _channels; c++)
                {
                    samples[baseIdx + c] *= gain;
                }
            }
        }
    }

    private void DropOldestLocked(int bytesToDrop)
    {
        int drop = Math.Min(bytesToDrop, _count);
        _readPos = (_readPos + drop) % _capacity;
        _count -= drop;
    }

    private static bool IsIeeeFloatFormat(WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        if (fmt is WaveFormatExtensible ext && ext.BitsPerSample == 32)
        {
            // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
            return ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        }
        return false;
    }

    private static int AlignDown(int value, int align) => (value / align) * align;
    private static int AlignUp(int value, int align) => ((value + align - 1) / align) * align;
}

/// <summary>
/// Adaptive micro-resampler that gently nudges playback rate (±0.4%, inaudible) using a PI loop
/// on the upstream ring buffer fill level to eliminate hardware crystal clock-drift clicks.
/// </summary>
public sealed class DriftCompensatingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly LowLatencyRingWaveProvider _ring;
    private readonly double _targetMs;
    private readonly int _channels;

    private float[] _inBuf = Array.Empty<float>();
    private int _inFrames;
    private double _fracPos;
    private float _prevL;
    private float _prevR;
    private double _smoothedErrorMs;
    private double _integralError;
    private volatile float _currentRatio = 1.0f;

    public DriftCompensatingSampleProvider(ISampleProvider source, LowLatencyRingWaveProvider ring)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _channels = source.WaveFormat.Channels;
        if (_channels != 2)
            throw new ArgumentException("DriftCompensatingSampleProvider expects stereo float input.", nameof(source));

        _targetMs = Math.Max(15.0, ring.TargetLatencyMs);
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }
    public float CurrentRateRatio => _currentRatio;

    public int Read(float[] buffer, int offset, int count)
    {
        int outFrames = count / 2;
        if (outFrames <= 0) return 0;

        // Smooth PI control against ring fill level (only active when ring has audio)
        double bufferedMs = _ring.BufferedMilliseconds;
        double ratio = 1.0;

        if (bufferedMs > 2.0)
        {
            double errorMs = bufferedMs - _targetMs;
            _smoothedErrorMs = _smoothedErrorMs * 0.92 + errorMs * 0.08;

            // Deadband of ±3 ms avoids unnecessary rate wobble
            if (Math.Abs(_smoothedErrorMs) > 3.0)
            {
                double effErr = _smoothedErrorMs > 0 ? _smoothedErrorMs - 3.0 : _smoothedErrorMs + 3.0;
                _integralError = Math.Clamp(_integralError + effErr * 0.000015, -0.0025, 0.0025);
                ratio = 1.0 + Math.Clamp(effErr * 0.00014 + _integralError, -0.004, 0.004);
            }
            else
            {
                _integralError *= 0.995;
                ratio = 1.0 + _integralError;
            }
        }
        else
        {
            _smoothedErrorMs *= 0.9;
            _integralError *= 0.95;
        }

        _currentRatio = (float)ratio;

        // Fast bypass when ratio is virtually unity and phase is zero
        if (Math.Abs(ratio - 1.0) < 0.00002 && _inFrames == 0 && _fracPos < 1e-6)
        {
            int direct = _source.Read(buffer, offset, outFrames * 2);
            if (direct >= 2)
            {
                _prevL = buffer[offset + direct - 2];
                _prevR = buffer[offset + direct - 1];
            }
            return direct;
        }

        int neededInFrames = (int)Math.Ceiling(outFrames * ratio + 4);
        EnsureInputCapacity(neededInFrames);

        while (_inFrames < neededInFrames)
        {
            int toReadSamples = (neededInFrames - _inFrames) * 2;
            int readSamples = _source.Read(_inBuf, _inFrames * 2, toReadSamples);
            if (readSamples <= 0) break;
            _inFrames += readSamples / 2;
        }

        if (_inFrames == 0)
        {
            Array.Clear(buffer, offset, outFrames * 2);
            return outFrames * 2;
        }

        int produced = 0;
        for (int f = 0; f < outFrames; f++)
        {
            int i0 = (int)Math.Floor(_fracPos);
            float frac = (float)(_fracPos - i0);

            float l0, r0, l1, r1;
            if (i0 < 0)
            {
                l0 = _prevL;
                r0 = _prevR;
                l1 = _inBuf[0];
                r1 = _inBuf[1];
            }
            else if (i0 >= _inFrames - 1)
            {
                int last = (_inFrames - 1) * 2;
                l0 = l1 = _inBuf[last];
                r0 = r1 = _inBuf[last + 1];
            }
            else
            {
                int p0 = i0 * 2;
                int p1 = p0 + 2;
                l0 = _inBuf[p0];
                r0 = _inBuf[p0 + 1];
                l1 = _inBuf[p1];
                r1 = _inBuf[p1 + 1];
            }

            int dst = offset + f * 2;
            buffer[dst] = l0 + (l1 - l0) * frac;
            buffer[dst + 1] = r0 + (r1 - r0) * frac;

            _fracPos += ratio;
            produced++;
        }

        int consumed = Math.Min(_inFrames, (int)Math.Floor(_fracPos));
        if (consumed > 0)
        {
            int lastConsumed = (consumed - 1) * 2;
            _prevL = _inBuf[lastConsumed];
            _prevR = _inBuf[lastConsumed + 1];

            int remaining = _inFrames - consumed;
            if (remaining > 0)
            {
                Array.Copy(_inBuf, consumed * 2, _inBuf, 0, remaining * 2);
            }
            _inFrames = remaining;
            _fracPos -= consumed;
        }

        return produced * 2;
    }

    private void EnsureInputCapacity(int frames)
    {
        int samples = frames * 2;
        if (_inBuf.Length < samples)
        {
            Array.Resize(ref _inBuf, Math.Max(samples, _inBuf.Length * 2));
        }
    }
}
