using System.IO;
using System.Text;
using System.Windows;
using VoxCable.Audio;
using VoxCable.Dsp;
using VoxCable.Platform;
using VoxCable.Soundboard;

namespace VoxCable;

public partial class App : Application
{
    private SingleInstanceGuard? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashLogger.LogError("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLogger.LogError("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            CrashLogger.LogError("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                $"VoxCable encountered an unexpected error:\n{args.Exception.Message}\n\nDetails written to:\n{CrashLogger.LogFilePath}",
                "VoxCable Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        if (e.Args.Any(a => string.Equals(a, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            string? outPath = null;
            for (int i = 0; i < e.Args.Length - 1; i++)
            {
                if (string.Equals(e.Args[i], "--smoke-test-out", StringComparison.OrdinalIgnoreCase))
                {
                    outPath = e.Args[i + 1];
                    break;
                }
            }

            int exitCode = RunSmokeTest(outPath);
            Shutdown(exitCode);
            return;
        }

        _singleInstance = new SingleInstanceGuard();
        if (!_singleInstance.IsPrimaryInstance)
        {
            _singleInstance.SignalExistingInstance();
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;

        _singleInstance.ActivationRequested += () =>
        {
            Dispatcher.BeginInvoke(() => window.RestoreFromTray());
        };

        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static int RunSmokeTest(string? outPath)
    {
        var log = new StringBuilder();
        try
        {
            var (inputs, outputs) = AudioDeviceManager.EnumerateDevices();
            var (defIn, defOut) = AudioDeviceManager.PickSmartDefaults(inputs, outputs);
            log.AppendLine($"Enumerated Inputs: {inputs.Count}, Outputs: {outputs.Count}");
            log.AppendLine($"Smart Default Pair: {defIn?.DisplayName ?? "(none)"} -> {defOut?.DisplayName ?? "(none)"}");
            log.AppendLine($"Win11 Per-App Process Loopback Supported: {ProcessLoopbackCapture.IsSupportedOnCurrentOs}");

            foreach (var input in inputs.Take(5))
                log.AppendLine($"  IN : {input.DisplayName}");
            foreach (var output in outputs.Take(5))
                log.AppendLine($"  OUT: {output.DisplayName}");

            var audioProcs = AudioDeviceManager.EnumerateAudioProcesses();
            log.AppendLine($"Active Audio Processes: {audioProcs.Count}");

            // Validate full DSP chain across multiple Windows sample rates including 8 kHz Bluetooth HFP & 96 kHz
            int[] sampleRates = { 8000, 48000, 96000 };
            foreach (int sampleRate in sampleRates)
            {
                var parameters = new DspParameters();
                var soundboard = new SoundboardEngine(sampleRate);
                var dsp = new DspChainSampleProvider(null, parameters, soundboard, sampleRate);

                var testAudio = new float[sampleRate * 2];
                for (int f = 0; f < sampleRate; f++)
                {
                    float s = 0.45f * MathF.Sin(2f * MathF.PI * 440f * f / sampleRate);
                    testAudio[f * 2] = s;
                    testAudio[f * 2 + 1] = s;
                }

                foreach (var preset in DspPresets.All)
                {
                    preset.Apply(parameters);
                    dsp.ResetEffects();

                    var working = (float[])testAudio.Clone();
                    if (preset.Name == "Chipmunk")
                    {
                        soundboard.TriggerPad(0);
                    }

                    // Process in realistic 10ms WASAPI chunks
                    int chunkSamples = Math.Max(64, (sampleRate / 100) * 2);
                    for (int pos = 0; pos < working.Length; pos += chunkSamples)
                    {
                        int n = Math.Min(chunkSamples, working.Length - pos);
                        dsp.ProcessBuffer(working, pos, n);
                    }

                    float maxAbs = 0f;
                    for (int i = 0; i < working.Length; i++)
                    {
                        if (!float.IsFinite(working[i]))
                            throw new InvalidOperationException($"Non-finite sample in preset '{preset.Name}' @ {sampleRate} Hz at index {i}.");
                        float abs = MathF.Abs(working[i]);
                        if (abs > maxAbs) maxAbs = abs;
                    }

                    if (maxAbs < 0.01f)
                        throw new InvalidOperationException($"Preset '{preset.Name}' @ {sampleRate} Hz produced near-silent output ({maxAbs}).");

                    if (sampleRate == 48000)
                        log.AppendLine($"Preset '{preset.Name}' @ 48kHz: OK (Peak={maxAbs:F3})");
                }
                log.AppendLine($"SampleRate {sampleRate} Hz: ALL {DspPresets.All.Count} PRESETS PASSED");
            }

            log.AppendLine("SMOKE TEST PASSED");
            string result = log.ToString();
            Console.WriteLine(result);
            if (!string.IsNullOrWhiteSpace(outPath))
            {
                File.WriteAllText(outPath, result);
            }
            return 0;
        }
        catch (Exception ex)
        {
            log.AppendLine($"SMOKE TEST FAILED: {ex}");
            string result = log.ToString();
            Console.Error.WriteLine(result);
            if (!string.IsNullOrWhiteSpace(outPath))
            {
                File.WriteAllText(outPath, result);
            }
            return 1;
        }
    }
}
