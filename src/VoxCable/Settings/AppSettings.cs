using System.IO;
using System.Text.Json;
using VoxCable.Audio;
using VoxCable.Dsp;
using VoxCable.Soundboard;

namespace VoxCable.Settings;

public sealed class PadSettingsEntry
{
    public string? CustomFilePath { get; set; }
    public string? CustomName { get; set; }
    public float Volume { get; set; } = 1.0f;
    public bool Loop { get; set; }
}

public sealed class AppSettings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VoxCable");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // Routing
    public string? InputDeviceId { get; set; }
    public bool InputIsLoopback { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }
    public int LatencyMs { get; set; } = 40;

    // Platform / UX
    public bool GlobalHotkeysEnabled { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoReconnectOnHotPlug { get; set; } = true;
    public bool RemoteDeckEnabled { get; set; } = false;
    public int RemoteDeckPort { get; set; } = 8765;
    public bool RemoteDeckAllowLan { get; set; } = false;

    // Complete atomic DSP snapshot
    public DspParamSnapshot Dsp { get; set; } = new();

    // Legacy backwards-compatible array + rich pad metadata
    public string?[]? CustomSoundPadPaths { get; set; }
    public PadSettingsEntry[]? SoundPads { get; set; }

    // Custom user presets
    public List<CustomPresetData> CustomPresets { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.Sanitize();
                    return loaded;
                }
            }
        }
        catch
        {
            // Ignore corrupt settings file and fall back to defaults
        }

        var fresh = new AppSettings();
        fresh.Sanitize();
        return fresh;
    }

    public static AppSettings LoadFromJson(string json)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded != null)
            {
                loaded.Sanitize();
                return loaded;
            }
        }
        catch
        {
            // Fall through to clean defaults
        }

        var fresh = new AppSettings();
        fresh.Sanitize();
        return fresh;
    }

    public void Sanitize()
    {
        LatencyMs = Math.Clamp(LatencyMs, 15, 120);
        RemoteDeckPort = Math.Clamp(RemoteDeckPort, 1024, 65535);
        Dsp ??= new DspParamSnapshot();
        CustomPresets ??= new List<CustomPresetData>();

        if (SoundPads == null || SoundPads.Length != SoundboardEngine.PadCount)
        {
            var pads = new PadSettingsEntry[SoundboardEngine.PadCount];
            for (int i = 0; i < SoundboardEngine.PadCount; i++)
            {
                pads[i] = (SoundPads != null && i < SoundPads.Length && SoundPads[i] != null)
                    ? SoundPads[i]
                    : new PadSettingsEntry();

                if (string.IsNullOrWhiteSpace(pads[i].CustomFilePath) &&
                    CustomSoundPadPaths != null &&
                    i < CustomSoundPadPaths.Length)
                {
                    pads[i].CustomFilePath = CustomSoundPadPaths[i];
                }
            }
            SoundPads = pads;
        }
        else
        {
            for (int i = 0; i < SoundPads.Length; i++)
            {
                SoundPads[i] ??= new PadSettingsEntry();
                SoundPads[i].Volume = Math.Clamp(SoundPads[i].Volume, 0f, 1.5f);
            }
        }
    }

    /// <summary>
    /// Atomically writes settings via temp file + rename so crashes never truncate settings.json.
    /// </summary>
    public void Save()
    {
        try
        {
            Sanitize();
            Directory.CreateDirectory(SettingsDir);
            string json = JsonSerializer.Serialize(this, JsonOptions);
            string tmpPath = SettingsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // Ignore write errors in read-only environments
        }
    }

    public void ApplyTo(DspParameters p, SoundboardEngine soundboard)
    {
        Sanitize();
        p.ReplaceSnapshot(Dsp with { HoldToBleep = false });

        for (int i = 0; i < SoundboardEngine.PadCount; i++)
        {
            var entry = SoundPads![i];
            soundboard.Pads[i].Volume = Math.Clamp(entry.Volume, 0f, 1.5f);
            soundboard.Pads[i].Loop = entry.Loop;

            if (!string.IsNullOrWhiteSpace(entry.CustomFilePath) && File.Exists(entry.CustomFilePath))
            {
                try
                {
                    soundboard.LoadCustomFile(i, entry.CustomFilePath, entry.CustomName);
                }
                catch
                {
                    // Ignore missing/unreadable custom file
                }
            }
            else if (!string.IsNullOrWhiteSpace(entry.CustomName))
            {
                soundboard.Pads[i].Name = entry.CustomName;
            }
        }
    }

    public void CaptureFrom(
        DspParameters p,
        SoundboardEngine soundboard,
        string? inputId,
        bool isLoopback,
        string? outputId,
        string? monitorId,
        int latencyMs)
    {
        Sanitize();
        InputDeviceId = inputId;
        InputIsLoopback = isLoopback;
        OutputDeviceId = outputId;
        MonitorDeviceId = monitorId;
        LatencyMs = Math.Clamp(latencyMs, 15, 120);
        Dsp = p.Current with { HoldToBleep = false };

        for (int i = 0; i < SoundboardEngine.PadCount; i++)
        {
            var pad = soundboard.Pads[i];
            SoundPads![i] = new PadSettingsEntry
            {
                CustomFilePath = pad.CustomFilePath,
                CustomName = pad.Name != pad.DefaultName ? pad.Name : null,
                Volume = pad.Volume,
                Loop = pad.Loop
            };
        }
    }
}
