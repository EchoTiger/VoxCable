using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VoxCable.Audio;

public sealed record AudioInputSource(
    string Id,
    string FriendlyName,
    bool IsLoopback,
    bool IsDefault,
    bool IsCommunicationsDefault,
    bool IsVirtualCable)
{
    public string DisplayName
    {
        get
        {
            string kind = IsLoopback ? "[Loopback]" : "[Mic/In]";
            string vTag = IsVirtualCable ? " [Virtual]" : "";
            string defTag = (IsDefault, IsCommunicationsDefault) switch
            {
                (true, true) => " (Default)",
                (true, false) => " (Default)",
                (false, true) => " (Comms)",
                _ => ""
            };
            return $"{kind} {FriendlyName}{vTag}{defTag}";
        }
    }

    public override string ToString() => DisplayName;
}

public sealed record AudioOutputTarget(
    string Id,
    string FriendlyName,
    bool IsDefault,
    bool IsCommunicationsDefault,
    bool IsVirtualCable)
{
    public string DisplayName
    {
        get
        {
            string vTag = IsVirtualCable ? "[Virtual Cable] " : "";
            string defTag = (IsDefault, IsCommunicationsDefault) switch
            {
                (true, true) => " (Default)",
                (true, false) => " (Default)",
                (false, true) => " (Comms)",
                _ => ""
            };
            return $"{vTag}{FriendlyName}{defTag}";
        }
    }

    public override string ToString() => DisplayName;
}

public sealed record AudioSessionProcess(uint ProcessId, string DisplayName, string ProcessName)
{
    public override string ToString() => $"{DisplayName} (PID {ProcessId})";
}

public static class AudioDeviceManager
{
    private static readonly string[] VirtualCableKeywords =
    {
        "VB-Audio", "CABLE Input", "CABLE Output", "VoiceMeeter",
        "Virtual Audio Cable", "Line 1 (Virtual", "Steam Streaming Microphone",
        "Steam Streaming Speakers", "SteelSeries Sonar", "Elgato Wave",
        "Boom Audio", "NVIDIA Broadcast", "Virtual Cable", "BlackHole"
    };

    public static bool IsVirtualCableName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (string kw in VirtualCableKeywords)
        {
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static (IReadOnlyList<AudioInputSource> Inputs, IReadOnlyList<AudioOutputTarget> Outputs) EnumerateDevices()
    {
        var inputs = new List<AudioInputSource>();
        var outputs = new List<AudioOutputTarget>();

        using var enumerator = new MMDeviceEnumerator();

        string? defaultCaptureId = TryGetDefaultDeviceId(enumerator, DataFlow.Capture, Role.Multimedia);
        string? commsCaptureId = TryGetDefaultDeviceId(enumerator, DataFlow.Capture, Role.Communications);
        string? defaultRenderId = TryGetDefaultDeviceId(enumerator, DataFlow.Render, Role.Multimedia);
        string? commsRenderId = TryGetDefaultDeviceId(enumerator, DataFlow.Render, Role.Communications);

        try
        {
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (dev)
                {
                    string name = dev.FriendlyName;
                    inputs.Add(new AudioInputSource(
                        dev.ID,
                        name,
                        IsLoopback: false,
                        IsDefault: string.Equals(dev.ID, defaultCaptureId, StringComparison.OrdinalIgnoreCase),
                        IsCommunicationsDefault: string.Equals(dev.ID, commsCaptureId, StringComparison.OrdinalIgnoreCase),
                        IsVirtualCable: IsVirtualCableName(name)));
                }
            }
        }
        catch
        {
            // Ignore enumeration failure for capture devices if none exist
        }

        try
        {
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (dev)
                {
                    string name = dev.FriendlyName;
                    bool isDefault = string.Equals(dev.ID, defaultRenderId, StringComparison.OrdinalIgnoreCase);
                    bool isComms = string.Equals(dev.ID, commsRenderId, StringComparison.OrdinalIgnoreCase);
                    bool isVirtual = IsVirtualCableName(name);

                    inputs.Add(new AudioInputSource(
                        dev.ID,
                        name,
                        IsLoopback: true,
                        IsDefault: isDefault,
                        IsCommunicationsDefault: isComms,
                        IsVirtualCable: isVirtual));

                    outputs.Add(new AudioOutputTarget(
                        dev.ID,
                        name,
                        IsDefault: isDefault,
                        IsCommunicationsDefault: isComms,
                        IsVirtualCable: isVirtual));
                }
            }
        }
        catch
        {
            // Ignore enumeration failure for render devices if none exist
        }

        var orderedInputs = inputs
            .OrderBy(i => i.IsLoopback ? 1 : 0)
            .ThenByDescending(i => i.IsDefault)
            .ThenByDescending(i => i.IsCommunicationsDefault)
            .ThenBy(i => i.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var orderedOutputs = outputs
            .OrderByDescending(o => o.IsVirtualCable)
            .ThenByDescending(o => o.IsDefault)
            .ThenBy(o => o.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (orderedInputs, orderedOutputs);
    }

    /// <summary>
    /// Picks a smart non-feedback default pair when no saved preferences exist.
    /// Prefers a real microphone -> Virtual Cable output, and avoids Loopback -> Same Device.
    /// </summary>
    public static (AudioInputSource? Input, AudioOutputTarget? Output) PickSmartDefaults(
        IReadOnlyList<AudioInputSource> inputs,
        IReadOnlyList<AudioOutputTarget> outputs)
    {
        AudioInputSource? pickedIn = inputs.FirstOrDefault(i => !i.IsLoopback && i.IsDefault)
                                     ?? inputs.FirstOrDefault(i => !i.IsLoopback)
                                     ?? inputs.FirstOrDefault();

        AudioOutputTarget? pickedOut = null;
        if (pickedIn != null && pickedIn.IsLoopback)
        {
            // Never default a loopback input to the identical output device!
            pickedOut = outputs.FirstOrDefault(o =>
                            o.IsVirtualCable && !string.Equals(o.Id, pickedIn.Id, StringComparison.OrdinalIgnoreCase))
                        ?? outputs.FirstOrDefault(o =>
                            !string.Equals(o.Id, pickedIn.Id, StringComparison.OrdinalIgnoreCase))
                        ?? outputs.FirstOrDefault();
        }
        else
        {
            pickedOut = outputs.FirstOrDefault(o => o.IsVirtualCable)
                        ?? outputs.FirstOrDefault(o => o.IsDefault)
                        ?? outputs.FirstOrDefault();
        }

        return (pickedIn, pickedOut);
    }

    /// <summary>
    /// Enumerates processes currently holding active Windows render audio sessions for Per-App Process Loopback.
    /// </summary>
    public static IReadOnlyList<AudioSessionProcess> EnumerateAudioProcesses()
    {
        var found = new Dictionary<uint, AudioSessionProcess>();
        uint selfPid = (uint)Environment.ProcessId;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (dev)
                {
                    try
                    {
                        var sessions = dev.AudioSessionManager.Sessions;
                        for (int i = 0; i < sessions.Count; i++)
                        {
                            var session = sessions[i];
                            uint pid = session.GetProcessID;
                            if (pid == 0 || pid == selfPid || found.ContainsKey(pid)) continue;

                            string procName = "Process";
                            string title = session.DisplayName;
                            try
                            {
                                using var proc = Process.GetProcessById((int)pid);
                                procName = proc.ProcessName;
                                if (string.IsNullOrWhiteSpace(title))
                                {
                                    title = !string.IsNullOrWhiteSpace(proc.MainWindowTitle)
                                        ? $"{procName} — {proc.MainWindowTitle}"
                                        : procName;
                                }
                            }
                            catch
                            {
                                if (string.IsNullOrWhiteSpace(title))
                                    title = $"PID {pid}";
                            }

                            if (title.Length > 44) title = title[..44] + "...";
                            found[pid] = new AudioSessionProcess(pid, title, procName);
                        }
                    }
                    catch
                    {
                        // Some virtual drivers do not expose AudioSessionManager
                    }
                }
            }
        }
        catch
        {
            // Ignore enumeration failure
        }

        return found.Values
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProcessId)
            .ToList();
    }

    public static string TranslateAudioError(Exception ex)
    {
        if (ex is COMException com)
        {
            return (uint)com.HResult switch
            {
                0x8889000A => "Audio device is locked in Exclusive Mode by another app. Close exclusive apps or disable Exclusive Mode in Windows Sound Settings.",
                0x88890004 => "Audio device was disconnected or its Windows sample-rate format changed.",
                0x80070005 => "Microphone access was denied by Windows Privacy Settings (Settings → Privacy & security → Microphone).",
                0x88890008 => "The selected audio device rejected the stream format. Check Windows Sound Settings (48 kHz recommended).",
                0x88890017 => "WASAPI audio engine hit CPU budget limit. Try increasing Latency to Balanced (40 ms) or Safe (70 ms).",
                0x8889000F => "Audio endpoint is currently unavailable or disabled.",
                _ => $"{com.Message} (0x{(uint)com.HResult:X8})"
            };
        }

        return ex.Message;
    }

    private static string? TryGetDefaultDeviceId(MMDeviceEnumerator enumerator, DataFlow flow, Role role)
    {
        try
        {
            using var dev = enumerator.GetDefaultAudioEndpoint(flow, role);
            return dev.ID;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Watches Windows Core Audio endpoint hot-plug, state, and default-device changes using IMMNotificationClient
/// with built-in debouncing so USB/Bluetooth plug events coalesce cleanly.
/// </summary>
public sealed class AudioDeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly System.Threading.Timer _debounceTimer;
    private readonly object _stateLock = new();
    private bool _disposed;
    private string? _lastChangedDeviceId;

    public event Action<string?>? DevicesChanged;

    public AudioDeviceWatcher()
    {
        _debounceTimer = new System.Threading.Timer(_ =>
        {
            string? devId;
            lock (_stateLock)
            {
                if (_disposed) return;
                devId = _lastChangedDeviceId;
                _lastChangedDeviceId = null;
            }
            DevicesChanged?.Invoke(devId);
        }, null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
        }
        catch
        {
            // Ignore if CoreAudio notification registration fails in headless tests
        }
    }

    private void ScheduleNotify(string? deviceId)
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _lastChangedDeviceId = deviceId;
            _debounceTimer.Change(350, Timeout.Infinite);
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => ScheduleNotify(deviceId);
    public void OnDeviceAdded(string pwstrDeviceId) => ScheduleNotify(pwstrDeviceId);
    public void OnDeviceRemoved(string deviceId) => ScheduleNotify(deviceId);
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => ScheduleNotify(defaultDeviceId);
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            _debounceTimer.Dispose();
        }

        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
        try { _enumerator.Dispose(); } catch { }
    }
}
