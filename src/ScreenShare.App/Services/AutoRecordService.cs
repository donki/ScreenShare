using System.Windows.Threading;
using SocScreenShare.AutoRecord;
using SocScreenShare.Capture;
using SocScreenShare.Engine;
using SocScreenShare.Localization;
using SocScreenShare.Settings;

namespace SocScreenShare.App.Services;

/// <summary>
/// La grabación automática en la aplicación: con el interruptor encendido y alguna regla activa,
/// vigila las ventanas (<see cref="WindowWatcher"/>, en su propio hilo) y, cuando se abre una que
/// cumple una regla, graba esa ventana con su sonido; al cerrarse, para y guarda. La decisión la toma
/// <see cref="AutoRecordTracker"/> en el hilo de la interfaz. Si el programa abre otra ventana de la
/// misma regla mientras graba (TeamViewer: del selector a la conexión), la grabación se pasa a ella.
/// </summary>
public sealed class AutoRecordService : IDisposable
{
    private readonly RecordingController _controller;
    private readonly Func<RecorderSettings> _settings;
    private readonly Dispatcher _ui;
    private readonly AutoRecordTracker _tracker = new();
    private WindowWatcher? _watcher;

    public AutoRecordService(RecordingController controller, Func<RecorderSettings> settings, Dispatcher ui)
    {
        _controller = controller;
        _settings = settings;
        _ui = ui;
        _controller.StateChanged += () =>
        {
            if (!_controller.Machine.IsBusy)
                _tracker.Clear();
        };
        _controller.AutoWindowClosed += hwnd => Handle(WindowEventKind.Destroyed, hwnd, null);
    }

    /// <summary>Avisos discretos para la bandeja (título y texto ya traducidos).</summary>
    public event Action<string, string>? Toast;

    public bool IsWatching => _watcher is not null;

    /// <summary>Enciende o apaga la vigilancia según los ajustes (sin vigilancia en el modo aislado: General §8.4).</summary>
    public void Refresh()
    {
        var s = _settings();
        var wanted = s.AutoRecordEnabled && s.AutoRules.Any(r => r.Enabled && r.Validate() is null) && !Sandbox.IsOn;
        if (wanted && _watcher is null)
        {
            _watcher = new WindowWatcher();
            _watcher.WindowEvent += (k, h, i) => _ui.BeginInvoke(() => Handle(k, h, i));
        }
        else if (!wanted && _watcher is not null)
        {
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private async void Handle(WindowEventKind kind, nint hwnd, WindowInfo? info)
    {
        try
        {
            var s = _settings();
            var decision = _tracker.OnEvent(kind, hwnd, info, s.AutoRules, s.AutoRecordEnabled, _controller.Machine.IsBusy);
            if (decision.Action == AutoAction.Start)
            {
                var copy = s.Clone();
                copy.Target = TargetKind.Window;
                await _controller.StartAsync(copy, new Selection(decision.Window, info?.Title), automatic: true);
                if (_controller.Machine.CanStop)
                    Toast?.Invoke(Loc.Get("AutoStartedTitle"), Loc.Format("AutoStartedText", info?.Title ?? decision.Rule!.ProcessName));
                else
                    _tracker.Clear();
            }
            while (decision.Action == AutoAction.Switch && _controller.IsAutomatic)
            {
                if (await _controller.SwitchWindowAsync(decision.Window))
                    return;
                decision = _tracker.SwitchFailed(decision.Window, s.AutoRules, _controller.Machine.IsBusy);
            }
            if (decision.Action == AutoAction.Stop && _controller.IsAutomatic)
            {
                var result = await _controller.StopAsync();
                if (result is not null)
                    Toast?.Invoke(Loc.Get("AutoStoppedTitle"), Loc.Format("AutoStoppedText", System.IO.Path.GetFileName(result.Path)));
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"grabación automática: {ex}");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}

/// <summary>
/// «Arrancar con Windows»: HKCU\…\Run con el exe y <c>--tray</c> (arranca en la bandeja). Solo
/// desde la versión publicada: un exe Debug no se apunta nunca (sOC Lucia, 2026-09-22).
/// </summary>
public static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "sOCScreenShare";

#if DEBUG
    public static bool IsAvailable => false;
#else
    public static bool IsAvailable => !Sandbox.IsOn;
#endif

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Si está puesto, que apunte a este exe (no a una copia vieja).</summary>
    public static void Refresh()
    {
        if (!IsAvailable)
            return;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            var current = key?.GetValue(ValueName) as string;
            if (string.IsNullOrEmpty(current) || Environment.ProcessPath is not { Length: > 0 } exe)
                return;
            if (!string.Equals(current, Command(exe), StringComparison.OrdinalIgnoreCase))
                Set(true);
        }
        catch (Exception) { }
    }

    public static string Command(string exe) => "\"" + exe + "\" --tray";

    public static bool Set(bool enabled)
    {
        if (!IsAvailable)
            return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled && Environment.ProcessPath is { Length: > 0 } exe)
                key.SetValue(ValueName, Command(exe));
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"arranque con Windows: {ex}");
            return false;
        }
    }
}
