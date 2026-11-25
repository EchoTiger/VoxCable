using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VoxCable.Audio;
using VoxCable.Dsp;
using VoxCable.Platform;
using VoxCable.Remote;
using VoxCable.Settings;
using VoxCable.Soundboard;

namespace VoxCable;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly AudioBridgeEngine _engine = new();
    private readonly AudioDeviceWatcher _deviceWatcher = new();
    private readonly GlobalHotkeyManager _hotkeyManager = new();
    private readonly TrayIconManager _trayManager = new();
    private readonly RemoteDeckServer _remoteDeck;

    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _saveDebounceTimer;
    private readonly DispatcherTimer _bleepPollTimer;

    private readonly Button[] _padButtons = new Button[SoundboardEngine.PadsPerBank];
    private readonly Border[] _padProgressBars = new Border[SoundboardEngine.PadsPerBank];
    private readonly ScaleTransform[] _padProgressScales = new ScaleTransform[SoundboardEngine.PadsPerBank];
    private readonly Dictionary<string, Button> _signalPills = new();
    private readonly float[] _waveScratch = new float[128];

    private IReadOnlyList<AudioInputSource> _inputs = Array.Empty<AudioInputSource>();
    private IReadOnlyList<AudioOutputTarget> _outputs = Array.Empty<AudioOutputTarget>();
    private IReadOnlyList<AudioSessionProcess> _audioProcesses = Array.Empty<AudioSessionProcess>();

    private AppSettings _settings = new();
    private string? _activePresetName = "Clean";
    private int _activePadBank; // 0 = pads 0..7, 1 = pads 8..15
    private bool _isSyncingUi;
    private bool _isMiniMode;
    private bool _wasRunningBeforeDisconnect;
    private double _normalWidth = 1040;
    private double _normalHeight = 790;

    public MainWindow()
    {
        InitializeComponent();

        _remoteDeck = new RemoteDeckServer(BuildRemoteDeckState, HandleRemoteDeckCommand);

        SourceInitialized += OnSourceInitialized;
        StateChanged += OnWindowStateChanged;
        Closing += OnWindowClosing;

        _engine.EngineError += msg => Dispatcher.BeginInvoke(() =>
        {
            ShowError(msg);
            UpdateConnectionVisuals();
            _remoteDeck.BroadcastState();
        });

        _engine.StateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            UpdateConnectionVisuals();
            UpdateMeterTimerState();
            _remoteDeck.BroadcastState();
        });

        _deviceWatcher.DevicesChanged += _ => Dispatcher.BeginInvoke(OnSystemAudioDevicesChanged);
        _hotkeyManager.HotkeyPressed += OnGlobalHotkeyPressed;
        _trayManager.RestoreRequested += () => Dispatcher.BeginInvoke(RestoreFromTray);

        _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _saveDebounceTimer.Tick += (_, _) =>
        {
            _saveDebounceTimer.Stop();
            PersistSettingsNow();
        };

        _bleepPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _bleepPollTimer.Tick += (_, _) =>
        {
            if (!GlobalHotkeyManager.IsBleepKeyCurrentlyDown)
            {
                _bleepPollTimer.Stop();
                SetHoldToBleep(false);
            }
        };

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _meterTimer.Tick += OnMeterTimerTick;

        BuildSignalFlowStrip();
        BuildSoundboardPads();

        _settings = AppSettings.Load();
        _settings.ApplyTo(_engine.Parameters, _engine.Soundboard);

        BuildPresetButtons();
        RefreshCustomPresetsList();
        RefreshDevices(selectSaved: true);
        RefreshAudioProcesses();
        SyncUiFromParameters();
        SyncPlatformUiFromSettings();
        RefreshSoundboardPadLabels();
        UpdateBankButtonVisuals();
        UpdateConnectionVisuals();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            int useDark = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
            }
        }
        catch { }

        _hotkeyManager.Attach(hwnd);
        _hotkeyManager.SetEnabled(_settings.GlobalHotkeysEnabled);
        UpdateHotkeyStatusText();

        _trayManager.Attach(this, BuildTrayContextMenu(), "VoxCable Studio — Standby");

        if (_settings.RemoteDeckEnabled)
        {
            StartOrStopRemoteDeck(true);
        }
    }

    private ContextMenu BuildTrayContextMenu()
    {
        var menu = new ContextMenu();
        var showItem = new MenuItem { Header = "Show / Restore VoxCable" };
        showItem.Click += (_, _) => RestoreFromTray();
        var connItem = new MenuItem { Header = "Toggle Connect" };
        connItem.Click += (_, _) => ConnectButton_Click(this, new RoutedEventArgs());
        var muteItem = new MenuItem { Header = "Toggle Mute Out" };
        muteItem.Click += (_, _) => { MuteCheckBox.IsChecked = !(MuteCheckBox.IsChecked == true); };
        var bypassItem = new MenuItem { Header = "Toggle Bypass FX" };
        bypassItem.Click += (_, _) => { BypassCheckBox.IsChecked = !(BypassCheckBox.IsChecked == true); };
        var exitItem = new MenuItem { Header = "Exit VoxCable" };
        exitItem.Click += (_, _) => Close();

        menu.Items.Add(showItem);
        menu.Items.Add(connItem);
        menu.Items.Add(muteItem);
        menu.Items.Add(bypassItem);
        menu.Items.Add(exitItem);
        return menu;
    }

    public void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        UpdateMeterTimerState();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray)
        {
            Hide();
            ShowInTaskbar = false;
        }
        UpdateMeterTimerState();
    }

    private void UpdateMeterTimerState()
    {
        bool shouldTick = _engine.IsRunning && IsVisible && WindowState != WindowState.Minimized;
        if (shouldTick && !_meterTimer.IsEnabled)
        {
            _meterTimer.Start();
        }
        else if (!shouldTick && _meterTimer.IsEnabled)
        {
            _meterTimer.Stop();
            if (!_engine.IsRunning)
            {
                ResetMeterVisuals();
            }
        }
    }

    private void ResetMeterVisuals()
    {
        InputMeterScaleL.ScaleX = 0;
        InputMeterScaleR.ScaleX = 0;
        OutputMeterScaleL.ScaleX = 0;
        OutputMeterScaleR.ScaleX = 0;
        InputPeakText.Text = "-inf dB";
        OutputPeakText.Text = "-inf dB";
        InputClipBadge.Visibility = Visibility.Collapsed;
        OutputClipBadge.Visibility = Visibility.Collapsed;
        CompGrText.Text = "GR: 0.0 dB";
        WaveformPolyline.Points.Clear();
    }

    private void BuildSignalFlowStrip()
    {
        SignalFlowPanel.Children.Clear();
        _signalPills.Clear();

        (string Key, string Label, Action Toggle)[] stages =
        {
            ("HPF", "HPF", () => HighPassEnabledBox.IsChecked = !(HighPassEnabledBox.IsChecked == true)),
            ("DENOISE", "DENOISE", () => DenoiseEnabledBox.IsChecked = !(DenoiseEnabledBox.IsChecked == true)),
            ("GATE", "GATE", () => NoiseGateEnabledBox.IsChecked = !(NoiseGateEnabledBox.IsChecked == true)),
            ("COMP", "COMP", () => CompressorEnabledBox.IsChecked = !(CompressorEnabledBox.IsChecked == true)),
            ("PITCH", "PITCH", () => PitchEnabledBox.IsChecked = !(PitchEnabledBox.IsChecked == true)),
            ("AUTOTUNE", "AUTO-TUNE", () => AutoTuneEnabledBox.IsChecked = !(AutoTuneEnabledBox.IsChecked == true)),
            ("EQ", "EQ", () => EqEnabledBox.IsChecked = !(EqEnabledBox.IsChecked == true)),
            ("CHORUS", "CHORUS", () => ChorusEnabledBox.IsChecked = !(ChorusEnabledBox.IsChecked == true)),
            ("ROBOT", "ROBOT", () => RobotEnabledBox.IsChecked = !(RobotEnabledBox.IsChecked == true)),
            ("RADIO", "RADIO", () => RadioEnabledBox.IsChecked = !(RadioEnabledBox.IsChecked == true)),
            ("CRUSH", "CRUSH", () => BitcrushEnabledBox.IsChecked = !(BitcrushEnabledBox.IsChecked == true)),
            ("ECHO", "ECHO", () => EchoEnabledBox.IsChecked = !(EchoEnabledBox.IsChecked == true)),
            ("REVERB", "REVERB", () => ReverbEnabledBox.IsChecked = !(ReverbEnabledBox.IsChecked == true))
        };

        SignalFlowPanel.Children.Add(new TextBlock
        {
            Text = "SIGNAL CHAIN: ",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });

        for (int i = 0; i < stages.Length; i++)
        {
            var stage = stages[i];
            var btn = new Button
            {
                Content = stage.Label,
                Style = (Style)FindResource("SignalPillStyle"),
                ToolTip = $"Click to toggle {stage.Label}"
            };
            btn.Click += (_, _) => stage.Toggle();
            _signalPills[stage.Key] = btn;
            SignalFlowPanel.Children.Add(btn);

            if (i < stages.Length - 1)
            {
                SignalFlowPanel.Children.Add(new TextBlock
                {
                    Text = "›",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("StatusMutedBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0)
                });
            }
        }
    }

    private void UpdateSignalFlowVisuals()
    {
        var p = _engine.Parameters.Current;
        bool bypassed = p.MasterBypass;
        var activeBg = new SolidColorBrush(Color.FromRgb(18, 52, 78));
        activeBg.Freeze();
        var activeBdr = (Brush)FindResource("AccentCyanBrush");
        var idleBg = (Brush)FindResource("InputBgBrush");
        var idleBdr = (Brush)FindResource("CardBorderBrush");
        var activeFg = (Brush)FindResource("TextPrimaryBrush");
        var idleFg = (Brush)FindResource("TextMutedBrush");

        void SetPill(string key, bool on)
        {
            if (!_signalPills.TryGetValue(key, out var btn)) return;
            bool lit = on && !bypassed;
            btn.Background = lit ? activeBg : idleBg;
            btn.BorderBrush = lit ? activeBdr : idleBdr;
            btn.Foreground = lit ? activeFg : idleFg;
        }

        SetPill("HPF", p.HighPassEnabled);
        SetPill("DENOISE", p.DenoiseEnabled);
        SetPill("GATE", p.NoiseGateEnabled);
        SetPill("COMP", p.CompressorEnabled);
        SetPill("PITCH", p.PitchEnabled);
        SetPill("AUTOTUNE", p.AutoTuneEnabled);
        SetPill("EQ", p.EqEnabled);
        SetPill("CHORUS", p.ChorusEnabled);
        SetPill("ROBOT", p.RobotEnabled);
        SetPill("RADIO", p.RadioEnabled);
        SetPill("CRUSH", p.BitcrushEnabled);
        SetPill("ECHO", p.EchoEnabled);
        SetPill("REVERB", p.ReverbEnabled);
    }

    private void BuildPresetButtons()
    {
        PresetsPanel.Children.Clear();
        foreach (var preset in DspPresets.All)
        {
            var btn = new Button
            {
                Content = preset.Name,
                Style = (Style)FindResource("PresetPillStyle"),
                Tag = preset.Name
            };
            btn.Click += (_, _) => ApplyPresetByName(preset.Name);
            PresetsPanel.Children.Add(btn);
        }

        foreach (var custom in _settings.CustomPresets)
        {
            string name = custom.Name;
            var btn = new Button
            {
                Content = $"★ {name}",
                Style = (Style)FindResource("PresetPillStyle"),
                Tag = name
            };
            btn.Click += (_, _) => ApplyPresetByName(name);
            PresetsPanel.Children.Add(btn);
        }

        UpdatePresetHighlights();
    }

    private void ApplyPresetByName(string presetName)
    {
        var builtIn = DspPresets.All.FirstOrDefault(p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
        if (builtIn != null)
        {
            builtIn.Apply(_engine.Parameters);
            _activePresetName = builtIn.Name;
            SyncUiFromParameters();
            UpdatePresetHighlights();
            ScheduleDebouncedSave();
            _remoteDeck.BroadcastState();
            return;
        }

        var custom = _settings.CustomPresets.FirstOrDefault(p => string.Equals(p.Name, presetName, StringComparison.OrdinalIgnoreCase));
        if (custom != null)
        {
            _engine.Parameters.UpdateAtomic(cur => custom.Snapshot with
            {
                MuteOutput = cur.MuteOutput,
                HoldToBleep = cur.HoldToBleep,
                InputChannelMode = cur.InputChannelMode,
                SfxVolume = cur.SfxVolume,
                RouteSfxThroughFx = cur.RouteSfxThroughFx,
                AppLoopbackVolume = cur.AppLoopbackVolume,
                RouteAppThroughFx = cur.RouteAppThroughFx,
                MonitorEnabled = cur.MonitorEnabled,
                MonitorVoice = cur.MonitorVoice,
                MonitorSfx = cur.MonitorSfx,
                MonitorVolume = cur.MonitorVolume
            });
            _activePresetName = custom.Name;
            SyncUiFromParameters();
            UpdatePresetHighlights();
            ScheduleDebouncedSave();
            _remoteDeck.BroadcastState();
        }
    }

    private void UpdatePresetHighlights()
    {
        var activeBg = new SolidColorBrush(Color.FromRgb(18, 52, 78));
        activeBg.Freeze();
        var activeBdr = (Brush)FindResource("AccentCyanBrush");
        var idleBg = (Brush)FindResource("InputBgBrush");
        var idleBdr = (Brush)FindResource("CardBorderBrush");

        foreach (var child in PresetsPanel.Children)
        {
            if (child is Button btn && btn.Tag is string name)
            {
                bool active = string.Equals(name, _activePresetName, StringComparison.OrdinalIgnoreCase);
                btn.Background = active ? activeBg : idleBg;
                btn.BorderBrush = active ? activeBdr : idleBdr;
            }
        }
    }

    private void BuildSoundboardPads()
    {
        SoundboardGrid.Children.Clear();
        for (int i = 0; i < SoundboardEngine.PadsPerBank; i++)
        {
            int slotInBank = i;
            var btn = new Button
            {
                Style = (Style)FindResource("SoundPadStyle"),
                Margin = new Thickness(5),
                AllowDrop = true
            };

            // Drag & drop custom audio files directly onto any pad
            btn.DragOver += (_, args) =>
            {
                args.Effects = args.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
                args.Handled = true;
            };
            btn.Drop += async (_, args) =>
            {
                if (args.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    await LoadCustomPadFileAsync(GetActualPadIndex(slotInBank), files[0]);
                }
            };

            var menu = new ContextMenu();
            var loadItem = new MenuItem { Header = "Load Audio File (.wav / .mp3 / .m4a)..." };
            loadItem.Click += (_, _) => BrowseAndLoadPadFile(GetActualPadIndex(slotInBank));

            var loopItem = new MenuItem { Header = "Toggle Loop Mode" };
            loopItem.Click += (_, _) =>
            {
                int idx = GetActualPadIndex(slotInBank);
                _engine.Soundboard.Pads[idx].Loop = !_engine.Soundboard.Pads[idx].Loop;
                RefreshSoundboardPadLabels();
                ScheduleDebouncedSave();
            };

            var stopItem = new MenuItem { Header = "Stop This Pad" };
            stopItem.Click += (_, _) => _engine.Soundboard.StopPad(GetActualPadIndex(slotInBank));

            var resetItem = new MenuItem { Header = "Reset to Default" };
            resetItem.Click += (_, _) =>
            {
                _engine.Soundboard.ResetPadToDefault(GetActualPadIndex(slotInBank));
                RefreshSoundboardPadLabels();
                ScheduleDebouncedSave();
                _remoteDeck.BroadcastState();
            };

            menu.Items.Add(loadItem);
            menu.Items.Add(loopItem);
            menu.Items.Add(stopItem);
            menu.Items.Add(resetItem);
            btn.ContextMenu = menu;

            btn.Click += (_, _) =>
            {
                int idx = GetActualPadIndex(slotInBank);
                if (!_engine.Soundboard.Pads[idx].HasAudio)
                {
                    BrowseAndLoadPadFile(idx);
                    return;
                }
                _engine.Soundboard.TriggerPad(idx);
            };

            _padButtons[i] = btn;
            SoundboardGrid.Children.Add(btn);
        }
    }

    private int GetActualPadIndex(int slotInBank) => _activePadBank * SoundboardEngine.PadsPerBank + slotInBank;

    private void RefreshSoundboardPadLabels()
    {
        for (int i = 0; i < SoundboardEngine.PadsPerBank; i++)
        {
            int actualIdx = GetActualPadIndex(i);
            var pad = _engine.Soundboard.Pads[actualIdx];

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            string loopTag = pad.Loop ? " 🔁" : "";
            stack.Children.Add(new TextBlock
            {
                Text = pad.Name + loopTag,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });

            string subtitle = pad.IsBuiltIn
                ? $"Synth · Ctrl+Alt+{i + 1}"
                : pad.HasAudio ? $"Custom · Ctrl+Alt+{i + 1}" : "Drop Audio or Click to Load";

            stack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 0)
            });

            Grid.SetRow(stack, 0);
            root.Children.Add(stack);

            // Live playback progress bar strip at bottom of pad
            var scale = new ScaleTransform(0, 1);
            var fillBar = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = (Brush)FindResource("AccentCyanBrush"),
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = scale
            };
            var track = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(Color.FromRgb(22, 28, 40)),
                Margin = new Thickness(0, 6, 0, 0),
                Child = fillBar
            };
            Grid.SetRow(track, 1);
            root.Children.Add(track);

            _padProgressBars[i] = fillBar;
            _padProgressScales[i] = scale;
            _padButtons[i].Content = root;
        }
    }

    private void Bank1Button_Click(object sender, RoutedEventArgs e)
    {
        _activePadBank = 0;
        UpdateBankButtonVisuals();
        RefreshSoundboardPadLabels();
    }

    private void Bank2Button_Click(object sender, RoutedEventArgs e)
    {
        _activePadBank = 1;
        UpdateBankButtonVisuals();
        RefreshSoundboardPadLabels();
    }

    private void UpdateBankButtonVisuals()
    {
        var activeBg = new SolidColorBrush(Color.FromRgb(18, 52, 78));
        activeBg.Freeze();
        var activeBdr = (Brush)FindResource("AccentCyanBrush");
        var idleBg = (Brush)FindResource("InputBgBrush");
        var idleBdr = (Brush)FindResource("CardBorderBrush");

        Bank1Button.Background = _activePadBank == 0 ? activeBg : idleBg;
        Bank1Button.BorderBrush = _activePadBank == 0 ? activeBdr : idleBdr;
        Bank2Button.Background = _activePadBank == 1 ? activeBg : idleBg;
        Bank2Button.BorderBrush = _activePadBank == 1 ? activeBdr : idleBdr;
    }

    private async void BrowseAndLoadPadFile(int padIndex)
    {
        var dlg = new OpenFileDialog
        {
            Title = $"Select Audio File for Pad {padIndex + 1}",
            Filter = "Audio Files (*.wav;*.mp3;*.m4a;*.wma;*.aac;*.aiff)|*.wav;*.mp3;*.m4a;*.wma;*.aac;*.aiff|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) != true) return;
        await LoadCustomPadFileAsync(padIndex, dlg.FileName);
    }

    private async Task LoadCustomPadFileAsync(int padIndex, string filePath)
    {
        try
        {
            await _engine.Soundboard.LoadCustomFileAsync(padIndex, filePath);
            RefreshSoundboardPadLabels();
            HideError();
            ScheduleDebouncedSave();
            _remoteDeck.BroadcastState();
        }
        catch (Exception ex)
        {
            ShowError($"Could not load audio file: {ex.Message}");
        }
    }

    private void OnSystemAudioDevicesChanged()
    {
        bool wasRunning = _engine.IsRunning;
        string? runningInId = _engine.ActiveInputId;
        bool runningLoop = _engine.ActiveInputIsLoopback;
        string? runningOutId = _engine.ActiveOutputId;

        RefreshDevices(selectSaved: false);

        if (wasRunning && runningInId != null && runningOutId != null)
        {
            bool stillPresent =
                _inputs.Any(i => string.Equals(i.Id, runningInId, StringComparison.OrdinalIgnoreCase) && i.IsLoopback == runningLoop) &&
                _outputs.Any(o => string.Equals(o.Id, runningOutId, StringComparison.OrdinalIgnoreCase));

            if (!stillPresent)
            {
                _wasRunningBeforeDisconnect = true;
                _engine.Stop();
                ShowError("Active audio device disconnected. Plug it back in to auto-reconnect or choose another device.");
                UpdateConnectionVisuals();
            }
        }
        else if (!wasRunning && _wasRunningBeforeDisconnect && _settings.AutoReconnectOnHotPlug)
        {
            if (InputComboBox.SelectedItem is AudioInputSource && OutputComboBox.SelectedItem is AudioOutputTarget)
            {
                _wasRunningBeforeDisconnect = false;
                StartBridge(skipFeedbackPrompt: true);
            }
        }
    }

    private void RefreshDevices(bool selectSaved)
    {
        var previousInput = InputComboBox.SelectedItem as AudioInputSource;
        var previousOutput = OutputComboBox.SelectedItem as AudioOutputTarget;
        var previousMonitor = MonitorComboBox.SelectedItem as AudioOutputTarget;

        (_inputs, _outputs) = AudioDeviceManager.EnumerateDevices();

        _isSyncingUi = true;
        try
        {
            InputComboBox.ItemsSource = _inputs;
            OutputComboBox.ItemsSource = _outputs;
            MonitorComboBox.ItemsSource = _outputs;

            if (selectSaved)
            {
                var (smartIn, smartOut) = AudioDeviceManager.PickSmartDefaults(_inputs, _outputs);

                var savedIn = _inputs.FirstOrDefault(i =>
                    string.Equals(i.Id, _settings.InputDeviceId, StringComparison.OrdinalIgnoreCase) &&
                    i.IsLoopback == _settings.InputIsLoopback);
                InputComboBox.SelectedItem = savedIn ?? smartIn;

                var savedOut = _outputs.FirstOrDefault(o =>
                    string.Equals(o.Id, _settings.OutputDeviceId, StringComparison.OrdinalIgnoreCase));
                OutputComboBox.SelectedItem = savedOut ?? smartOut;

                var savedMon = _outputs.FirstOrDefault(o =>
                    string.Equals(o.Id, _settings.MonitorDeviceId, StringComparison.OrdinalIgnoreCase));
                MonitorComboBox.SelectedItem = savedMon ?? _outputs.FirstOrDefault(o => o.IsDefault) ?? _outputs.FirstOrDefault();

                foreach (ComboBoxItem item in LatencyComboBox.Items)
                {
                    if (int.TryParse(item.Tag?.ToString(), out int ms) && ms == _settings.LatencyMs)
                    {
                        LatencyComboBox.SelectedItem = item;
                        break;
                    }
                }
            }
            else
            {
                string? targetInId = _engine.IsRunning ? _engine.ActiveInputId : previousInput?.Id;
                bool targetInLoop = _engine.IsRunning ? _engine.ActiveInputIsLoopback : (previousInput?.IsLoopback ?? false);
                string? targetOutId = _engine.IsRunning ? _engine.ActiveOutputId : previousOutput?.Id;
                string? targetMonId = _engine.IsRunning ? _engine.ActiveMonitorId : previousMonitor?.Id;

                var (smartIn, smartOut) = AudioDeviceManager.PickSmartDefaults(_inputs, _outputs);

                InputComboBox.SelectedItem = targetInId != null
                    ? _inputs.FirstOrDefault(i => string.Equals(i.Id, targetInId, StringComparison.OrdinalIgnoreCase) && i.IsLoopback == targetInLoop) ?? smartIn
                    : smartIn;

                OutputComboBox.SelectedItem = targetOutId != null
                    ? _outputs.FirstOrDefault(o => string.Equals(o.Id, targetOutId, StringComparison.OrdinalIgnoreCase)) ?? smartOut
                    : smartOut;

                MonitorComboBox.SelectedItem = targetMonId != null
                    ? _outputs.FirstOrDefault(o => string.Equals(o.Id, targetMonId, StringComparison.OrdinalIgnoreCase)) ?? _outputs.FirstOrDefault(o => o.IsDefault)
                    : _outputs.FirstOrDefault(o => o.IsDefault) ?? _outputs.FirstOrDefault();
            }
        }
        finally
        {
            _isSyncingUi = false;
        }

        UpdateFeedbackWarning();
    }

    private void RefreshAudioProcesses()
    {
        var previous = AppProcessComboBox.SelectedItem as AudioSessionProcess;
        _audioProcesses = AudioDeviceManager.EnumerateAudioProcesses();

        _isSyncingUi = true;
        try
        {
            AppProcessComboBox.ItemsSource = _audioProcesses;
            if (previous != null)
            {
                AppProcessComboBox.SelectedItem = _audioProcesses.FirstOrDefault(p => p.ProcessId == previous.ProcessId)
                                                  ?? _audioProcesses.FirstOrDefault();
            }
            else
            {
                AppProcessComboBox.SelectedItem = _audioProcesses.FirstOrDefault();
            }

            if (!ProcessLoopbackCapture.IsSupportedOnCurrentOs)
            {
                AppLoopbackEnabledBox.IsEnabled = false;
                AppProcessComboBox.IsEnabled = false;
                AppLoopbackEnabledBox.ToolTip = "Requires Windows 10 Build 20348 or Windows 11.";
            }
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private int GetSelectedLatencyMs()
    {
        if (LatencyComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int ms))
        {
            return ms;
        }
        return 40;
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsRunning)
        {
            _wasRunningBeforeDisconnect = false;
            _engine.Stop();
            UpdateConnectionVisuals();
            return;
        }

        StartBridge();
    }

    private void StartBridge(bool skipFeedbackPrompt = false)
    {
        HideError();

        if (InputComboBox.SelectedItem is not AudioInputSource input ||
            OutputComboBox.SelectedItem is not AudioOutputTarget output)
        {
            ShowError("Please select both an Input Source and an Output Target.");
            return;
        }

        if (!skipFeedbackPrompt && input.IsLoopback &&
            string.Equals(input.Id, output.Id, StringComparison.OrdinalIgnoreCase))
        {
            var confirm = MessageBox.Show(
                "Loopback Input and Output Target point to the exact same audio device, which can cause loud feedback howling.\n\nDo you really want to connect this loop?",
                "VoxCable Feedback Safeguard",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes) return;
        }

        AudioOutputTarget? monitorTarget = MonitorEnabledBox.IsChecked == true
            ? MonitorComboBox.SelectedItem as AudioOutputTarget
            : null;

        uint? appPid = (AppLoopbackEnabledBox.IsChecked == true && AppProcessComboBox.SelectedItem is AudioSessionProcess proc)
            ? proc.ProcessId
            : null;

        try
        {
            _engine.Start(input, output, GetSelectedLatencyMs(), monitorTarget, appPid);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to start audio bridge: {AudioDeviceManager.TranslateAudioError(ex)}");
        }

        UpdateConnectionVisuals();
        UpdateMeterTimerState();
        ScheduleDebouncedSave();
    }

    private void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshDevices(selectSaved: false);
        RefreshAudioProcesses();
    }

    private void RefreshAppsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAudioProcesses();
    }

    private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingUi) return;
        UpdateFeedbackWarning();
        ScheduleDebouncedSave();
        if (_engine.IsRunning) StartBridge();
    }

    private void MonitorRoutingChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingUi || !IsLoaded) return;
        PushUiToParameters();
        ScheduleDebouncedSave();
        if (_engine.IsRunning) StartBridge(skipFeedbackPrompt: true);
    }

    private void AppLoopbackRoutingChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingUi || !IsLoaded) return;
        ScheduleDebouncedSave();
        if (_engine.IsRunning) StartBridge(skipFeedbackPrompt: true);
    }

    private void LatencyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingUi) return;
        ScheduleDebouncedSave();
        if (_engine.IsRunning) StartBridge(skipFeedbackPrompt: true);
    }

    private void UpdateFeedbackWarning()
    {
        if (InputComboBox.SelectedItem is AudioInputSource input &&
            OutputComboBox.SelectedItem is AudioOutputTarget output &&
            input.IsLoopback &&
            string.Equals(input.Id, output.Id, StringComparison.OrdinalIgnoreCase))
        {
            FeedbackWarningBanner.Visibility = Visibility.Visible;
        }
        else
        {
            FeedbackWarningBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateConnectionVisuals()
    {
        if (_engine.IsRunning)
        {
            ConnectButton.Content = "DISCONNECT";
            ConnectButton.Background = (Brush)FindResource("AccentCrimsonBrush");
            ConnectButton.BorderBrush = (Brush)FindResource("AccentCrimsonLightBrush");
            StatusDot.Fill = (Brush)FindResource("ActiveGreenBrush");
            StatusText.Foreground = (Brush)FindResource("ActiveGreenBrush");
            _trayManager.UpdateTooltip($"VoxCable Studio — LIVE ({_engine.SampleRate / 1000.0:0.#} kHz)");
        }
        else
        {
            ConnectButton.Content = "CONNECT";
            ConnectButton.Background = (Brush)FindResource("ActiveGreenDarkBrush");
            ConnectButton.BorderBrush = (Brush)FindResource("ActiveGreenBrush");
            StatusDot.Fill = (Brush)FindResource("StatusMutedBrush");
            StatusText.Text = "STANDBY";
            StatusText.Foreground = (Brush)FindResource("TextMutedBrush");
            _trayManager.UpdateTooltip("VoxCable Studio — Standby");
        }
    }

    private void MiniModeButton_Click(object sender, RoutedEventArgs e)
    {
        _isMiniMode = !_isMiniMode;
        if (_isMiniMode)
        {
            _normalWidth = Width;
            _normalHeight = Height;
            RoutingRow.Visibility = Visibility.Collapsed;
            StudioRoutingRow.Visibility = Visibility.Collapsed;
            MainTabControl.Visibility = Visibility.Collapsed;
            FooterTipText.Visibility = Visibility.Collapsed;
            Topmost = true;
            Height = 250;
            MiniModeButton.Content = "Full Mode";
        }
        else
        {
            RoutingRow.Visibility = Visibility.Visible;
            StudioRoutingRow.Visibility = Visibility.Visible;
            MainTabControl.Visibility = Visibility.Visible;
            FooterTipText.Visibility = Visibility.Visible;
            Topmost = false;
            Width = _normalWidth;
            Height = _normalHeight;
            MiniModeButton.Content = "Mini Mode";
        }
    }

    private void HoldBleepButton_Down(object sender, MouseButtonEventArgs e)
    {
        HoldBleepButton.CaptureMouse();
        SetHoldToBleep(true);
    }

    private void HoldBleepButton_Up(object sender, RoutedEventArgs e)
    {
        if (HoldBleepButton.IsMouseCaptured) HoldBleepButton.ReleaseMouseCapture();
        SetHoldToBleep(false);
    }

    private void SetHoldToBleep(bool active)
    {
        _engine.Parameters.HoldToBleep = active;
        _remoteDeck.BroadcastState();
    }

    private void EffectsRack_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? cur = e.OriginalSource as DependencyObject;
        while (cur != null)
        {
            if (cur is Slider slider &&
                double.TryParse(slider.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double defVal))
            {
                slider.Value = defVal;
                e.Handled = true;
                return;
            }
            cur = VisualTreeHelper.GetParent(cur);
        }
    }

    private void QuickPitchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn &&
            float.TryParse(btn.Tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float st))
        {
            _isSyncingUi = true;
            try
            {
                PitchSemitonesSlider.Value = st;
                PitchEnabledBox.IsChecked = MathF.Abs(st) >= 0.05f;
            }
            finally
            {
                _isSyncingUi = false;
            }
            PushUiToParameters();
            UpdateValueLabels();
            UpdateSignalFlowVisuals();
            ScheduleDebouncedSave();
        }
    }

    private void StopAllSfxButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.Soundboard.StopAll();
    }

    private void DspControlChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingUi || !IsLoaded) return;
        _activePresetName = null;
        PushUiToParameters();
        UpdateValueLabels();
        UpdateSignalFlowVisuals();
        UpdatePresetHighlights();
        ScheduleDebouncedSave();
        _remoteDeck.BroadcastState();
    }

    private InputChannelMode GetSelectedInputMode()
    {
        if (InputModeComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int val))
        {
            return (InputChannelMode)Math.Clamp(val, 0, 3);
        }
        return InputChannelMode.Stereo;
    }

    private int GetSelectedAutoTuneKey()
    {
        if (AutoTuneKeyCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int k))
            return Math.Clamp(k, 0, 11);
        return 0;
    }

    private AutoTuneScale GetSelectedAutoTuneScale()
    {
        if (AutoTuneScaleCombo.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int s))
            return (AutoTuneScale)Math.Clamp(s, 0, 4);
        return AutoTuneScale.Major;
    }

    private void PushUiToParameters()
    {
        var snap = new DspParamSnapshot
        {
            MasterBypass = BypassCheckBox.IsChecked == true,
            MuteOutput = MuteCheckBox.IsChecked == true,
            HoldToBleep = _engine.Parameters.HoldToBleep,
            InputChannelMode = GetSelectedInputMode(),

            InputGainDb = (float)InputGainSlider.Value,
            HighPassEnabled = HighPassEnabledBox.IsChecked == true,
            HighPassHz = (float)HighPassSlider.Value,
            DenoiseEnabled = DenoiseEnabledBox.IsChecked == true,
            DenoiseStrength = (float)DenoiseSlider.Value,
            NoiseGateEnabled = NoiseGateEnabledBox.IsChecked == true,
            NoiseGateThresholdDb = (float)NoiseGateSlider.Value,
            CompressorEnabled = CompressorEnabledBox.IsChecked == true,
            CompressorThresholdDb = (float)CompThreshSlider.Value,
            CompressorRatio = (float)CompRatioSlider.Value,
            CompressorMakeupDb = (float)CompMakeupSlider.Value,

            PitchEnabled = PitchEnabledBox.IsChecked == true,
            PitchSemitones = (float)PitchSemitonesSlider.Value,
            FormantSemitones = (float)FormantSlider.Value,
            PreserveFormants = PreserveFormantsBox.IsChecked == true,
            PitchMix = (float)PitchMixSlider.Value,

            AutoTuneEnabled = AutoTuneEnabledBox.IsChecked == true,
            AutoTuneKey = GetSelectedAutoTuneKey(),
            AutoTuneScale = GetSelectedAutoTuneScale(),
            AutoTuneStrength = (float)AutoTuneStrengthSlider.Value,

            EqEnabled = EqEnabledBox.IsChecked == true,
            BassDb = (float)BassSlider.Value,
            MidDb = (float)MidSlider.Value,
            TrebleDb = (float)TrebleSlider.Value,

            ChorusEnabled = ChorusEnabledBox.IsChecked == true,
            ChorusRateHz = (float)ChorusRateSlider.Value,
            ChorusDepthMs = (float)ChorusDepthSlider.Value,
            ChorusMix = (float)ChorusMixSlider.Value,

            RobotEnabled = RobotEnabledBox.IsChecked == true,
            RobotFreqHz = (float)RobotFreqSlider.Value,
            RobotMix = (float)RobotMixSlider.Value,

            RadioEnabled = RadioEnabledBox.IsChecked == true,
            RadioDrive = (float)RadioDriveSlider.Value,
            RadioStatic = (float)RadioStaticSlider.Value,
            RadioMix = (float)RadioMixSlider.Value,
            RadioSquelch = RadioSquelchBox.IsChecked == true,

            BitcrushEnabled = BitcrushEnabledBox.IsChecked == true,
            BitcrushBits = (float)BitcrushBitsSlider.Value,
            BitcrushDownsample = (float)BitcrushDownsampleSlider.Value,
            BitcrushMix = (float)BitcrushMixSlider.Value,

            EchoEnabled = EchoEnabledBox.IsChecked == true,
            EchoDelayMs = (float)EchoDelaySlider.Value,
            EchoFeedback = (float)EchoFeedbackSlider.Value,
            EchoMix = (float)EchoMixSlider.Value,

            ReverbEnabled = ReverbEnabledBox.IsChecked == true,
            ReverbRoomSize = (float)ReverbRoomSlider.Value,
            ReverbDamping = (float)ReverbDampingSlider.Value,
            ReverbMix = (float)ReverbMixSlider.Value,

            OutputGainDb = (float)OutputGainSlider.Value,
            SoftLimiterEnabled = LimiterEnabledBox.IsChecked == true,

            SfxVolume = (float)SfxVolumeSlider.Value,
            RouteSfxThroughFx = RouteSfxThroughFxBox.IsChecked == true,
            AppLoopbackVolume = (float)AppLoopbackVolumeSlider.Value,
            RouteAppThroughFx = RouteAppThroughFxBox.IsChecked == true,

            MonitorEnabled = MonitorEnabledBox.IsChecked == true,
            MonitorVoice = MonitorVoiceBox.IsChecked == true,
            MonitorSfx = MonitorSfxBox.IsChecked == true,
            MonitorVolume = (float)MonitorVolumeSlider.Value
        };

        _engine.Parameters.ReplaceSnapshot(snap);
    }

    private void SyncUiFromParameters()
    {
        _isSyncingUi = true;
        try
        {
            var p = _engine.Parameters.Current;
            BypassCheckBox.IsChecked = p.MasterBypass;
            MuteCheckBox.IsChecked = p.MuteOutput;
            SelectComboByTag(InputModeComboBox, (int)p.InputChannelMode);

            InputGainSlider.Value = p.InputGainDb;
            HighPassEnabledBox.IsChecked = p.HighPassEnabled;
            HighPassSlider.Value = p.HighPassHz;
            DenoiseEnabledBox.IsChecked = p.DenoiseEnabled;
            DenoiseSlider.Value = p.DenoiseStrength;
            NoiseGateEnabledBox.IsChecked = p.NoiseGateEnabled;
            NoiseGateSlider.Value = p.NoiseGateThresholdDb;
            CompressorEnabledBox.IsChecked = p.CompressorEnabled;
            CompThreshSlider.Value = p.CompressorThresholdDb;
            CompRatioSlider.Value = p.CompressorRatio;
            CompMakeupSlider.Value = p.CompressorMakeupDb;

            PitchEnabledBox.IsChecked = p.PitchEnabled;
            PitchSemitonesSlider.Value = p.PitchSemitones;
            FormantSlider.Value = p.FormantSemitones;
            PreserveFormantsBox.IsChecked = p.PreserveFormants;
            PitchMixSlider.Value = p.PitchMix;

            AutoTuneEnabledBox.IsChecked = p.AutoTuneEnabled;
            SelectComboByTag(AutoTuneKeyCombo, p.AutoTuneKey);
            SelectComboByTag(AutoTuneScaleCombo, (int)p.AutoTuneScale);
            AutoTuneStrengthSlider.Value = p.AutoTuneStrength;

            EqEnabledBox.IsChecked = p.EqEnabled;
            BassSlider.Value = p.BassDb;
            MidSlider.Value = p.MidDb;
            TrebleSlider.Value = p.TrebleDb;

            ChorusEnabledBox.IsChecked = p.ChorusEnabled;
            ChorusRateSlider.Value = p.ChorusRateHz;
            ChorusDepthSlider.Value = p.ChorusDepthMs;
            ChorusMixSlider.Value = p.ChorusMix;

            RobotEnabledBox.IsChecked = p.RobotEnabled;
            RobotFreqSlider.Value = p.RobotFreqHz;
            RobotMixSlider.Value = p.RobotMix;

            RadioEnabledBox.IsChecked = p.RadioEnabled;
            RadioDriveSlider.Value = p.RadioDrive;
            RadioStaticSlider.Value = p.RadioStatic;
            RadioMixSlider.Value = p.RadioMix;
            RadioSquelchBox.IsChecked = p.RadioSquelch;

            BitcrushEnabledBox.IsChecked = p.BitcrushEnabled;
            BitcrushBitsSlider.Value = p.BitcrushBits;
            BitcrushDownsampleSlider.Value = p.BitcrushDownsample;
            BitcrushMixSlider.Value = p.BitcrushMix;

            EchoEnabledBox.IsChecked = p.EchoEnabled;
            EchoDelaySlider.Value = p.EchoDelayMs;
            EchoFeedbackSlider.Value = p.EchoFeedback;
            EchoMixSlider.Value = p.EchoMix;

            ReverbEnabledBox.IsChecked = p.ReverbEnabled;
            ReverbRoomSlider.Value = p.ReverbRoomSize;
            ReverbDampingSlider.Value = p.ReverbDamping;
            ReverbMixSlider.Value = p.ReverbMix;

            OutputGainSlider.Value = p.OutputGainDb;
            LimiterEnabledBox.IsChecked = p.SoftLimiterEnabled;

            SfxVolumeSlider.Value = p.SfxVolume;
            RouteSfxThroughFxBox.IsChecked = p.RouteSfxThroughFx;
            AppLoopbackVolumeSlider.Value = p.AppLoopbackVolume;
            RouteAppThroughFxBox.IsChecked = p.RouteAppThroughFx;

            MonitorEnabledBox.IsChecked = p.MonitorEnabled;
            MonitorVoiceBox.IsChecked = p.MonitorVoice;
            MonitorSfxBox.IsChecked = p.MonitorSfx;
            MonitorVolumeSlider.Value = p.MonitorVolume;

            UpdateValueLabels();
            UpdateSignalFlowVisuals();
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private static void SelectComboByTag(ComboBox combo, int tagValue)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (int.TryParse(item.Tag?.ToString(), out int v) && v == tagValue)
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateValueLabels()
    {
        var p = _engine.Parameters.Current;
        HighPassValue.Text = $"{p.HighPassHz:0} Hz";
        DenoiseValue.Text = $"{p.DenoiseStrength * 100f:0}%";
        NoiseGateValue.Text = $"{p.NoiseGateThresholdDb:0} dB";
        CompSummaryValue.Text = $"{p.CompressorThresholdDb:0} dB · {p.CompressorRatio:0.0}:1 · {p.CompressorMakeupDb:+0.0;-0.0;0} dB";

        PitchSemitonesValue.Text = $"{p.PitchSemitones:+0.0;-0.0;0.0} st";
        FormantPitchMixValue.Text = $"{p.FormantSemitones:+0.0;-0.0;0.0} st / {p.PitchMix * 100f:0}%";
        AutoTuneStrengthValue.Text = $"{p.AutoTuneStrength * 100f:0}%";

        InOutGainValue.Text = $"{p.InputGainDb:+0.0;-0.0;0.0} dB / {p.OutputGainDb:+0.0;-0.0;0.0} dB";
        EqSummaryValue.Text = $"{p.BassDb:+0.0;-0.0;0} / {p.MidDb:+0.0;-0.0;0} / {p.TrebleDb:+0.0;-0.0;0} dB";

        ChorusSummaryValue.Text = $"{p.ChorusRateHz:0.0} Hz · {p.ChorusDepthMs:0.0} ms · {p.ChorusMix * 100f:0}%";
        RobotSummaryValue.Text = $"{p.RobotFreqHz:0} Hz / {p.RobotMix * 100f:0}%";
        RadioSummaryValue.Text = $"{p.RadioDrive:0.0}x · {p.RadioStatic * 100f:0}% · {p.RadioMix * 100f:0}%";
        BitcrushSummaryValue.Text = $"{Math.Round(p.BitcrushBits):0} bits · {Math.Round(p.BitcrushDownsample):0}x · {p.BitcrushMix * 100f:0}%";
        EchoSummaryValue.Text = $"{p.EchoDelayMs:0} ms · {p.EchoFeedback * 100f:0}% · {p.EchoMix * 100f:0}%";
        ReverbSummaryValue.Text = $"{p.ReverbRoomSize * 100f:0}% · {p.ReverbDamping * 100f:0}% · {p.ReverbMix * 100f:0}%";

        SfxVolumeValue.Text = $"{p.SfxVolume * 100f:0}%";
        MonitorVolumeValue.Text = $"{p.MonitorVolume * 100f:0}%";
        AppLoopbackVolumeValue.Text = $"{p.AppLoopbackVolume * 100f:0}%";
    }

    private void OnMeterTimerTick(object? sender, EventArgs e)
    {
        if (!_engine.IsRunning) return;

        string monTag = _engine.IsMonitorActive ? " · MON" : "";
        string appTag = _engine.IsAppLoopbackActive ? " · APP" : "";
        StatusText.Text = $"LIVE · {_engine.SampleRate / 1000.0:0.#} kHz · {_engine.BufferedLatencyMs:0} ms{monTag}{appTag}";

        // GPU-accelerated ScaleTransform meters (zero layout pass!)
        InputMeterScaleL.ScaleX = Math.Clamp(Math.Sqrt(_engine.InputPeakL), 0.0, 1.0);
        InputMeterScaleR.ScaleX = Math.Clamp(Math.Sqrt(_engine.InputPeakR), 0.0, 1.0);
        OutputMeterScaleL.ScaleX = Math.Clamp(Math.Sqrt(_engine.OutputPeakL), 0.0, 1.0);
        OutputMeterScaleR.ScaleX = Math.Clamp(Math.Sqrt(_engine.OutputPeakR), 0.0, 1.0);

        InputPeakText.Text = PeakToDbText(MathF.Max(_engine.InputPeakL, _engine.InputPeakR));
        OutputPeakText.Text = PeakToDbText(MathF.Max(_engine.OutputPeakL, _engine.OutputPeakR));

        InputClipBadge.Visibility = _engine.InputClipping ? Visibility.Visible : Visibility.Collapsed;
        OutputClipBadge.Visibility = _engine.OutputClipping ? Visibility.Visible : Visibility.Collapsed;

        // Broadcast strip live LEDs
        GateStatusDot.Fill = _engine.NoiseGateOpen
            ? (Brush)FindResource("ActiveGreenBrush")
            : (Brush)FindResource("AccentCrimsonBrush");
        CompGrText.Text = $"GR: -{_engine.CompressorGainReductionDb:0.0} dB";

        // Update mini waveform scope
        int samples = _engine.CopyWaveformSnapshot(_waveScratch);
        if (samples > 1)
        {
            var pts = new PointCollection(samples);
            double w = 148.0;
            double midY = 12.0;
            double dx = w / (samples - 1);
            for (int i = 0; i < samples; i++)
            {
                double y = midY - Math.Clamp(_waveScratch[i], -1f, 1f) * 11.0;
                pts.Add(new Point(i * dx, y));
            }
            pts.Freeze();
            WaveformPolyline.Points = pts;
        }

        // Soundboard live per-pad playback progress fill
        for (int i = 0; i < SoundboardEngine.PadsPerBank; i++)
        {
            _padProgressScales[i].ScaleX = _engine.Soundboard.GetPadProgress(GetActualPadIndex(i));
        }

        if (_remoteDeck.IsRunning)
        {
            RemoteDeckClientsText.Text = $"{_remoteDeck.ClientCount} connected";
        }
    }

    private static string PeakToDbText(float peak)
    {
        if (peak <= 0.0001f) return "-inf dB";
        float db = 20f * MathF.Log10(peak);
        return $"{db:0.0} dB";
    }

    #region Custom Presets

    private void RefreshCustomPresetsList()
    {
        CustomPresetsListBox.ItemsSource = null;
        CustomPresetsListBox.ItemsSource = _settings.CustomPresets.Select(p => p.Name).ToList();
    }

    private void SaveCustomPresetButton_Click(object sender, RoutedEventArgs e)
    {
        string name = NewPresetNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Enter a name for your custom preset first.");
            return;
        }

        _settings.CustomPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _settings.CustomPresets.Add(new CustomPresetData
        {
            Name = name,
            Snapshot = _engine.Parameters.Current with { HoldToBleep = false }
        });

        _activePresetName = name;
        NewPresetNameBox.Clear();
        BuildPresetButtons();
        RefreshCustomPresetsList();
        ScheduleDebouncedSave();
        _remoteDeck.BroadcastState();
        HideError();
    }

    private void ApplyCustomPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomPresetsListBox.SelectedItem is string name)
        {
            ApplyPresetByName(name);
        }
    }

    private void DeleteCustomPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (CustomPresetsListBox.SelectedItem is string name)
        {
            _settings.CustomPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            BuildPresetButtons();
            RefreshCustomPresetsList();
            ScheduleDebouncedSave();
            _remoteDeck.BroadcastState();
        }
    }

    private void ExportPresetsButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export VoxCable Custom Presets",
            Filter = "JSON Files (*.json)|*.json",
            FileName = "voxcable-presets.json"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            string json = JsonSerializer.Serialize(_settings.CustomPresets, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dlg.FileName, json);
            HideError();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to export presets: {ex.Message}");
        }
    }

    private void ImportPresetsButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import VoxCable Custom Presets",
            Filter = "JSON Files (*.json)|*.json"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            string json = File.ReadAllText(dlg.FileName);
            var imported = JsonSerializer.Deserialize<List<CustomPresetData>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (imported != null)
            {
                foreach (var item in imported)
                {
                    if (string.IsNullOrWhiteSpace(item.Name) || item.Snapshot == null) continue;
                    _settings.CustomPresets.RemoveAll(p => string.Equals(p.Name, item.Name, StringComparison.OrdinalIgnoreCase));
                    _settings.CustomPresets.Add(item);
                }
                BuildPresetButtons();
                RefreshCustomPresetsList();
                ScheduleDebouncedSave();
                _remoteDeck.BroadcastState();
            }
            HideError();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to import presets: {ex.Message}");
        }
    }

    #endregion

    #region Global Hotkeys & Wireless Remote Deck

    private void SyncPlatformUiFromSettings()
    {
        _isSyncingUi = true;
        try
        {
            GlobalHotkeysBox.IsChecked = _settings.GlobalHotkeysEnabled;
            MinimizeToTrayBox.IsChecked = _settings.MinimizeToTray;
            AutoReconnectBox.IsChecked = _settings.AutoReconnectOnHotPlug;
            RemoteDeckEnabledBox.IsChecked = _settings.RemoteDeckEnabled;
            RemoteDeckAllowLanBox.IsChecked = _settings.RemoteDeckAllowLan;
            RemoteDeckPortBox.Text = _settings.RemoteDeckPort.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isSyncingUi = false;
        }
    }

    private void PlatformSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingUi || !IsLoaded) return;
        _settings.GlobalHotkeysEnabled = GlobalHotkeysBox.IsChecked == true;
        _settings.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        _settings.AutoReconnectOnHotPlug = AutoReconnectBox.IsChecked == true;

        _hotkeyManager.SetEnabled(_settings.GlobalHotkeysEnabled);
        UpdateHotkeyStatusText();
        ScheduleDebouncedSave();
    }

    private void UpdateHotkeyStatusText()
    {
        HotkeyStatusText.Text = _settings.GlobalHotkeysEnabled
            ? $"{_hotkeyManager.RegisteredCount} registered"
            : "Disabled";
    }

    private void OnGlobalHotkeyPressed(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleMute:
                MuteCheckBox.IsChecked = !(MuteCheckBox.IsChecked == true);
                break;
            case HotkeyAction.ToggleBypass:
                BypassCheckBox.IsChecked = !(BypassCheckBox.IsChecked == true);
                break;
            case HotkeyAction.HoldCensorBleep:
                SetHoldToBleep(true);
                _bleepPollTimer.Start();
                break;
            case HotkeyAction.StopAllSfx:
                _engine.Soundboard.StopAll();
                break;
            default:
                int padOffset = (int)action - (int)HotkeyAction.Pad1;
                if (padOffset >= 0 && padOffset < SoundboardEngine.PadsPerBank)
                {
                    _engine.Soundboard.TriggerPad(GetActualPadIndex(padOffset));
                }
                break;
        }
    }

    private void RemoteDeckSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingUi || !IsLoaded) return;
        bool enable = RemoteDeckEnabledBox.IsChecked == true;
        _settings.RemoteDeckEnabled = enable;
        _settings.RemoteDeckAllowLan = RemoteDeckAllowLanBox.IsChecked == true;
        if (int.TryParse(RemoteDeckPortBox.Text, out int port))
        {
            _settings.RemoteDeckPort = Math.Clamp(port, 1024, 65535);
        }

        StartOrStopRemoteDeck(enable);
        ScheduleDebouncedSave();
    }

    private void StartOrStopRemoteDeck(bool enable)
    {
        if (!enable)
        {
            _remoteDeck.Stop();
            RemoteDeckUrlText.Text = "Server stopped — toggle Enable Server to launch local Touch Deck";
            RemoteDeckClientsText.Text = "Offline";
            return;
        }

        try
        {
            string url = _remoteDeck.Start(_settings.RemoteDeckPort, _settings.RemoteDeckAllowLan);
            RemoteDeckUrlText.Text = url;
            RemoteDeckClientsText.Text = "0 connected";
            HideError();
        }
        catch (Exception ex)
        {
            ShowError($"Could not start Wireless Touch Deck: {ex.Message}");
            _isSyncingUi = true;
            RemoteDeckEnabledBox.IsChecked = false;
            _settings.RemoteDeckEnabled = false;
            _isSyncingUi = false;
        }
    }

    private void CopyDeckUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteDeck.IsRunning && !string.IsNullOrWhiteSpace(_remoteDeck.ActiveUrl))
        {
            Clipboard.SetText(_remoteDeck.ActiveUrl);
        }
    }

    private void OpenDeckBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        if (_remoteDeck.IsRunning && !string.IsNullOrWhiteSpace(_remoteDeck.ActiveUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_remoteDeck.ActiveUrl) { UseShellExecute = true });
            }
            catch { }
        }
    }

    private RemoteDeckState BuildRemoteDeckState()
    {
        var p = _engine.Parameters.Current;
        var presets = DspPresets.All.Select(x => x.Name)
            .Concat(_settings.CustomPresets.Select(x => x.Name))
            .ToList();
        var pads = _engine.Soundboard.Pads.Select(x => x.Name).ToList();

        return new RemoteDeckState(
            _engine.IsRunning,
            p.MuteOutput,
            p.MasterBypass,
            p.HoldToBleep,
            _activePresetName,
            presets,
            pads);
    }

    private void HandleRemoteDeckCommand(RemoteDeckCommand cmd)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (cmd.Action.ToLowerInvariant())
            {
                case "toggleconnect":
                    ConnectButton_Click(this, new RoutedEventArgs());
                    break;
                case "togglemute":
                    MuteCheckBox.IsChecked = !(MuteCheckBox.IsChecked == true);
                    break;
                case "togglebypass":
                    BypassCheckBox.IsChecked = !(BypassCheckBox.IsChecked == true);
                    break;
                case "setbleep":
                    SetHoldToBleep(cmd.Flag);
                    break;
                case "applypreset":
                    if (!string.IsNullOrWhiteSpace(cmd.Value)) ApplyPresetByName(cmd.Value);
                    break;
                case "triggerpad":
                    _engine.Soundboard.TriggerPad(cmd.Index);
                    break;
                case "stopall":
                    _engine.Soundboard.StopAll();
                    break;
            }
        });
    }

    #endregion

    private void ScheduleDebouncedSave()
    {
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private void PersistSettingsNow()
    {
        var input = InputComboBox.SelectedItem as AudioInputSource;
        var output = OutputComboBox.SelectedItem as AudioOutputTarget;
        var monitor = MonitorComboBox.SelectedItem as AudioOutputTarget;
        _settings.CaptureFrom(
            _engine.Parameters,
            _engine.Soundboard,
            input?.Id,
            input?.IsLoopback ?? false,
            output?.Id,
            monitor?.Id,
            GetSelectedLatencyMs());
        _settings.Save();
    }

    private void ShowError(string message)
    {
        ErrorBannerText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _meterTimer.Stop();
        _saveDebounceTimer.Stop();
        _bleepPollTimer.Stop();

        PersistSettingsNow();

        _remoteDeck.Dispose();
        _hotkeyManager.Dispose();
        _trayManager.Dispose();
        _deviceWatcher.Dispose();
        _engine.Dispose();
    }
}
