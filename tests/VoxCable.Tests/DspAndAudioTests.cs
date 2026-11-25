using System.Diagnostics;
using System.Net.Http;
using NAudio.Wave;
using VoxCable.Audio;
using VoxCable.Dsp;
using VoxCable.Remote;
using VoxCable.Settings;
using VoxCable.Soundboard;
using Xunit;

namespace VoxCable.Tests;

public class DspAndAudioTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void PitchShifter_UpOneOctave_Shifts440HzTo880Hz()
    {
        float[] stereo = CreateStereoSine(440f, SampleRate, seconds: 2.0f);
        var shifter = new PhaseVocoderPitchShifter(SampleRate);

        ProcessInRealisticChunks(stereo, (buf, off, cnt) =>
            shifter.Process(buf, off, cnt, semitones: 12f, mix: 1.0f));

        AssertAllFinite(stereo);
        double dominantHz = EstimateDominantFrequencyHz(stereo, SampleRate, startFrame: SampleRate, frameCount: SampleRate);
        Assert.InRange(dominantHz, 880.0 * 0.94, 880.0 * 1.06);
    }

    [Fact]
    public void PitchShifter_DownOneOctave_Shifts440HzTo220Hz()
    {
        float[] stereo = CreateStereoSine(440f, SampleRate, seconds: 2.0f);
        var shifter = new PhaseVocoderPitchShifter(SampleRate);

        ProcessInRealisticChunks(stereo, (buf, off, cnt) =>
            shifter.Process(buf, off, cnt, semitones: -12f, mix: 1.0f));

        AssertAllFinite(stereo);
        double dominantHz = EstimateDominantFrequencyHz(stereo, SampleRate, startFrame: SampleRate, frameCount: SampleRate);
        Assert.InRange(dominantHz, 220.0 * 0.94, 220.0 * 1.06);
    }

    [Fact]
    public void PitchShifter_PreserveFormantsAndMultiSampleRates_AllFiniteAndAudible()
    {
        int[] rates = { 8000, 16000, 44100, 48000, 96000, 192000 };
        foreach (int sr in rates)
        {
            float[] stereo = CreateStereoSine(300f, sr, seconds: 0.4f);
            var shifter = new PhaseVocoderPitchShifter(sr);
            shifter.Process(stereo, 0, stereo.Length, semitones: -4f, mix: 0.8f, preserveFormants: true, formantSemitones: 2f);
            AssertAllFinite(stereo);
            Assert.Contains(stereo, s => MathF.Abs(s) > 0.05f);
        }
    }

    [Fact]
    public void PitchShifter_ZeroSemitones_PassesDrySignalUnchanged()
    {
        float[] original = CreateStereoSine(440f, SampleRate, seconds: 0.2f);
        float[] copy = (float[])original.Clone();
        var shifter = new PhaseVocoderPitchShifter(SampleRate);

        shifter.Process(copy, 0, copy.Length, semitones: 0f, mix: 1.0f);

        Assert.Equal(original, copy);
    }

    [Fact]
    public void ThreeBandEq_At8kHzBluetoothHfp_NeverProducesNaN()
    {
        // Regression test: 4500 Hz high shelf at 8000 Hz sample rate previously exceeded Nyquist (4000 Hz)
        var eq = new ThreeBandEqEffect(sampleRate: 8000);
        float[] buf = CreateStereoSine(440f, 8000, seconds: 0.5f);
        eq.Process(buf, 0, buf.Length, bassDb: 12f, midDb: -6f, trebleDb: 12f);

        AssertAllFinite(buf);
        Assert.Contains(buf, s => MathF.Abs(s) > 0.05f);
        Assert.All(buf, s => Assert.True(MathF.Abs(s) < 10f));
    }

    [Fact]
    public void ThreeBandEq_LiveSliderSweep_ProducesNoHardClicks()
    {
        var eq = new ThreeBandEqEffect(SampleRate);
        float[] buf = CreateStereoSine(220f, SampleRate, seconds: 0.5f, amplitude: 0.35f);

        int chunk = 480 * 2; // 10ms chunks
        float bass = -10f;
        for (int off = 0; off < buf.Length; off += chunk)
        {
            int n = Math.Min(chunk, buf.Length - off);
            eq.Process(buf, off, n, bassDb: bass, midDb: 0f, trebleDb: 0f);
            bass += 0.4f;
        }

        AssertAllFinite(buf);
        // Check sample-to-sample derivative for sudden state-reset spikes
        float maxDelta = 0f;
        for (int i = 200; i < buf.Length - 2; i += 2)
        {
            float d = MathF.Abs(buf[i + 2] - buf[i]);
            if (d > maxDelta) maxDelta = d;
        }
        Assert.True(maxDelta < 0.25f, $"Unexpected EQ sweep click detected (maxDelta={maxDelta:F4})");
    }

    [Fact]
    public void BroadcastStrip_HpfDenoiserGateCompressor_WorkAsExpected()
    {
        // 1. HPF attenuates 30 Hz rumble strongly while keeping 1 kHz voice
        var hpf = new HighPassFilterEffect(SampleRate);
        float[] rumble = CreateStereoSine(30f, SampleRate, 0.5f, 0.5f);
        hpf.Process(rumble, 0, rumble.Length, cutoffHz: 100f);
        float rumbleTailPeak = PeakSlice(rumble, rumble.Length / 2, rumble.Length);
        Assert.True(rumbleTailPeak < 0.08f, $"Expected 30 Hz rumble attenuated, got {rumbleTailPeak}");

        hpf.Reset();
        float[] voice = CreateStereoSine(1000f, SampleRate, 0.5f, 0.5f);
        hpf.Process(voice, 0, voice.Length, cutoffHz: 100f);
        float voiceTailPeak = PeakSlice(voice, voice.Length / 2, voice.Length);
        Assert.True(voiceTailPeak > 0.45f, $"Expected 1 kHz voice preserved, got {voiceTailPeak}");

        // 2. Spectral Denoiser reduces stationary white noise
        var denoiser = new SpectralDenoiserEffect(SampleRate);
        float[] noise = new float[SampleRate * 2]; // 1 sec stereo
        uint rng = 0x12345678u;
        for (int i = 0; i < noise.Length; i++)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            noise[i] = (((rng & 0xFFFF) / 32768f) - 1f) * 0.08f;
        }
        float beforeRms = RmsSlice(noise, noise.Length / 2, noise.Length);
        denoiser.Process(noise, 0, noise.Length, strength: 0.85f);
        AssertAllFinite(noise);
        float afterRms = RmsSlice(noise, noise.Length / 2, noise.Length);
        Assert.True(afterRms < beforeRms * 0.65f, $"Expected denoiser RMS drop: before={beforeRms:F4}, after={afterRms:F4}");

        // 3. Noise gate closes cleanly below threshold
        var gate = new NoiseGateEffect(SampleRate);
        float[] quiet = CreateStereoSine(440f, SampleRate, seconds: 0.8f, amplitude: 0.002f);
        gate.Process(quiet, 0, quiet.Length, thresholdDb: -25f);
        float gateTail = PeakSlice(quiet, quiet.Length - 2000, quiet.Length);
        Assert.True(gateTail < 0.0005f, $"Expected gate tail < 0.0005, got {gateTail}");
        Assert.False(gate.IsOpen);

        // 4. Compressor engages on loud audio and reports GR
        var comp = new CompressorEffect(SampleRate);
        float[] loud = CreateStereoSine(440f, SampleRate, seconds: 0.5f, amplitude: 0.8f);
        comp.Process(loud, 0, loud.Length, thresholdDb: -20f, ratio: 4f, makeupDb: 0f);
        AssertAllFinite(loud);
        Assert.True(comp.GainReductionDb > 5f, $"Expected >5 dB gain reduction, got {comp.GainReductionDb}");
    }

    [Fact]
    public void AutoTuneTracker_DetectsOffPitchToneAndComputesCorrection()
    {
        var tracker = new AutoTuneTracker(SampleRate);
        // 452 Hz is ~+46 cents sharp of A4 (440 Hz); in A Minor / C Major nearest note is A4 (440 Hz)
        float[] sharpA = CreateStereoSine(452f, SampleRate, seconds: 0.35f, amplitude: 0.4f);
        float corr = 0f;
        ProcessInRealisticChunks(sharpA, (buf, off, cnt) =>
        {
            corr = tracker.AnalyzeAndComputeCorrection(buf, off, cnt, key: 0, AutoTuneScale.Major, strength: 1.0f);
        });
        // Correction should push downward (negative semitones) toward A4
        Assert.InRange(corr, -0.8f, -0.15f);
    }

    [Fact]
    public void RadioEffect_WithSquelch_KeepsSilenceQuietForDiscordVad()
    {
        var radio = new RadioEffect(SampleRate);
        var silence = new float[SampleRate * 2]; // 1 second of pure silence
        radio.Process(silence, 0, silence.Length, drive: 10f, staticLevel: 0.20f, mix: 1.0f, squelch: true);

        AssertAllFinite(silence);
        float peak = PeakSlice(silence, 0, silence.Length);
        Assert.True(peak < 0.0001f, $"Expected squelched silence < 0.0001, got {peak}");
    }

    [Fact]
    public void EchoChorusReverb_ProduceTailsAndRingOutNaturallyWhenDisabled()
    {
        var chorus = new ChorusEffect(SampleRate);
        float[] cBuf = CreateStereoSine(330f, SampleRate, 0.3f);
        chorus.Process(cBuf, 0, cBuf.Length, rateHz: 1.8f, depthMs: 7f, mix: 0.5f);
        AssertAllFinite(cBuf);

        var echo = new EchoEffect(SampleRate);
        var echoBuf = new float[SampleRate * 2];
        echoBuf[0] = 0.8f; echoBuf[1] = 0.8f;
        echo.Process(echoBuf, 0, echoBuf.Length, delayMs: 100f, feedback: 0.5f, mix: 0.8f, inputEnabled: true);
        int delaySampleIdx = (int)(SampleRate * 0.100f) * 2;
        Assert.True(MathF.Abs(echoBuf[delaySampleIdx]) > 0.1f, "Expected delayed echo impulse.");

        // Disabling input lets tail decay naturally to HasTailEnergy == false
        var decay = new float[SampleRate * 6];
        echo.Process(decay, 0, decay.Length, delayMs: 100f, feedback: 0.4f, mix: 0.8f, inputEnabled: false);
        Assert.False(echo.HasTailEnergy);

        var reverb = new ReverbEffect(SampleRate);
        var revBuf = new float[SampleRate * 2];
        revBuf[0] = 0.9f; revBuf[1] = 0.9f;
        reverb.Process(revBuf, 0, revBuf.Length, roomSize: 0.8f, damping: 0.3f, mix: 0.8f, inputEnabled: true);
        float tailEnergy = 0f;
        for (int i = 4000; i < revBuf.Length; i++) tailEnergy += MathF.Abs(revBuf[i]);
        Assert.True(tailEnergy > 1.0f, "Expected reverb tail energy.");
    }

    [Fact]
    public void HoldToBleep_ReplacesSignalWith1kHzTone()
    {
        var p = new DspParameters { HoldToBleep = true };
        var sb = new SoundboardEngine(SampleRate);
        var dsp = new DspChainSampleProvider(null, p, sb, SampleRate);

        float[] buf = CreateStereoSine(200f, SampleRate, seconds: 0.5f, amplitude: 0.5f);
        ProcessInRealisticChunks(buf, (b, off, cnt) => dsp.ProcessBuffer(b, off, cnt));

        AssertAllFinite(buf);
        double hz = EstimateDominantFrequencyHz(buf, SampleRate, SampleRate / 4, SampleRate / 4);
        Assert.InRange(hz, 950.0, 1050.0);
    }

    [Fact]
    public void SoftLimiter_ClampsExtremeValuesAndSanitizesNaN()
    {
        float[] buf = { 5.0f, -10.0f, float.NaN, float.PositiveInfinity, 0.25f };
        SoftLimiterEffect.Process(buf, 0, buf.Length, enabled: true);

        AssertAllFinite(buf);
        Assert.True(buf[0] <= 0.985f && buf[0] > 0.8f);
        Assert.True(buf[1] >= -0.985f && buf[1] < -0.8f);
        Assert.Equal(0f, buf[2]);
        Assert.Equal(0f, buf[3]);
        Assert.Equal(0.25f, buf[4], precision: 4);
    }

    [Fact]
    public async Task RingBuffer_PrimingDriftCompensationAndConcurrentStress()
    {
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2); // 384 bytes/ms
        var ring = new LowLatencyRingWaveProvider(fmt, targetLatencyMs: 30, maxLatencyMs: 70, enablePriming: true);

        // Before priming threshold is reached, Read outputs silence
        var readBuf = new byte[384 * 10];
        Array.Fill(readBuf, (byte)0x55);
        int read = ring.Read(readBuf, 0, readBuf.Length);
        Assert.Equal(readBuf.Length, read);
        Assert.All(readBuf, b => Assert.Equal(0, b));

        // Writing 150 ms primes and trims back inside max bound
        var bigChunk = new byte[384 * 150];
        ring.Write(bigChunk, 0, bigChunk.Length);
        Assert.True(ring.BufferedMilliseconds <= 75.0, $"Buffered ms drifted too high: {ring.BufferedMilliseconds}");

        // Test DriftCompensatingSampleProvider stays finite under continuous streaming
        var drift = new DriftCompensatingSampleProvider(ring.ToSampleProvider(), ring);
        var outFloats = new float[960];
        int produced = drift.Read(outFloats, 0, outFloats.Length);
        Assert.Equal(outFloats.Length, produced);
        AssertAllFinite(outFloats);

        // Concurrent producer/consumer race test
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var writer = Task.Run(() =>
        {
            var w = new byte[384 * 5];
            while (!cts.IsCancellationRequested) ring.Write(w, 0, w.Length);
        });
        var reader = Task.Run(() =>
        {
            var r = new byte[384 * 5];
            while (!cts.IsCancellationRequested) ring.Read(r, 0, r.Length);
        });
        await Task.WhenAll(writer, reader);
    }

    [Fact]
    public void ChannelAdapters_XlrLeftOnly_SurroundSafeDownmix_AndExactFloatWaveProvider()
    {
        // XLR Left-Only mode duplicates L channel to stereo and drops silent R channel
        var leftOnlySrc = new InterleavedStereoProvider(48000, leftVal: 0.7f, rightVal: 0.0f);
        var xlrNorm = new ChannelNormalizerSampleProvider(leftOnlySrc, () => InputChannelMode.LeftOnly);
        var stereoBuf = new float[8];
        xlrNorm.Read(stereoBuf, 0, stereoBuf.Length);
        Assert.All(stereoBuf, s => Assert.Equal(0.7f, s, precision: 4));

        // 5.1 Surround full-scale input must downmix without exceeding 1.0 (no clipping!)
        var surroundSrc = new ConstantSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 6), 1.0f);
        var surroundNorm = new ChannelNormalizerSampleProvider(surroundSrc);
        var downmixBuf = new float[16];
        surroundNorm.Read(downmixBuf, 0, downmixBuf.Length);
        AssertAllFinite(downmixBuf);
        Assert.All(downmixBuf, s => Assert.InRange(s, 0.5f, 1.0f));

        // ExactFloatWaveProvider preserves exact IEEE float wave format
        var extFmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var exactWave = new ExactFloatWaveProvider(leftOnlySrc, extFmt);
        var byteBuf = new byte[32];
        int bytesRead = exactWave.Read(byteBuf, 0, byteBuf.Length);
        Assert.Equal(32, bytesRead);
    }

    [Fact]
    public void BuiltInSfxSynthAndLockFreeSoundboard_ProduceAudibleAudioAndFadeStop()
    {
        for (int i = 0; i < 8; i++)
        {
            float[]? sfx = BuiltInSfxSynth.Generate(i, SampleRate);
            Assert.NotNull(sfx);
            Assert.True(sfx!.Length > SampleRate / 4);
            AssertAllFinite(sfx);
            Assert.Contains(sfx, s => MathF.Abs(s) > 0.05f);
        }

        var sb = new SoundboardEngine(SampleRate);
        Assert.True(sb.TriggerPad(0));
        var mixBuf = new float[4096];
        sb.MixInto(mixBuf, 0, mixBuf.Length, masterVolume: 0.8f);
        AssertAllFinite(mixBuf);
        Assert.Contains(mixBuf, s => MathF.Abs(s) > 0.01f);
        Assert.True(sb.GetPadProgress(0) > 0f);

        // Smooth fade-out stop
        sb.StopAll();
        var fadeBuf = new float[4096];
        sb.MixInto(fadeBuf, 0, fadeBuf.Length, masterVolume: 0.8f);
        AssertAllFinite(fadeBuf);
        // Tail end after 5ms fade should be pure silence
        float afterFadePeak = PeakSlice(fadeBuf, fadeBuf.Length - 256, fadeBuf.Length);
        Assert.Equal(0f, afterFadePeak);
    }

    [Fact]
    public void AppSettings_MalformedAndNullJson_RecoversSafely()
    {
        var nullPaths = AppSettings.LoadFromJson("""{"CustomSoundPadPaths": null, "SoundPads": null, "LatencyMs": 0}""");
        Assert.NotNull(nullPaths.SoundPads);
        Assert.Equal(SoundboardEngine.PadCount, nullPaths.SoundPads!.Length);
        Assert.InRange(nullPaths.LatencyMs, 15, 120);

        var corrupt = AppSettings.LoadFromJson("{not valid json!!!");
        Assert.NotNull(corrupt.Dsp);
        Assert.Equal(SoundboardEngine.PadCount, corrupt.SoundPads!.Length);
    }

    [Fact]
    public void SmartDefaults_NeverAutoPickIdenticalLoopbackAndOutput()
    {
        var inputs = new List<AudioInputSource>
        {
            new("dev-1", "Speakers", IsLoopback: true, IsDefault: true, IsCommunicationsDefault: false, IsVirtualCable: false),
            new("dev-2", "VB-Audio CABLE Input", IsLoopback: true, IsDefault: false, IsCommunicationsDefault: false, IsVirtualCable: true)
        };
        var outputs = new List<AudioOutputTarget>
        {
            new("dev-1", "Speakers", IsDefault: true, IsCommunicationsDefault: false, IsVirtualCable: false),
            new("dev-2", "VB-Audio CABLE Input", IsDefault: false, IsCommunicationsDefault: false, IsVirtualCable: true)
        };

        var (pickedIn, pickedOut) = AudioDeviceManager.PickSmartDefaults(inputs, outputs);
        Assert.NotNull(pickedIn);
        Assert.NotNull(pickedOut);
        Assert.NotEqual(pickedIn!.Id, pickedOut!.Id);
    }

    [Fact]
    public async Task RemoteDeckServer_ServesTouchDeckAndStateApi()
    {
        int port = 18765 + Random.Shared.Next(0, 1000);
        var state = new RemoteDeckState(true, false, false, false, "Clean", new[] { "Clean" }, new[] { "Airhorn" });
        using var server = new RemoteDeckServer(() => state, _ => { });
        string url = server.Start(port, allowLan: false);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        string html = await http.GetStringAsync(url);
        Assert.Contains("VOXCABLE", html);

        string json = await http.GetStringAsync(url + "api/state");
        Assert.Contains("Airhorn", json);
    }

    [Fact]
    public void ProcessLoopbackCapture_ActivatesCleanlyOnWindows11()
    {
        if (!ProcessLoopbackCapture.IsSupportedOnCurrentOs) return;

        // Validate real COM activation against current process PID (verifies PropVariantBlob & IAgileObject interop)
        using var cap = new ProcessLoopbackCapture((uint)Environment.ProcessId, 48000);
        cap.Start();
        Thread.Sleep(50);
        cap.Stop();
    }

    private static void ProcessInRealisticChunks(float[] buf, Action<float[], int, int> proc)
    {
        const int chunk = 480 * 2; // 10ms stereo @ 48 kHz
        for (int off = 0; off < buf.Length; off += chunk)
        {
            proc(buf, off, Math.Min(chunk, buf.Length - off));
        }
    }

    private static float[] CreateStereoSine(float freqHz, int sampleRate, float seconds, float amplitude = 0.5f)
    {
        int frames = (int)(sampleRate * seconds);
        var buf = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float s = amplitude * MathF.Sin(2f * MathF.PI * freqHz * f / sampleRate);
            buf[f * 2] = s;
            buf[f * 2 + 1] = s;
        }
        return buf;
    }

    private static float PeakSlice(float[] buf, int start, int end)
    {
        float p = 0f;
        for (int i = start; i < end; i++) p = MathF.Max(p, MathF.Abs(buf[i]));
        return p;
    }

    private static float RmsSlice(float[] buf, int start, int end)
    {
        double sum = 0;
        int n = Math.Max(1, end - start);
        for (int i = start; i < end; i++) sum += buf[i] * (double)buf[i];
        return (float)Math.Sqrt(sum / n);
    }

    private static void AssertAllFinite(float[] samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            Assert.True(float.IsFinite(samples[i]), $"Non-finite value at sample {i}");
        }
    }

    private static double EstimateDominantFrequencyHz(float[] interleavedStereo, int sampleRate, int startFrame, int frameCount)
    {
        int crossings = 0;
        float prev = interleavedStereo[startFrame * 2];
        for (int f = 1; f < frameCount; f++)
        {
            float curr = interleavedStereo[(startFrame + f) * 2];
            if (prev <= 0f && curr > 0f) crossings++;
            prev = curr;
        }
        return crossings / (frameCount / (double)sampleRate);
    }

    private sealed class ConstantSampleProvider : ISampleProvider
    {
        private readonly float _value;
        public ConstantSampleProvider(WaveFormat format, float value) { WaveFormat = format; _value = value; }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count) { Array.Fill(buffer, _value, offset, count); return count; }
    }

    private sealed class InterleavedStereoProvider : ISampleProvider
    {
        private readonly float _l, _r;
        public InterleavedStereoProvider(int sampleRate, float leftVal, float rightVal)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
            _l = leftVal;
            _r = rightVal;
        }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = offset; i + 1 < offset + count; i += 2) { buffer[i] = _l; buffer[i + 1] = _r; }
            return count;
        }
    }
}
