using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SocScreenShare.App.Services;
using SocScreenShare.Capture;
using SocScreenShare.Cli;
using SocScreenShare.Engine;
using SocScreenShare.Input;
using SocScreenShare.Localization;
using SocScreenShare.Settings;

namespace SocScreenShare.App;

/// <summary>
/// Arranque: gestor de excepciones → modo aislado → ajustes → línea de órdenes o la aplicación
/// (instancia única, bandeja, atajos globales, grabación automática y ventana).
/// </summary>
public partial class App : Application
{
    private MessageWindow? _messages;
    private TrayIcon? _tray;
    private bool _showingError;
    private bool _trayHintShown;

    public static App Instance { get; private set; } = null!;
    public static RecorderSettings Settings { get; private set; } = new();
    public static RecordingController Controller { get; private set; } = null!;
    public static AutoRecordService Auto { get; private set; } = null!;
    public static MainWindow? MainView { get; private set; }

    /// <summary>La ventana elegida para grabar (es de esta sesión: no se guarda).</summary>
    public static nint SelectedWindow { get; set; }
    public static string? SelectedWindowTitle { get; set; }

    /// <summary>Se está rehaciendo la ventana principal (cambio de idioma): cerrarla no es salir.</summary>
    public static bool Rebuilding { get; private set; }

    /// <summary>Por qué no funciona algún atajo (otra aplicación lo tiene), ya traducido; null si van los dos.</summary>
    public static string? HotkeyProblem { get; private set; }

    /// <summary>Cerrar la ventana la deja en la bandeja: grabando o con la grabación automática encendida.</summary>
    public bool KeepsRunningInTray => _tray is not null && (Controller.Machine.IsBusy || Settings.AutoRecordEnabled);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Instance = this;
        GuardAgainstCrashes();
        Sandbox.Apply();
        var args = e.Args.ToList();
        Settings = RecorderSettings.Load();
        if (Settings.Language is { } lang)
            Loc.Use(lang);

        // 1. Línea de órdenes (sin ventana).
        if (CommandLine.IsCommand(args))
        {
            Shutdown(ConsoleMode.Run(args, Settings));
            return;
        }

        // 2. La aplicación.
        ThemeManager.Apply();
        if (!Sandbox.IsOn && !SingleInstance.Claim())
        {
            Shutdown();
            return;
        }
        Controller = new RecordingController(Dispatcher);
        Controller.Error += (title, text) => Alert(title, text);
        Controller.Notice += text => _tray?.Notify(Loc.Get("AppTitle"), text);
        Controller.StateChanged += UpdateTray;
        Controller.StateChanged += () => ThemeManager.ExcludeAllFromCapture(Controller.Machine.IsBusy);
        Controller.Tick += UpdateTray;
        Auto = new AutoRecordService(Controller, () => Settings, Dispatcher);
        Auto.Toast += (title, text) => _tray?.Notify(title, text);
        Loc.LanguageChanged += OnLanguageChanged;

        if (!Sandbox.IsOn)
        {
            _messages = new MessageWindow();
            _messages.HotkeyPressed += OnHotkey;
            _messages.ShowRequested += ShowMain;
            ApplyHotkeys();
            _tray = new TrayIcon(BuildTrayMenu, ShowMain);
            UpdateTray();
            WindowsStartup.Refresh();
        }
        Auto.Refresh();

        if (!args.Contains("--tray") || Sandbox.IsOn || _tray is null)
        {
            ShowMain();
            var size = args.FindIndex(a => a == "--size");
            if (size >= 0 && size + 1 < args.Count && args[size + 1].Split('x') is [var w, var h] && double.TryParse(w, out var pw) && double.TryParse(h, out var ph))
            {
                MainView!.Width = pw;
                MainView.Height = ph;
            }
            MainView!.Dispatcher.BeginInvoke(ShowFirstRunWindows, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>La guía sale sola la primera vez; las novedades al cambiar de versión (§6.7, §6.10).</summary>
    private void ShowFirstRunWindows()
    {
        var current = WhatsNew.CurrentVersion();
        var (guide, news) = WhatsNew.OnStartup(Settings, current);
        Settings.GuideShown = true;
        Settings.LastSeenVersion = current;
        Settings.Save();
        if (guide)
            new GuideWindow { Owner = MainView }.ShowDialog();
        else if (news)
            new WhatsNewWindow { Owner = MainView }.ShowDialog();
    }

    public void ShowMain()
    {
        if (MainView is null)
        {
            MainView = new MainWindow();
            MainView.Closed += (_, _) => MainView = null;
        }
        MainView.Show();
        if (MainView.WindowState == WindowState.Minimized)
            MainView.WindowState = WindowState.Normal;
        if (!Sandbox.IsOn)
            MainView.Activate();
    }

    private void OnLanguageChanged()
    {
        Settings.Language = Loc.Language;
        Settings.Save();
        UpdateTray();
        // Los textos del XAML se fijan al construir: la ventana se vuelve a abrir ya traducida.
        if (MainView is { IsVisible: true } old)
        {
            var bounds = new Rect(old.Left, old.Top, old.Width, old.Height);
            var state = old.WindowState;
            Rebuilding = true;
            try
            {
                MainView = null;
                ShowMain();
                MainView!.Left = bounds.Left;
                MainView.Top = bounds.Top;
                MainView.Width = bounds.Width;
                MainView.Height = bounds.Height;
                MainView.WindowState = state;
                old.Close();
            }
            finally
            {
                Rebuilding = false;
            }
        }
    }

    // ------------------------------------------------------------------ grabar y parar

    public async Task StartRecordingAsync()
    {
        if (!Controller.Machine.CanStart)
            return;
        if (Settings.Target == TargetKind.Window && !Devices.IsWindowAlive(SelectedWindow))
        {
            ShowMain();
            Alert(Loc.Get("ErrorTitle"), Loc.Get("ErrNoWindowChosen"));
            return;
        }
        await Controller.StartAsync(Settings, new Selection(SelectedWindow, SelectedWindowTitle));
        // Sin bandeja (modo aislado) la ventana no se esconde: no habría por dónde volver.
        if (Controller.Machine.CanStop && Settings.HideWhileRecording && _tray is not null)
            MainView?.Hide();
    }

    public async Task StopRecordingAsync()
    {
        var automatic = Controller.IsAutomatic;
        var result = await Controller.StopAsync();
        if (result is not null && !automatic)
            ShowMain();
    }

    /// <summary>
    /// Registra los dos atajos. Si otra aplicación ya tiene uno, se dice en la ventana (la grabación
    /// se sigue pudiendo empezar y parar con los botones y desde la bandeja).
    /// </summary>
    public static void ApplyHotkeys()
    {
        HotkeyProblem = null;
        if (Instance._messages is not { } m)
            return;
        var start = Hotkey.Parse(Settings.StartHotkey);
        var stop = Hotkey.Parse(Settings.StopHotkey);
        var es = Loc.Language == "es";
        var okStart = start is not null && m.RegisterStart(start);
        var okStop = stop is not null && m.RegisterStop(stop);
        var taken = new List<string>();
        if (!okStart && start is not null) taken.Add(start.Format(es));
        if (!okStop && stop is not null) taken.Add(stop.Format(es));
        if (taken.Count > 0)
        {
            HotkeyProblem = Loc.Format("HotkeyTaken", string.Join(", ", taken));
            AppLog.Write($"atajo ocupado por otra aplicación: {string.Join(", ", taken)}");
        }
    }

    private async void OnHotkey(int which)
    {
        try
        {
            if (which == MessageWindow.StartId)
                await StartRecordingAsync();
            else if (which == MessageWindow.StopId && Controller.Machine.CanStop)
                await StopRecordingAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"atajo: {ex}");
        }
    }

    // ------------------------------------------------------------------ bandeja

    private void UpdateTray()
    {
        if (_tray is null)
            return;
        var m = Controller.Machine;
        _tray.SetRecording(m.IsBusy);
        _tray.SetTip(m.State switch
        {
            Recording.RecordingState.Recording => Loc.Format("TrayRecording", Controller.Elapsed.ToString(@"hh\:mm\:ss")),
            Recording.RecordingState.Paused => Loc.Format("TrayPaused", Controller.Elapsed.ToString(@"hh\:mm\:ss")),
            _ => Settings.AutoRecordEnabled ? Loc.Get("TrayWatching") : Loc.Get("AppTitle"),
        });
    }

    private ContextMenu BuildTrayMenu()
    {
        var m = Controller.Machine;
        var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu") };
        var es = Loc.Language == "es";
        if (m.IsBusy)
        {
            menu.Items.Add(m.CanResume
                ? TrayIcon.Item(Loc.Get("ActionResume"), "", Controller.TogglePause)
                : TrayIcon.Item(Loc.Get("ActionPause"), "", Controller.TogglePause, m.CanPause));
            menu.Items.Add(TrayIcon.Item(Loc.Get("ActionStop") + "  " + Hotkey.Parse(Settings.StopHotkey)?.Format(es), "", () => _ = StopRecordingAsync(), m.CanStop));
        }
        else
        {
            menu.Items.Add(TrayIcon.Item(Loc.Get("ActionRecord") + "  " + Hotkey.Parse(Settings.StartHotkey)?.Format(es), "", () => _ = StartRecordingAsync(), danger: true));
        }
        menu.Items.Add(TrayIcon.Separator());
        menu.Items.Add(TrayIcon.Item(Loc.Get(Settings.AutoRecordEnabled ? "TrayAutoOff" : "TrayAutoOn"), Settings.AutoRecordEnabled ? "" : "", () =>
        {
            Settings.AutoRecordEnabled = !Settings.AutoRecordEnabled;
            Settings.Save();
            Auto.Refresh();
            UpdateTray();
            if (MainView is { } w)
            {
                // La ventana enseña el interruptor: se rehace para que no mienta.
                var visible = w.IsVisible;
                Rebuilding = true;
                try { MainView = null; w.Close(); } finally { Rebuilding = false; }
                if (visible) ShowMain();
            }
        }));
        menu.Items.Add(TrayIcon.Item(Loc.Get("TrayOpen"), "", ShowMain));
        menu.Items.Add(TrayIcon.Separator());
        menu.Items.Add(TrayIcon.Item(Loc.Get("TrayExit"), "", () => _ = ExitAsync()));
        return menu;
    }

    public void NotifyStillInTray()
    {
        if (_trayHintShown || _tray is null)
            return;
        _trayHintShown = true;
        _tray.Notify(Loc.Get("AppTitle"), Loc.Get(Controller.Machine.IsBusy ? "TrayStillRecording" : "TrayStillWatching"));
    }

    /// <summary>Salir: si está grabando, primero se para y se guarda.</summary>
    public async Task ExitAsync()
    {
        if (Controller.Machine.IsBusy)
            await Controller.StopAsync();
        Auto.Dispose();
        _messages?.Dispose();
        _messages = null;
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _messages?.Dispose();
        base.OnExit(e);
    }

    // ------------------------------------------------------------------ errores

    public void Alert(string title, string text)
    {
        if (_showingError)
            return;
        _showingError = true;
        try
        {
            PromptWindow.Alert(MainView is { IsVisible: true } w ? w : null, title, text);
        }
        catch (Exception ex)
        {
            AppLog.Write($"no se pudo enseñar el aviso: {ex}");
        }
        finally
        {
            _showingError = false;
        }
    }

    /// <summary>
    /// Gestor global de excepciones (General §6.12): un error inesperado se apunta con su traza y
    /// se avisa en el idioma del usuario, y la aplicación sigue abierta.
    /// </summary>
    private void GuardAgainstCrashes()
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            AppLog.Write($"error no controlado: {ex.Exception}");
            ex.Handled = true;
            Alert(Loc.Get("UnexpectedErrorTitle"), Loc.Format("UnexpectedErrorText", AppLog.FilePath));
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            AppLog.Write($"error no controlado en tarea: {ex.Exception}");
            ex.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => AppLog.Write($"error fatal: {ex.ExceptionObject}");
    }
}
