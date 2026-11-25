using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VoxCable.Audio;

public enum InputChannelMode
{
    Stereo = 0,
    MonoSum = 1,
    LeftOnly = 2,
    RightOnly = 3
}

/// <summary>
/// Converts any input channel layout (mono, stereo, XLR single-sided, 5.1/7.1 surround) into
/// standardized 2-channel Stereo IEEE Float with safe headroom normalization.
/// </summary>
public sealed class ChannelNormalizerSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private readonly Func<InputChannelMode>? _modeGetter;
    private float[] _scratch = Array.Empty<float>();

    public ChannelNormalizerSampleProvider(ISampleProvider source, Func<InputChannelMode>? modeGetter = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sourceChannels = Math.Max(1, source.WaveFormat.Channels);
        _modeGetter = modeGetter;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int framesRequested = count / 2;
        if (framesRequested <= 0) return 0;

        InputChannelMode mode = _modeGetter?.Invoke() ?? InputChannelMode.Stereo;

        if (_sourceChannels == 2 && mode == InputChannelMode.Stereo)
        {
            return _source.Read(buffer, offset, framesRequested * 2);
        }

        int sourceSamplesNeeded = framesRequested * _sourceChannels;
        if (_scratch.Length < sourceSamplesNeeded)
        {
            _scratch = new float[sourceSamplesNeeded];
        }

        int samplesRead = _source.Read(_scratch, 0, sourceSamplesNeeded);
        int framesRead = samplesRead / _sourceChannels;

        if (_sourceChannels == 1)
        {
            for (int f = 0; f < framesRead; f++)
            {
                float s = _scratch[f];
                int dst = offset + f * 2;
                buffer[dst] = s;
                buffer[dst + 1] = s;
            }
        }
        else if (_sourceChannels == 2)
        {
            for (int f = 0; f < framesRead; f++)
            {
                int src = f * 2;
                int dst = offset + f * 2;
                float l = _scratch[src];
                float r = _scratch[src + 1];
                float mono = mode switch
                {
                    InputChannelMode.LeftOnly => l,
                    InputChannelMode.RightOnly => r,
                    _ => 0.5f * (l + r)
                };
                buffer[dst] = mono;
                buffer[dst + 1] = mono;
            }
        }
        else
        {
            // Multi-channel (>=3): ITU-R BS.775 downmix with clipping-safe normalization
            bool hasSurround = _sourceChannels >= 6;
            bool hasBack = _sourceChannels >= 8;
            const float centerGain = 0.70710678f;
            const float surroundGain = 0.70710678f;
            const float backGain = 0.5f;

            float norm = 1f / (1f + centerGain + (hasSurround ? surroundGain : 0f) + (hasBack ? backGain : 0f));

            for (int f = 0; f < framesRead; f++)
            {
                int src = f * _sourceChannels;
                int dst = offset + f * 2;

                if (mode == InputChannelMode.LeftOnly)
                {
                    float s = _scratch[src];
                    buffer[dst] = s;
                    buffer[dst + 1] = s;
                    continue;
                }

                if (mode == InputChannelMode.RightOnly)
                {
                    float s = _scratch[src + 1];
                    buffer[dst] = s;
                    buffer[dst + 1] = s;
                    continue;
                }

                // Standard Windows channel order: 0=FL, 1=FR, 2=FC, 3=LFE, 4=BL/SL, 5=BR/SR, 6=SL, 7=SR
                float fl = _scratch[src];
                float fr = _scratch[src + 1];
                float fc = _scratch[src + 2] * centerGain;

                float l = fl + fc;
                float r = fr + fc;

                if (hasSurround)
                {
                    l += _scratch[src + 4] * surroundGain;
                    r += _scratch[src + 5] * surroundGain;
                }

                if (hasBack)
                {
                    l += _scratch[src + 6] * backGain;
                    r += _scratch[src + 7] * backGain;
                }

                l *= norm;
                r *= norm;

                if (mode == InputChannelMode.MonoSum)
                {
                    float m = 0.5f * (l + r);
                    l = m;
                    r = m;
                }

                buffer[dst] = l;
                buffer[dst + 1] = r;
            }
        }

        return framesRead * 2;
    }
}

/// <summary>
/// Converts 2-channel Stereo IEEE Float into the exact channel count required by the target WASAPI MixFormat.
/// </summary>
public sealed class OutputChannelAdapter : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _targetChannels;
    private float[] _scratch = Array.Empty<float>();

    public OutputChannelAdapter(ISampleProvider source, int targetChannels)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.WaveFormat.Channels != 2)
            throw new ArgumentException("OutputChannelAdapter expects a 2-channel stereo source.", nameof(source));

        _targetChannels = Math.Max(1, targetChannels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, _targetChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int framesRequested = count / _targetChannels;
        if (framesRequested <= 0) return 0;

        if (_targetChannels == 2)
        {
            return _source.Read(buffer, offset, framesRequested * 2);
        }

        int stereoNeeded = framesRequested * 2;
        if (_scratch.Length < stereoNeeded)
        {
            _scratch = new float[stereoNeeded];
        }

        int stereoRead = _source.Read(_scratch, 0, stereoNeeded);
        int framesRead = stereoRead / 2;

        if (_targetChannels == 1)
        {
            for (int f = 0; f < framesRead; f++)
            {
                int src = f * 2;
                buffer[offset + f] = 0.5f * (_scratch[src] + _scratch[src + 1]);
            }
        }
        else
        {
            for (int f = 0; f < framesRead; f++)
            {
                int src = f * 2;
                int dst = offset + f * _targetChannels;
                buffer[dst] = _scratch[src];
                buffer[dst + 1] = _scratch[src + 1];
                for (int c = 2; c < _targetChannels; c++)
                {
                    buffer[dst + c] = 0f;
                }
            }
        }

        return framesRead * _targetChannels;
    }
}

/// <summary>
/// Directly adapts an <see cref="ISampleProvider"/> to an <see cref="IWaveProvider"/> matching the
/// exact target WASAPI <see cref="WaveFormatExtensible"/> (32-bit IEEE Float) so 5.1/7.1/Spatial
/// endpoints avoid Media Foundation DMO resampling fallbacks.
/// </summary>
public sealed class ExactFloatWaveProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private float[] _floatScratch = Array.Empty<float>();

    public ExactFloatWaveProvider(ISampleProvider source, WaveFormat exactFloatFormat)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        WaveFormat = exactFloatFormat ?? throw new ArgumentNullException(nameof(exactFloatFormat));
    }

    public WaveFormat WaveFormat { get; }

    public static bool Is32BitFloatFormat(WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
            return true;

        if (fmt is WaveFormatExtensible ext && ext.BitsPerSample == 32)
            return ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");

        return false;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int floatCount = count / sizeof(float);
        if (floatCount <= 0) return 0;

        if (_floatScratch.Length < floatCount)
        {
            _floatScratch = new float[floatCount];
        }

        int readFloats = _source.Read(_floatScratch, 0, floatCount);
        if (readFloats <= 0) return 0;

        int bytes = readFloats * sizeof(float);
        MemoryMarshal.AsBytes(_floatScratch.AsSpan(0, readFloats))
            .CopyTo(buffer.AsSpan(offset, bytes));
        return bytes;
    }
}
