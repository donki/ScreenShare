using System.IO;
using System.Windows.Threading;
using SocScreenShare.Capture;
using SocScreenShare.Engine;
using SocScreenShare.Localization;
using SocScreenShare.Recording;
using SocScreenShare.Settings;

namespace SocScreenShare.App.Services;

/// <summary>Lo que la ventana ha elegido y no está en los ajustes (la ventana a grabar es de esta sesión).</summary>
public sealed record Selection(nint Window, string? WindowTitle);

/// <summary>
/// La grabación vista desde la interfaz: comprueba lo elegido, arranca el motor, lleva el estado
/// (<see cref="RecordingStateMachine"/>), el contador, la bandeja y los avisos, y al parar entrega el
/// resultado. La ventana, la bandeja y el atajo global pasan todos por aquí.
/// </summary>
public sealed class RecordingController
{
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _tick;
    private Recorder? _recorder;

    public RecordingController(Dispatcher ui)
    {
        _ui = ui;
        _tick = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => Tick?.Invoke();
        Machine.Changed += _ => _ui.BeginInvoke(() => StateChanged?.Invoke());
    }

    public RecordingStateMachine Machine { get; } = new();
    public TimeSpan Elapsed => _recorder?.Elapsed ?? TimeSpan.Zero;
    public float SystemPeak => _recorder?.SystemPeak ?? 0;
    public float MicPeak => _recorder?.MicPeak ?? 0;
    public RecordingResult? LastResult { get; private set; }

    /// <summary>La grabación en marcha la empezó una regla de grabación automática.</summary>
    public bool IsAutomatic { get; private set; }

    /// <summary>Cada cuarto de segundo mientras graba (contador, medidores, bandeja).</summary>
    public event Action? Tick;
    public event Action? StateChanged;

    /// <summary>Un aviso para el usuario (texto ya traducido) que no detiene la grabación.</summary>
    public event Action<string>? Notice;

    /// <summary>Un error que impide lo pedido: título y texto ya traducidos.</summary>
    public event Action<string, string>? Error;

    public event Action<RecordingResult>? Finished;

    public void SetSystemGain(int percent) => _recorder?.SetSystemGain(percent / 100f);
    public void SetMicGain(int percent) => _recorder?.SetMicGain(percent / 100f);

    public async Task StartAsync(RecorderSettings settings, Selection selection, bool automatic = false)
    {
        if (!Machine.TryStart())
            return;
        IsAutomatic = automatic;
        try
        {
            var request = BuildRequest(settings, selection);
            _recorder = await Recorder.StartAsync(request);
            _recorder.Notice += OnNotice;
            Machine.Started();
            _tick.Start();
            foreach (var n in _recorder.StartNotices)
                Notice?.Invoke(Text(n.Key, n.Argument));
        }
        catch (Exception ex)
        {
            Machine.Failed();
            IsAutomatic = false;
            _recorder = null;
            var (key, arg) = ex is RecorderException r ? (r.Key, r.Argument) : ("ErrStartFailed", null);
            AppLog.Write($"no se pudo empezar a grabar: {ex}");
            Error?.Invoke(Loc.Get("ErrorTitle"), Text(key, arg ?? AppLog.FilePath));
        }
    }

    /// <summary>De los ajustes y lo elegido a la petición del motor. Lanza <see cref="RecorderException"/> si falta algo.</summary>
    public static RecordingRequest BuildRequest(RecorderSettings s, Selection selection, DateTime? now = null)
    {
        if (RecorderSettings.CheckFolder(s.OutputFolder) is { } folderError)
            throw new RecorderException(folderError, s.OutputFolder);
        var monitors = Devices.Monitors();
        if (s.Target == TargetKind.Window && selection.Window == 0)
            throw new RecorderException("ErrNoWindowChosen");
        AudioPlan audio;
        if (s.Target == TargetKind.Window)
        {
            // Al grabar una ventana, su sonido y solo el suyo (el de su proceso y sus hijos), nunca
            // el del equipo entero; si Windows no sabe separarlo, el motor lo avisa y no graba otro.
            var pid = Devices.ProcessOf(selection.Window);
            audio = new AudioPlan(s.WindowAudio ? SystemAudioMode.App : SystemAudioMode.None, null, pid == 0 ? null : pid,
                s.WindowVolume / 100f, s.MicEnabled, s.MicDeviceId, s.MicVolume / 100f);
        }
        else
        {
            int? pid = null;
            if (s.SystemAudio == SystemAudioMode.App && s.AppProcessName is { } app)
                pid = AudioCatalog.ResolveProcess(app);
            audio = new AudioPlan(s.SystemAudio, s.OutputDeviceId, pid, s.SystemVolume / 100f,
                s.MicEnabled, s.MicDeviceId, s.MicVolume / 100f);
        }
        var path = FileNamer.NewPath(s.OutputFolder, Loc.Get("FilePrefix"), now ?? DateTime.Now);
        return new RecordingRequest(s.Target, MonitorLayout.Resolve(monitors, s.MonitorId), monitors, selection.Window,
            s.EffectiveQuality(), s.ShowCursor, audio, path);
    }

    public void TogglePause()
    {
        if (_recorder is null)
            return;
        if (Machine.TryPause())
            _recorder.Pause();
        else if (Machine.TryResume())
            _recorder.Resume();
    }

    public async Task<RecordingResult?> StopAsync()
    {
        if (_recorder is not { } recorder || !Machine.TryStop())
            return null;
        _tick.Stop();
        RecordingResult? result = null;
        try
        {
            result = await recorder.StopAsync();
        }
        catch (Exception ex)
        {
            AppLog.Write($"al parar: {ex}");
        }
        finally
        {
            recorder.Notice -= OnNotice;
            await recorder.DisposeAsync();
            _recorder = null;
            IsAutomatic = false;
            Machine.Stopped();
        }
        if (result is null)
        {
            Error?.Invoke(Loc.Get("ErrorTitle"), Loc.Format("ErrFinalize", AppLog.FilePath));
            return null;
        }
        LastResult = result;
        if (result.ErrorKey is { } key)
            Error?.Invoke(Loc.Get("ErrorTitle"), Text(key, AppLog.FilePath));
        if (result.Bytes > 0)
            Finished?.Invoke(result);
        else
            TryDelete(result.Path);
        return result;
    }

    private void OnNotice(RecorderNotice n) => _ui.BeginInvoke(async () =>
    {
        Notice?.Invoke(Text(n.Key, n.Argument));
        // Sin lo que se graba no tiene sentido seguir: se para y se guarda lo que haya.
        if (n.Key is "NoticeWindowClosed" or "NoticeMonitorGone" or "NoticeDiskFull" or "NoticeRecordingFailed")
            await StopAsync();
    });

    public static string Text(string key, string? argument) => Loc.Format(key, argument ?? string.Empty);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
}
