using VoxCable.Audio;

namespace VoxCable.Dsp;

public enum AutoTuneScale
{
    Chromatic = 0,
    Major = 1,
    Minor = 2,
    PentatonicMajor = 3,
    PentatonicMinor = 4
}

/// <summary>
/// Immutable parameter snapshot read atomically by the real-time audio thread so preset switches
/// and multi-control updates never produce torn state mid-buffer.
/// </summary>
public sealed record DspParamSnapshot
{
    public bool MasterBypass { get; init; }
    public bool MuteOutput { get; init; }
    public bool HoldToBleep { get; init; }
    public InputChannelMode InputChannelMode { get; init; } = InputChannelMode.Stereo;

    public float InputGainDb { get; init; }

    // Broadcast Strip
    public bool HighPassEnabled { get; init; }
    public float HighPassHz { get; init; } = 85f;

    public bool DenoiseEnabled { get; init; }
    public float DenoiseStrength { get; init; } = 0.65f;

    public bool NoiseGateEnabled { get; init; }
    public float NoiseGateThresholdDb { get; init; } = -42f;

    public bool CompressorEnabled { get; init; }
    public float CompressorThresholdDb { get; init; } = -18f;
    public float CompressorRatio { get; init; } = 3.5f;
    public float CompressorMakeupDb { get; init; } = 3f;

    // Pitch, Formant & Auto-Tune
    public bool PitchEnabled { get; init; }
    public float PitchSemitones { get; init; }
    public float FormantSemitones { get; init; }
    public bool PreserveFormants { get; init; }
    public float PitchMix { get; init; } = 1.0f;

    public bool AutoTuneEnabled { get; init; }
    public int AutoTuneKey { get; init; } // 0 = C .. 11 = B
    public AutoTuneScale AutoTuneScale { get; init; } = AutoTuneScale.Major;
    public float AutoTuneStrength { get; init; } = 0.8f;

    // 3-Band EQ
    public bool EqEnabled { get; init; }
    public float BassDb { get; init; }
    public float MidDb { get; init; }
    public float TrebleDb { get; init; }

    // Creative FX
    public bool ChorusEnabled { get; init; }
    public float ChorusRateHz { get; init; } = 1.4f;
    public float ChorusDepthMs { get; init; } = 6.5f;
    public float ChorusMix { get; init; } = 0.45f;

    public bool RobotEnabled { get; init; }
    public float RobotFreqHz { get; init; } = 75f;
    public float RobotMix { get; init; } = 0.8f;

    public bool RadioEnabled { get; init; }
    public float RadioDrive { get; init; } = 5f;
    public float RadioStatic { get; init; } = 0.03f;
    public float RadioMix { get; init; } = 1.0f;
    public bool RadioSquelch { get; init; } = true;

    public bool BitcrushEnabled { get; init; }
    public float BitcrushBits { get; init; } = 6f;
    public float BitcrushDownsample { get; init; } = 6f;
    public float BitcrushMix { get; init; } = 0.85f;

    public bool EchoEnabled { get; init; }
    public float EchoDelayMs { get; init; } = 260f;
    public float EchoFeedback { get; init; } = 0.45f;
    public float EchoMix { get; init; } = 0.40f;

    public bool ReverbEnabled { get; init; }
    public float ReverbRoomSize { get; init; } = 0.65f;
    public float ReverbDamping { get; init; } = 0.40f;
    public float ReverbMix { get; init; } = 0.35f;

    // Master Output
    public float OutputGainDb { get; init; }
    public bool SoftLimiterEnabled { get; init; } = true;

    // Soundboard & App Loopback & Monitor
    public float SfxVolume { get; init; } = 0.85f;
    public bool RouteSfxThroughFx { get; init; }

    public float AppLoopbackVolume { get; init; } = 0.70f;
    public bool RouteAppThroughFx { get; init; }

    public bool MonitorEnabled { get; init; }
    public bool MonitorVoice { get; init; } = true;
    public bool MonitorSfx { get; init; } = true;
    public float MonitorVolume { get; init; } = 0.80f;
}

/// <summary>
/// Thread-safe parameter bag backed by an atomically swapped <see cref="DspParamSnapshot"/>.
/// Individual property setters remain compatible with direct UI & test mutation, while
/// <see cref="UpdateAtomic"/> applies whole presets in a single atomic pointer swap.
/// </summary>
public sealed class DspParameters
{
    private readonly object _writeLock = new();
    private volatile DspParamSnapshot _snapshot = new();

    public DspParamSnapshot Current => _snapshot;

    public void UpdateAtomic(Func<DspParamSnapshot, DspParamSnapshot> mutator)
    {
        lock (_writeLock)
        {
            _snapshot = mutator(_snapshot);
        }
    }

    public void ReplaceSnapshot(DspParamSnapshot snapshot)
    {
        lock (_writeLock)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }
    }

    public bool MasterBypass { get => _snapshot.MasterBypass; set => UpdateAtomic(s => s with { MasterBypass = value }); }
    public bool MuteOutput { get => _snapshot.MuteOutput; set => UpdateAtomic(s => s with { MuteOutput = value }); }
    public bool HoldToBleep { get => _snapshot.HoldToBleep; set => UpdateAtomic(s => s with { HoldToBleep = value }); }
    public InputChannelMode InputChannelMode { get => _snapshot.InputChannelMode; set => UpdateAtomic(s => s with { InputChannelMode = value }); }

    public float InputGainDb { get => _snapshot.InputGainDb; set => UpdateAtomic(s => s with { InputGainDb = value }); }

    public bool HighPassEnabled { get => _snapshot.HighPassEnabled; set => UpdateAtomic(s => s with { HighPassEnabled = value }); }
    public float HighPassHz { get => _snapshot.HighPassHz; set => UpdateAtomic(s => s with { HighPassHz = value }); }

    public bool DenoiseEnabled { get => _snapshot.DenoiseEnabled; set => UpdateAtomic(s => s with { DenoiseEnabled = value }); }
    public float DenoiseStrength { get => _snapshot.DenoiseStrength; set => UpdateAtomic(s => s with { DenoiseStrength = value }); }

    public bool NoiseGateEnabled { get => _snapshot.NoiseGateEnabled; set => UpdateAtomic(s => s with { NoiseGateEnabled = value }); }
    public float NoiseGateThresholdDb { get => _snapshot.NoiseGateThresholdDb; set => UpdateAtomic(s => s with { NoiseGateThresholdDb = value }); }

    public bool CompressorEnabled { get => _snapshot.CompressorEnabled; set => UpdateAtomic(s => s with { CompressorEnabled = value }); }
    public float CompressorThresholdDb { get => _snapshot.CompressorThresholdDb; set => UpdateAtomic(s => s with { CompressorThresholdDb = value }); }
    public float CompressorRatio { get => _snapshot.CompressorRatio; set => UpdateAtomic(s => s with { CompressorRatio = value }); }
    public float CompressorMakeupDb { get => _snapshot.CompressorMakeupDb; set => UpdateAtomic(s => s with { CompressorMakeupDb = value }); }

    public bool PitchEnabled { get => _snapshot.PitchEnabled; set => UpdateAtomic(s => s with { PitchEnabled = value }); }
    public float PitchSemitones { get => _snapshot.PitchSemitones; set => UpdateAtomic(s => s with { PitchSemitones = value }); }
    public float FormantSemitones { get => _snapshot.FormantSemitones; set => UpdateAtomic(s => s with { FormantSemitones = value }); }
    public bool PreserveFormants { get => _snapshot.PreserveFormants; set => UpdateAtomic(s => s with { PreserveFormants = value }); }
    public float PitchMix { get => _snapshot.PitchMix; set => UpdateAtomic(s => s with { PitchMix = value }); }

    public bool AutoTuneEnabled { get => _snapshot.AutoTuneEnabled; set => UpdateAtomic(s => s with { AutoTuneEnabled = value }); }
    public int AutoTuneKey { get => _snapshot.AutoTuneKey; set => UpdateAtomic(s => s with { AutoTuneKey = value }); }
    public AutoTuneScale AutoTuneScale { get => _snapshot.AutoTuneScale; set => UpdateAtomic(s => s with { AutoTuneScale = value }); }
    public float AutoTuneStrength { get => _snapshot.AutoTuneStrength; set => UpdateAtomic(s => s with { AutoTuneStrength = value }); }

    public bool EqEnabled { get => _snapshot.EqEnabled; set => UpdateAtomic(s => s with { EqEnabled = value }); }
    public float BassDb { get => _snapshot.BassDb; set => UpdateAtomic(s => s with { BassDb = value }); }
    public float MidDb { get => _snapshot.MidDb; set => UpdateAtomic(s => s with { MidDb = value }); }
    public float TrebleDb { get => _snapshot.TrebleDb; set => UpdateAtomic(s => s with { TrebleDb = value }); }

    public bool ChorusEnabled { get => _snapshot.ChorusEnabled; set => UpdateAtomic(s => s with { ChorusEnabled = value }); }
    public float ChorusRateHz { get => _snapshot.ChorusRateHz; set => UpdateAtomic(s => s with { ChorusRateHz = value }); }
    public float ChorusDepthMs { get => _snapshot.ChorusDepthMs; set => UpdateAtomic(s => s with { ChorusDepthMs = value }); }
    public float ChorusMix { get => _snapshot.ChorusMix; set => UpdateAtomic(s => s with { ChorusMix = value }); }

    public bool RobotEnabled { get => _snapshot.RobotEnabled; set => UpdateAtomic(s => s with { RobotEnabled = value }); }
    public float RobotFreqHz { get => _snapshot.RobotFreqHz; set => UpdateAtomic(s => s with { RobotFreqHz = value }); }
    public float RobotMix { get => _snapshot.RobotMix; set => UpdateAtomic(s => s with { RobotMix = value }); }

    public bool RadioEnabled { get => _snapshot.RadioEnabled; set => UpdateAtomic(s => s with { RadioEnabled = value }); }
    public float RadioDrive { get => _snapshot.RadioDrive; set => UpdateAtomic(s => s with { RadioDrive = value }); }
    public float RadioStatic { get => _snapshot.RadioStatic; set => UpdateAtomic(s => s with { RadioStatic = value }); }
    public float RadioMix { get => _snapshot.RadioMix; set => UpdateAtomic(s => s with { RadioMix = value }); }
    public bool RadioSquelch { get => _snapshot.RadioSquelch; set => UpdateAtomic(s => s with { RadioSquelch = value }); }

    public bool BitcrushEnabled { get => _snapshot.BitcrushEnabled; set => UpdateAtomic(s => s with { BitcrushEnabled = value }); }
    public float BitcrushBits { get => _snapshot.BitcrushBits; set => UpdateAtomic(s => s with { BitcrushBits = value }); }
    public float BitcrushDownsample { get => _snapshot.BitcrushDownsample; set => UpdateAtomic(s => s with { BitcrushDownsample = value }); }
    public float BitcrushMix { get => _snapshot.BitcrushMix; set => UpdateAtomic(s => s with { BitcrushMix = value }); }

    public bool EchoEnabled { get => _snapshot.EchoEnabled; set => UpdateAtomic(s => s with { EchoEnabled = value }); }
    public float EchoDelayMs { get => _snapshot.EchoDelayMs; set => UpdateAtomic(s => s with { EchoDelayMs = value }); }
    public float EchoFeedback { get => _snapshot.EchoFeedback; set => UpdateAtomic(s => s with { EchoFeedback = value }); }
    public float EchoMix { get => _snapshot.EchoMix; set => UpdateAtomic(s => s with { EchoMix = value }); }

    public bool ReverbEnabled { get => _snapshot.ReverbEnabled; set => UpdateAtomic(s => s with { ReverbEnabled = value }); }
    public float ReverbRoomSize { get => _snapshot.ReverbRoomSize; set => UpdateAtomic(s => s with { ReverbRoomSize = value }); }
    public float ReverbDamping { get => _snapshot.ReverbDamping; set => UpdateAtomic(s => s with { ReverbDamping = value }); }
    public float ReverbMix { get => _snapshot.ReverbMix; set => UpdateAtomic(s => s with { ReverbMix = value }); }

    public float OutputGainDb { get => _snapshot.OutputGainDb; set => UpdateAtomic(s => s with { OutputGainDb = value }); }
    public bool SoftLimiterEnabled { get => _snapshot.SoftLimiterEnabled; set => UpdateAtomic(s => s with { SoftLimiterEnabled = value }); }

    public float SfxVolume { get => _snapshot.SfxVolume; set => UpdateAtomic(s => s with { SfxVolume = value }); }
    public bool RouteSfxThroughFx { get => _snapshot.RouteSfxThroughFx; set => UpdateAtomic(s => s with { RouteSfxThroughFx = value }); }

    public float AppLoopbackVolume { get => _snapshot.AppLoopbackVolume; set => UpdateAtomic(s => s with { AppLoopbackVolume = value }); }
    public bool RouteAppThroughFx { get => _snapshot.RouteAppThroughFx; set => UpdateAtomic(s => s with { RouteAppThroughFx = value }); }

    public bool MonitorEnabled { get => _snapshot.MonitorEnabled; set => UpdateAtomic(s => s with { MonitorEnabled = value }); }
    public bool MonitorVoice { get => _snapshot.MonitorVoice; set => UpdateAtomic(s => s with { MonitorVoice = value }); }
    public bool MonitorSfx { get => _snapshot.MonitorSfx; set => UpdateAtomic(s => s with { MonitorSfx = value }); }
    public float MonitorVolume { get => _snapshot.MonitorVolume; set => UpdateAtomic(s => s with { MonitorVolume = value }); }

    public static DspParamSnapshot CleanEffectsPreservingRouting(DspParamSnapshot s) => new()
    {
        MuteOutput = s.MuteOutput,
        HoldToBleep = s.HoldToBleep,
        InputChannelMode = s.InputChannelMode,
        SfxVolume = s.SfxVolume,
        RouteSfxThroughFx = s.RouteSfxThroughFx,
        AppLoopbackVolume = s.AppLoopbackVolume,
        RouteAppThroughFx = s.RouteAppThroughFx,
        MonitorEnabled = s.MonitorEnabled,
        MonitorVoice = s.MonitorVoice,
        MonitorSfx = s.MonitorSfx,
        MonitorVolume = s.MonitorVolume,
        SoftLimiterEnabled = true
    };

    public void ResetEffectsToClean() => UpdateAtomic(CleanEffectsPreservingRouting);
}

public sealed record DspPreset(string Name, Func<DspParamSnapshot, DspParamSnapshot> Transform)
{
    public void Apply(DspParameters parameters)
    {
        parameters.UpdateAtomic(s => Transform(DspParameters.CleanEffectsPreservingRouting(s)));
    }
}

public sealed class CustomPresetData
{
    public string Name { get; set; } = "Custom";
    public DspParamSnapshot Snapshot { get; set; } = new();
}

public static class DspPresets
{
    public static IReadOnlyList<DspPreset> All { get; } = new DspPreset[]
    {
        new("Clean", s => s),

        new("Broadcast Studio", s => s with
        {
            HighPassEnabled = true,
            HighPassHz = 85f,
            DenoiseEnabled = true,
            DenoiseStrength = 0.55f,
            NoiseGateEnabled = true,
            NoiseGateThresholdDb = -44f,
            CompressorEnabled = true,
            CompressorThresholdDb = -18f,
            CompressorRatio = 3.2f,
            CompressorMakeupDb = 3.5f,
            EqEnabled = true,
            BassDb = 2.0f,
            MidDb = -0.5f,
            TrebleDb = 2.5f
        }),

        new("Natural Deep", s => s with
        {
            PitchEnabled = true,
            PitchSemitones = -3.5f,
            PreserveFormants = true,
            FormantSemitones = -1.0f,
            PitchMix = 1.0f,
            EqEnabled = true,
            BassDb = 2.5f,
            TrebleDb = 1.0f
        }),

        new("Chipmunk", s => s with
        {
            PitchEnabled = true,
            PitchSemitones = 7.0f,
            PreserveFormants = false,
            PitchMix = 1.0f,
            EqEnabled = true,
            BassDb = -4f,
            MidDb = 1.5f,
            TrebleDb = 4f
        }),

        new("Deep Demon", s => s with
        {
            PitchEnabled = true,
            PitchSemitones = -6.0f,
            PreserveFormants = false,
            PitchMix = 1.0f,
            EqEnabled = true,
            BassDb = 6f,
            MidDb = -1f,
            TrebleDb = -2f,
            ReverbEnabled = true,
            ReverbRoomSize = 0.75f,
            ReverbDamping = 0.45f,
            ReverbMix = 0.30f
        }),

        new("Clockwork Robot", s => s with
        {
            RobotEnabled = true,
            RobotFreqHz = 68f,
            RobotMix = 0.85f,
            EchoEnabled = true,
            EchoDelayMs = 75f,
            EchoFeedback = 0.35f,
            EchoMix = 0.20f
        }),

        new("Walkie-Talkie", s => s with
        {
            RadioEnabled = true,
            RadioDrive = 6.5f,
            RadioStatic = 0.04f,
            RadioMix = 1.0f,
            RadioSquelch = true
        }),

        new("Cave Echo", s => s with
        {
            EchoEnabled = true,
            EchoDelayMs = 340f,
            EchoFeedback = 0.55f,
            EchoMix = 0.45f,
            ReverbEnabled = true,
            ReverbRoomSize = 0.88f,
            ReverbDamping = 0.30f,
            ReverbMix = 0.50f
        }),

        new("Retro 8-Bit", s => s with
        {
            BitcrushEnabled = true,
            BitcrushBits = 5f,
            BitcrushDownsample = 7f,
            BitcrushMix = 0.90f
        }),

        new("Harmonizer", s => s with
        {
            PitchEnabled = true,
            PitchSemitones = 5.0f,
            PreserveFormants = true,
            PitchMix = 0.45f,
            ReverbEnabled = true,
            ReverbRoomSize = 0.65f,
            ReverbDamping = 0.40f,
            ReverbMix = 0.30f
        }),

        new("Auto-Tune Pop", s => s with
        {
            HighPassEnabled = true,
            CompressorEnabled = true,
            CompressorThresholdDb = -18f,
            CompressorRatio = 3.5f,
            CompressorMakeupDb = 3.0f,
            AutoTuneEnabled = true,
            AutoTuneKey = 0,
            AutoTuneScale = AutoTuneScale.Major,
            AutoTuneStrength = 0.90f,
            ReverbEnabled = true,
            ReverbRoomSize = 0.55f,
            ReverbDamping = 0.45f,
            ReverbMix = 0.22f
        }),

        new("Space Alien", s => s with
        {
            PitchEnabled = true,
            PitchSemitones = 2.5f,
            PreserveFormants = true,
            FormantSemitones = -4.5f,
            PitchMix = 1.0f,
            ChorusEnabled = true,
            ChorusRateHz = 2.4f,
            ChorusDepthMs = 8.5f,
            ChorusMix = 0.55f,
            ReverbEnabled = true,
            ReverbRoomSize = 0.78f,
            ReverbDamping = 0.35f,
            ReverbMix = 0.35f
        })
    };
}
