using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SocScreenShare.App.Services;
using SocScreenShare.Capture;
using SocScreenShare.Engine;
using SocScreenShare.Input;
using SocScreenShare.Localization;
using SocScreenShare.Recording;
using SocScreenShare.Settings;
using SocScreenShare.Video;

namespace SocScreenShare.App;

/// <summary>Una opción de un desplegable: el valor y su texto ya traducido.</summary>
public sealed record Choice<T>(T Value, string Text)
{
    // El desplegable con plantilla propia enseña el elemento tal cual: su texto.
    public override string ToString() => Text;
}

/// <summary>Una ventana de la lista, con su texto.</summary>
public sealed record WindowItem(nint Handle, string Title, string Detail, RectI Bounds);

/// <summary>
/// La ventana principal: qué grabar (pantalla, todas o ventana), sonido, carpeta y calidad, atajos y
/// grabación automática, y abajo los mandos. Todo se guarda al cambiarlo. La grabación la lleva
/// <see cref="RecordingController"/>: esta ventana solo la enseña y la manda.
/// </summary>
public partial class MainWindow : Window
{
    private readonly RecorderSettings _s = App.Settings;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);
        LogoImage.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png"));
        if (Sandbox.IsOn)
            Title += " [SOC_SANDBOX]";
        if (_s.WindowWidth > 400 && _s.WindowHeight > 300)
        {
            Width = _s.WindowWidth;
            Height = _s.WindowHeight;
        }

        SystemAudioCombo.ItemsSource = new[]
        {
            new Choice<SystemAudioMode>(SystemAudioMode.None, Loc.Get("AudioNone")),
            new Choice<SystemAudioMode>(SystemAudioMode.All, Loc.Get("AudioAll")),
            new Choice<SystemAudioMode>(SystemAudioMode.App, Loc.Get("AudioApp")),
        };
        QualityCombo.ItemsSource = new[]
        {
            new Choice<QualityPreset>(QualityPreset.Share, Loc.Get("QualityShare")),
            new Choice<QualityPreset>(QualityPreset.High, Loc.Get("QualityHigh")),
            new Choice<QualityPreset>(QualityPreset.Custom, Loc.Get("QualityCustom")),
        };
        LoadOutputDevices();
        LoadMicDevices();
        LoadApps();

        // Lo guardado, a la ventana.
        MonitorTile.IsChecked = _s.Target == TargetKind.Monitor;
        AllTile.IsChecked = _s.Target == TargetKind.AllMonitors;
        WindowTile.IsChecked = _s.Target == TargetKind.Window;
        SystemAudioCombo.SelectedItem = ((Choice<SystemAudioMode>[])SystemAudioCombo.ItemsSource).First(c => c.Value == _s.SystemAudio);
        QualityCombo.SelectedItem = ((Choice<QualityPreset>[])QualityCombo.ItemsSource).First(c => c.Value == _s.Quality);
        SystemVolumeSlider.Value = _s.SystemVolume;
        WindowVolumeSlider.Value = _s.WindowVolume;
        MicVolumeSlider.Value = _s.MicVolume;
        WindowAudioCheck.IsChecked = _s.WindowAudio;
        MicCheck.IsChecked = _s.MicEnabled;
        CursorCheck.IsChecked = _s.ShowCursor;
        HideCheck.IsChecked = _s.HideWhileRecording;
        FolderBox.Text = _s.OutputFolder;
        AutoRecordCheck.IsChecked = _s.AutoRecordEnabled;
        StartupCheck.IsEnabled = WindowsStartup.IsAvailable;
        StartupCheck.IsChecked = WindowsStartup.IsAvailable && WindowsStartup.IsEnabled();
        StartupHint.Text = Loc.Get(WindowsStartup.IsAvailable ? "StartupHint" : "StartupUnavailable");
        WindowAudioUnsupportedText.Visibility = AudioCatalog.AppCaptureSupported ? Visibility.Collapsed : Visibility.Visible;
        _loading = false;

        ShowTargetPanels();
        ShowAudioPanels();
        ShowHotkeys();
        ShowAutoSummary();
        UpdateControls();
        if (App.Controller.LastResult is { } last)
            ShowResult(last);

        App.Controller.StateChanged += UpdateControls;
        App.Controller.Tick += OnTick;
        App.Controller.Notice += ShowStatus;
        App.Controller.Finished += ShowResult;
        Closed += (_, _) =>
        {
            App.Controller.StateChanged -= UpdateControls;
            App.Controller.Tick -= OnTick;
            App.Controller.Notice -= ShowStatus;
            App.Controller.Finished -= ShowResult;
        };
        Loaded += (_, _) => BuildMonitorMap();
        MonitorMap.SizeChanged += (_, _) => BuildMonitorMap();
        Activated += (_, _) => RefreshMonitorsIfChanged();
        Closing += OnClosing;
    }

    // ------------------------------------------------------------------ guardar

    private void Save()
    {
        if (!_loading)
            _s.Save();
    }

    public void ShowStatus(string text) => StatusText.Text = text;

    // ------------------------------------------------------------------ qué grabar

    private void OnTargetChecked(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _s.Target = WindowTile.IsChecked == true ? TargetKind.Window : AllTile.IsChecked == true ? TargetKind.AllMonitors : TargetKind.Monitor;
        Save();
        ShowTargetPanels();
        ShowAudioPanels();
    }

    private void ShowTargetPanels()
    {
        MonitorPanel.Visibility = _s.Target == TargetKind.Monitor ? Visibility.Visible : Visibility.Collapsed;
        AllLabel.Visibility = _s.Target == TargetKind.AllMonitors ? Visibility.Visible : Visibility.Collapsed;
        WindowPanel.Visibility = _s.Target == TargetKind.Window ? Visibility.Visible : Visibility.Collapsed;
        if (_s.Target == TargetKind.Window)
            RefreshWindows();
        UpdateEstimate();
    }

    private void RefreshMonitorsIfChanged()
    {
        var now = Devices.Monitors();
        if (!now.Select(m => (m.Id, m.Bounds)).SequenceEqual(_monitors.Select(m => (m.Id, m.Bounds))))
            BuildMonitorMap();
    }

    /// <summary>Las pantallas dibujadas en su sitio y a escala; se pulsa la que se quiere grabar.</summary>
    private void BuildMonitorMap()
    {
        _monitors = Devices.Monitors();
        MonitorMap.Children.Clear();
        var width = MonitorMap.ActualWidth > 10 ? MonitorMap.ActualWidth : 600;
        var chosen = MonitorLayout.Resolve(_monitors, _s.MonitorId);
        foreach (var (m, x, y, w, h) in MonitorLayout.Map(_monitors, width, MonitorMap.Height))
        {
            var b = new RadioButton
            {
                Style = (Style)FindResource("MonitorButton"),
                GroupName = "Monitor",
                Content = m.Number.ToString(CultureInfo.CurrentCulture),
                Width = w,
                Height = h,
                IsChecked = m == chosen,
                ToolTip = MonitorText(m),
            };
            AutomationProperties.SetAutomationId(b, "Monitor" + m.Number);
            AutomationProperties.SetName(b, MonitorText(m));
            b.Checked += (_, _) =>
            {
                _s.MonitorId = m.Id;
                Save();
                MonitorLabel.Text = MonitorText(m);
                UpdateEstimate();
            };
            Canvas.SetLeft(b, x);
            Canvas.SetTop(b, y);
            MonitorMap.Children.Add(b);
        }
        MonitorLabel.Text = chosen is null ? Loc.Get("ErrNoMonitor") : MonitorText(chosen);
        var all = MonitorLayout.VirtualBounds(_monitors);
        AllLabel.Text = Loc.Format("AllLabel", _monitors.Count, $"{all.Width}×{all.Height}");
        UpdateEstimate();
    }

    private static string MonitorText(MonitorInfo m) =>
        Loc.Format(m.Primary ? "MonitorPrimary" : "MonitorItem", m.Number, m.SizeText);

    private void OnRefreshWindows(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows()
    {
        var keep = App.SelectedWindow;
        var items = Devices.RecordableWindows()
            .Select(w => new WindowItem(w.Handle, w.Title, w.Minimized ? Loc.Format("WindowMinimized", w.ProcessName) : w.ProcessName, w.Bounds))
            .ToList();
        WindowList.ItemsSource = items;
        WindowList.SelectedItem = items.FirstOrDefault(i => i.Handle == keep);
    }

    private void OnWindowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (WindowList.SelectedItem is WindowItem w)
        {
            App.SelectedWindow = w.Handle;
            App.SelectedWindowTitle = w.Title;
        }
        UpdateEstimate();
    }

    // ------------------------------------------------------------------ sonido

    private void LoadOutputDevices()
    {
        var items = new List<AudioDevice> { new(string.Empty, Loc.Get("DeviceDefault"), true) };
        items.AddRange(AudioCatalog.Outputs());
        OutputDeviceCombo.ItemsSource = items;
        OutputDeviceCombo.SelectedItem = items.FirstOrDefault(d => d.Id == (_s.OutputDeviceId ?? string.Empty)) ?? items[0];
    }

    private void LoadMicDevices()
    {
        var items = new List<AudioDevice> { new(string.Empty, Loc.Get("DeviceDefault"), true) };
        items.AddRange(AudioCatalog.Inputs());
        MicDeviceCombo.ItemsSource = items;
        MicDeviceCombo.SelectedItem = items.FirstOrDefault(d => d.Id == (_s.MicDeviceId ?? string.Empty)) ?? items[0];
    }

    private void LoadApps()
    {
        var items = AudioCatalog.Apps().Select(a => new Choice<string>(a.ProcessName, $"{a.ProcessName} — {a.Title}")).ToList();
        // La guardada sigue en la lista aunque ahora no esté abierta.
        if (_s.AppProcessName is { } saved && items.All(i => !string.Equals(i.Value, saved, StringComparison.OrdinalIgnoreCase)))
            items.Insert(0, new Choice<string>(saved, saved));
        var wasLoading = _loading;
        _loading = true;
        AppCombo.ItemsSource = items;
        AppCombo.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Value, _s.AppProcessName, StringComparison.OrdinalIgnoreCase));
        _loading = wasLoading;
    }

    private void OnOutputDropDown(object? sender, EventArgs e)
    {
        var was = _loading;
        _loading = true;
        LoadOutputDevices();
        _loading = was;
    }

    private void OnMicDropDown(object? sender, EventArgs e)
    {
        var was = _loading;
        _loading = true;
        LoadMicDevices();
        _loading = was;
    }

    private void OnAppDropDown(object? sender, EventArgs e) => LoadApps();

    private void OnSystemAudioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SystemAudioCombo.SelectedItem is Choice<SystemAudioMode> c && !_loading)
        {
            _s.SystemAudio = c.Value;
            Save();
        }
        ShowAudioPanels();
    }

    private void ShowAudioPanels()
    {
        var window = _s.Target == TargetKind.Window;
        ScreenAudioPanel.Visibility = window ? Visibility.Collapsed : Visibility.Visible;
        WindowAudioPanel.Visibility = window ? Visibility.Visible : Visibility.Collapsed;
        OutputDeviceCombo.Visibility = _s.SystemAudio == SystemAudioMode.App ? Visibility.Collapsed : Visibility.Visible;
        OutputDeviceCombo.IsEnabled = _s.SystemAudio == SystemAudioMode.All;
        AppCombo.Visibility = _s.SystemAudio == SystemAudioMode.App ? Visibility.Visible : Visibility.Collapsed;
        AppUnsupportedText.Visibility = _s.SystemAudio == SystemAudioMode.App && !AudioCatalog.AppCaptureSupported ? Visibility.Visible : Visibility.Collapsed;
        SystemVolumeRow.IsEnabled = _s.SystemAudio != SystemAudioMode.None;
        MicDeviceCombo.IsEnabled = _s.MicEnabled;
        MicVolumeSlider.IsEnabled = _s.MicEnabled;
        WindowVolumeSlider.IsEnabled = _s.WindowAudio;
        SystemVolumeText.Text = Loc.Format("Percent", _s.SystemVolume);
        WindowVolumeText.Text = Loc.Format("Percent", _s.WindowVolume);
        MicVolumeText.Text = Loc.Format("Percent", _s.MicVolume);
        UpdateEstimate();
    }

    private void OnOutputDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OutputDeviceCombo.SelectedItem is not AudioDevice d)
            return;
        _s.OutputDeviceId = d.Id.Length == 0 ? null : d.Id;
        Save();
    }

    private void OnAppChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppCombo.SelectedItem is not Choice<string> c)
            return;
        _s.AppProcessName = c.Value;
        Save();
    }

    private void OnMicDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MicDeviceCombo.SelectedItem is not AudioDevice d)
            return;
        _s.MicDeviceId = d.Id.Length == 0 ? null : d.Id;
        Save();
    }

    private void OnWindowAudio(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _s.WindowAudio = WindowAudioCheck.IsChecked == true;
        Save();
        ShowAudioPanels();
    }

    private void OnMic(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _s.MicEnabled = MicCheck.IsChecked == true;
        Save();
        ShowAudioPanels();
    }

    private void OnSystemVolume(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        _s.SystemVolume = (int)Math.Round(e.NewValue);
        App.Controller.SetSystemGain(_s.SystemVolume);
        SystemVolumeText.Text = Loc.Format("Percent", _s.SystemVolume);
        Save();
    }

    private void OnWindowVolume(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        _s.WindowVolume = (int)Math.Round(e.NewValue);
        if (_s.Target == TargetKind.Window)
            App.Controller.SetSystemGain(_s.WindowVolume);
        WindowVolumeText.Text = Loc.Format("Percent", _s.WindowVolume);
        Save();
    }

    private void OnMicVolume(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;
        _s.MicVolume = (int)Math.Round(e.NewValue);
        App.Controller.SetMicGain(_s.MicVolume);
        MicVolumeText.Text = Loc.Format("Percent", _s.MicVolume);
        Save();
    }

    // ------------------------------------------------------------------ guardar en

    /// <summary>Lo escrito en la carpeta se aplica al salir de la casilla y con Intro; si no vale, se avisa (General §6.8).</summary>
    private void ApplyFolder()
    {
        var text = FolderBox.Text.Trim();
        if (string.Equals(text, _s.OutputFolder, StringComparison.OrdinalIgnoreCase))
            return;
        if (RecorderSettings.CheckFolder(text) is { } error)
        {
            PromptWindow.Alert(this, Loc.Get("ErrorTitle"), Loc.Format(error, text));
            FolderBox.Text = _s.OutputFolder;
            return;
        }
        _s.OutputFolder = text;
        Save();
        ShowStatus(Loc.Get("FolderSaved"));
    }

    private void OnFolderLostFocus(object sender, RoutedEventArgs e) => ApplyFolder();

    private void OnFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ApplyFolder();
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("BrowseFolder") };
        if (Directory.Exists(_s.OutputFolder))
            dialog.InitialDirectory = _s.OutputFolder;
        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
            ApplyFolder();
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (RecorderSettings.CheckFolder(_s.OutputFolder) is { } error)
        {
            PromptWindow.Alert(this, Loc.Get("ErrorTitle"), Loc.Format(error, _s.OutputFolder));
            return;
        }
        Shell.Open(_s.OutputFolder, this);
    }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityCombo.SelectedItem is Choice<QualityPreset> c && !_loading)
        {
            // Un atajo rellena las opciones; «Personalizada» usa las guardadas (y si no hay, se abren).
            _s.Quality = c.Value;
            if (c.Value != QualityPreset.Custom)
                _s.CustomQuality = QualityOptions.ForPreset(c.Value);
            Save();
            if (c.Value == QualityPreset.Custom && _s.CustomQuality.Detect() != QualityPreset.Custom)
                OnQualityOptions(sender, e);
        }
        UpdateEstimate();
    }

    /// <summary>El diálogo de calidad; la ventana principal solo actualiza el desplegable y la estimación.</summary>
    private void OnQualityOptions(object sender, RoutedEventArgs e)
    {
        var dialog = new QualityWindow(_s.EffectiveQuality(), SourceSize) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;
        var q = dialog.Result;
        _s.CustomQuality = q;
        _s.Quality = q.Detect();
        Save();
        _loading = true;
        QualityCombo.SelectedItem = ((Choice<QualityPreset>[])QualityCombo.ItemsSource).First(c => c.Value == _s.Quality);
        _loading = false;
        UpdateEstimate();
    }

    /// <summary>El tamaño de lo que se va a grabar ahora mismo (para las estimaciones).</summary>
    private (int Width, int Height)? SourceSize()
    {
        RectI? source = _s.Target switch
        {
            TargetKind.AllMonitors => MonitorLayout.VirtualBounds(_monitors),
            TargetKind.Window => (WindowList.SelectedItem as WindowItem)?.Bounds,
            _ => MonitorLayout.Resolve(_monitors, _s.MonitorId)?.Bounds,
        };
        return source is { IsEmpty: false } r ? (r.Width, r.Height) : null;
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _s.ShowCursor = CursorCheck.IsChecked == true;
        _s.HideWhileRecording = HideCheck.IsChecked == true;
        Save();
    }

    /// <summary>Tamaño, imágenes por segundo y megas por minuto de lo elegido.</summary>
    private void UpdateEstimate()
    {
        if (EstimateText is null)
            return;
        if (SourceSize() is not { } size)
        {
            EstimateText.Text = Loc.Get(_s.Target == TargetKind.Window ? "EstimateNoWindow" : "EstimateUnknown");
            return;
        }
        var q = _s.EffectiveQuality();
        q.Codec = q.EffectiveCodec(EncoderCatalog.HevcAvailable);
        var plan = Presets.For(q, size.Width, size.Height);
        var audio = (_s.Target == TargetKind.Window ? _s.WindowAudio : _s.SystemAudio != SystemAudioMode.None) || _s.MicEnabled;
        var mb = plan.BytesPerMinute(audio) / 1024.0 / 1024.0;
        EstimateText.Text = Loc.Format(q.RateControl == RateControl.Quality ? "EstimateFormatApprox" : "EstimateFormat",
            plan.Width, plan.Height, plan.Fps, mb.ToString("0", CultureInfo.CurrentCulture), plan.Codec == VideoCodec.Hevc ? "HEVC" : "H.264");
    }

    // ------------------------------------------------------------------ atajos y automático

    private void ShowHotkeys()
    {
        var es = Loc.Language == "es";
        var start = Hotkey.Parse(_s.StartHotkey)?.Format(es) ?? string.Empty;
        var stop = Hotkey.Parse(_s.StopHotkey)?.Format(es) ?? string.Empty;
        StartHotkeyBox.Text = start;
        StopHotkeyBox.Text = stop;
        RecordHotkeyText.Text = start;
        StopHotkeyText.Text = stop;
        RecordButton.ToolTip = Loc.Format("ActionRecordTip", start);
        StopButton.ToolTip = Loc.Format("ActionStopTip", stop);
        if (App.HotkeyProblem is { } problem)
            HotkeyHint.Text = problem;
    }

    /// <summary>En las casillas de atajo se pulsa la combinación nueva (Esc deja la que había).</summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;
        if (key is Key.Escape or Key.Tab)
        {
            ShowHotkeys();
            if (key == Key.Tab)
                ((UIElement)sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            return;
        }
        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        var hotkey = Hotkey.TryCreate(mods, KeyInterop.VirtualKeyFromKey(key));
        var isStart = sender == StartHotkeyBox;
        var (start, stop) = isStart ? (hotkey?.ToString(), _s.StopHotkey) : (_s.StartHotkey, hotkey?.ToString());
        if (Hotkey.ValidatePair(start, stop) is { } error)
        {
            HotkeyHint.Text = Loc.Get(error);
            ShowStatus(Loc.Get(error));
            return;
        }
        _s.StartHotkey = start!;
        _s.StopHotkey = stop!;
        Save();
        App.ApplyHotkeys();
        HotkeyHint.Text = App.HotkeyProblem ?? Loc.Get("HotkeySaved");
        ShowHotkeys();
    }

    private void OnResetHotkeys(object sender, RoutedEventArgs e)
    {
        _s.StartHotkey = Hotkey.DefaultStart;
        _s.StopHotkey = Hotkey.DefaultStop;
        Save();
        App.ApplyHotkeys();
        HotkeyHint.Text = App.HotkeyProblem ?? Loc.Get("HotkeySaved");
        ShowHotkeys();
    }

    private void OnAutoRecord(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _s.AutoRecordEnabled = AutoRecordCheck.IsChecked == true;
        Save();
        App.Auto.Refresh();
        ShowAutoSummary();
        if (_s.AutoRecordEnabled && _s.AutoRules.Count == 0)
            OnAutoRules(sender, e);
    }

    private void OnAutoRules(object sender, RoutedEventArgs e)
    {
        new AutoRulesWindow { Owner = this }.ShowDialog();
        App.Auto.Refresh();
        ShowAutoSummary();
    }

    private void ShowAutoSummary()
    {
        var active = _s.AutoRules.Count(r => r.Enabled && r.Validate() is null);
        AutoSummaryText.Text = _s.AutoRules.Count == 0 ? Loc.Get("AutoNoRules")
            : Loc.Format("AutoSummary", active, _s.AutoRules.Count) + (Sandbox.IsOn ? " · " + Loc.Get("ModeSandbox") : string.Empty);
    }

    private void OnStartup(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var want = StartupCheck.IsChecked == true;
        if (!WindowsStartup.Set(want))
            ShowStatus(Loc.Get("StartupFailed"));
        StartupCheck.IsChecked = WindowsStartup.IsEnabled();
    }

    // ------------------------------------------------------------------ mandos

    private async void OnRecord(object sender, RoutedEventArgs e) => await App.Instance.StartRecordingAsync();

    private void OnPause(object sender, RoutedEventArgs e) => App.Controller.TogglePause();

    private async void OnStop(object sender, RoutedEventArgs e) => await App.Instance.StopRecordingAsync();

    private void UpdateControls()
    {
        var m = App.Controller.Machine;
        RecordButton.IsEnabled = m.CanStart;
        PauseButton.IsEnabled = m.CanPause || m.CanResume;
        StopButton.IsEnabled = m.CanStop;
        var paused = m.State == RecordingState.Paused;
        PauseButton.Content = paused ? "" : "";
        PauseButton.ToolTip = Loc.Get(paused ? "ActionResume" : "ActionPause");
        AutomationProperties.SetName(PauseButton, Loc.Get(paused ? "ActionResume" : "ActionPause"));
        var idle = !m.IsBusy;
        foreach (var c in new UIElement[] { MonitorTile, AllTile, WindowTile, MonitorMap, WindowList, RefreshWindowsButton, SystemAudioCombo, OutputDeviceCombo, AppCombo,
                     WindowAudioCheck, MicCheck, MicDeviceCombo, FolderBox, BrowseFolderButton, QualityCombo, QualityButton, CursorCheck, StartHotkeyBox, StopHotkeyBox, ResetHotkeysButton })
            c.IsEnabled = idle;
        if (idle)
        {
            ShowAudioPanels();
            SystemMeter.Value = WindowMeter.Value = MicMeter.Value = 0;
        }
        StatusText.Text = m.State switch
        {
            RecordingState.Starting => Loc.Get("StatusStarting"),
            RecordingState.Recording => Loc.Get(App.Controller.IsAutomatic ? "StatusRecordingAuto" : "StatusRecording"),
            RecordingState.Paused => Loc.Get("StatusPaused"),
            RecordingState.Stopping => Loc.Get("StatusStopping"),
            _ => StatusText.Text,
        };
        if (m.State == RecordingState.Starting)
            TimerText.Text = "00:00:00";
    }

    private void OnTick()
    {
        TimerText.Text = App.Controller.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        SystemMeter.Value = WindowMeter.Value = App.Controller.SystemPeak;
        MicMeter.Value = App.Controller.MicPeak;
    }

    public void ShowResult(RecordingResult r)
    {
        LastCard.Visibility = Visibility.Visible;
        LastNameText.Text = Path.GetFileName(r.Path);
        LastInfoText.Text = Loc.Format("LastInfo", (r.Bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.CurrentCulture),
            r.Duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture), r.Plan.Width, r.Plan.Height, r.Plan.Fps,
            Loc.Get(r.HasAudio ? "LastWithAudio" : "LastNoAudio"));
        StatusText.Text = Loc.Get("StatusSaved");
        TimerText.Text = r.Duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (App.Controller.LastResult is { } r)
            Shell.Open(r.Path, this);
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (App.Controller.LastResult is { } r)
            Shell.ShowInFolder(r.Path, this);
    }

    /// <summary>Copia el fichero (no la ruta): se pega tal cual en el Explorador, el correo o una conversación.</summary>
    private void OnCopyFile(object sender, RoutedEventArgs e)
    {
        if (App.Controller.LastResult is not { } r || !File.Exists(r.Path))
            return;
        try
        {
            Clipboard.SetFileDropList([r.Path]);
            ShowStatus(Loc.Get("CopiedFile"));
        }
        catch (Exception ex)
        {
            AppLog.Write($"copiar el fichero: {ex}");
            PromptWindow.Alert(this, Loc.Get("ErrorTitle"), Loc.Get("ErrCopy"));
        }
    }

    // ------------------------------------------------------------------ cabecera y cierre

    private void OnGuide(object sender, RoutedEventArgs e) => new GuideWindow { Owner = this }.ShowDialog();

    private void OnNews(object sender, RoutedEventArgs e) => new WhatsNewWindow { Owner = this }.ShowDialog();

    private void OnLanguage(object sender, RoutedEventArgs e) => Loc.Toggle();

    private void OnAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _s.WindowWidth = Width;
            _s.WindowHeight = Height;
            _s.Save();
        }
        if (App.Rebuilding)
            return;
        // Grabando o con la grabación automática encendida, se queda en la bandeja.
        if (App.Instance.KeepsRunningInTray)
        {
            e.Cancel = true;
            Hide();
            App.Instance.NotifyStillInTray();
            return;
        }
        if (App.Controller.Machine.IsBusy)
        {
            e.Cancel = true;
            _ = App.Instance.ExitAsync();
            return;
        }
        App.Instance.Dispatcher.BeginInvoke(() => _ = App.Instance.ExitAsync());
    }
}

/// <summary>Abrir con el programa de Windows y enseñar en el Explorador, con su aviso si falla.</summary>
public static class Shell
{
    public static void Open(string path, Window? owner)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            AppLog.Write($"abrir {path}: {ex.Message}");
            PromptWindow.Alert(owner, Loc.Get("ErrorTitle"), Loc.Format("ErrOpen", path));
        }
    }

    public static void ShowInFolder(string path, Window? owner)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex)
        {
            AppLog.Write($"enseñar {path}: {ex.Message}");
            PromptWindow.Alert(owner, Loc.Get("ErrorTitle"), Loc.Format("ErrOpen", path));
        }
    }
}
