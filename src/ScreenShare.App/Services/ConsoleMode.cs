using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SocScreenShare.AutoRecord;
using SocScreenShare.Capture;
using SocScreenShare.Cli;
using SocScreenShare.Engine;
using SocScreenShare.Localization;
using SocScreenShare.Recording;
using SocScreenShare.Settings;

namespace SocScreenShare.App.Services;

/// <summary>
/// La línea de órdenes: <c>--record &lt;segundos&gt;</c> graba sin ventana con los ajustes guardados
/// (o lo que se indique) y comprueba el MP4; <c>--probe &lt;fichero&gt;</c> dice qué hay en un MP4.
/// Código de salida 0 si todo fue bien, 1 si no, 2 si la orden no vale.
/// </summary>
public static class ConsoleMode
{
    public static int Run(IReadOnlyList<string> args, RecorderSettings settings)
    {
        AttachConsole(-1);
        var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        output.WriteLine();
        try
        {
            return Task.Run(() => RunAsync(CommandLine.Parse(args), settings, output, error)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLog.Write($"línea de órdenes: {ex}");
            error.WriteLine(Loc.Format("ErrStartFailed", AppLog.FilePath));
            return 1;
        }
    }

    public static async Task<int> RunAsync(CliCommand cmd, RecorderSettings settings, TextWriter output, TextWriter error)
    {
        switch (cmd.Verb)
        {
            case CliVerb.Version:
                output.WriteLine(WhatsNew.CurrentVersion());
                return 0;
            case CliVerb.Help:
                if (cmd.ErrorKey is { } key)
                {
                    error.WriteLine(Loc.Format(key, cmd.ErrorArgument ?? string.Empty));
                    output.WriteLine(Loc.Get("CliHelp"));
                    return 2;
                }
                output.WriteLine(Loc.Get("CliHelp"));
                return 0;
            case CliVerb.Probe:
                return Probe(cmd.ProbePath!, output, error);
            case CliVerb.Record:
                break;
            case CliVerb.Auto:
                return await AutoAsync(cmd, settings, output, error);
            default:
                output.WriteLine(Loc.Get("CliHelp"));
                return 0;
        }

        var s = Apply(cmd, settings);
        var monitors = Devices.Monitors();
        nint window = 0;
        if (s.Target == TargetKind.Window)
        {
            var w = Devices.RecordableWindows().FirstOrDefault(w => cmd.WindowTitle is not null && w.Title.Contains(cmd.WindowTitle, StringComparison.CurrentCultureIgnoreCase));
            if (w is null)
            {
                error.WriteLine(Loc.Format("CliWindowNotFound", cmd.WindowTitle ?? string.Empty));
                return 2;
            }
            window = w.Handle;
        }
        if (cmd.MonitorNumber is { } n)
        {
            var m = monitors.FirstOrDefault(m => m.Number == n);
            if (m is null)
            {
                error.WriteLine(Loc.Format("CliMonitorNotFound", n));
                return 2;
            }
            s.MonitorId = m.Id;
        }
        RecordingRequest request;
        try
        {
            request = RecordingController.BuildRequest(s, new Selection(window, null));
            if (cmd.Output is { } outPath)
                request = request with { OutputPath = Path.GetFullPath(outPath.EndsWith(FileNamer.Extension, StringComparison.OrdinalIgnoreCase) ? outPath : outPath + FileNamer.Extension) };
        }
        catch (RecorderException ex)
        {
            error.WriteLine(Loc.Format(ex.Key, ex.Argument ?? string.Empty));
            return 1;
        }

        Recorder recorder;
        try
        {
            recorder = await Recorder.StartAsync(request);
        }
        catch (RecorderException ex)
        {
            AppLog.Write($"--record: {ex}");
            error.WriteLine(Loc.Format(ex.Key, ex.Argument ?? AppLog.FilePath));
            return 1;
        }
        await using (recorder)
        {
            foreach (var notice in recorder.StartNotices)
                error.WriteLine(Loc.Format(notice.Key, notice.Argument ?? string.Empty));
            output.WriteLine(Loc.Format("CliRecording", request.OutputPath, cmd.Seconds.ToString("0.##", CultureInfo.CurrentCulture),
                $"{recorder.Plan.Codec} {recorder.Plan.Width}×{recorder.Plan.Height}", recorder.Plan.Fps, recorder.HardwareEncoder ? Loc.Get("EncoderHardware") : Loc.Get("EncoderSoftware")));
            await Task.Delay(TimeSpan.FromSeconds(cmd.Seconds));
            var result = await recorder.StopAsync();
            if (result.ErrorKey is { } key)
            {
                error.WriteLine(Loc.Format(key, AppLog.FilePath));
                return 1;
            }
            output.WriteLine(Loc.Format("CliDone", result.Path, (result.Bytes / 1024.0 / 1024.0).ToString("0.00", CultureInfo.CurrentCulture),
                result.Duration.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture), result.VideoFrames, result.DroppedFrames));
        }
        return Probe(request.OutputPath, output, error);
    }

    /// <summary>
    /// <c>--auto &lt;aplicación&gt; [--title texto] [--regex] [--timeout s]</c>: espera a que se abra una
    /// ventana que cumpla la regla, la graba con su sonido y para al cerrarse. Lo mismo que hace la
    /// grabación automática de la ventana, sin ventana.
    /// </summary>
    private static async Task<int> AutoAsync(CliCommand cmd, RecorderSettings settings, TextWriter output, TextWriter error)
    {
        var rule = new AutoRecordRule
        {
            ProcessName = AutoRecordRule.NormalizeProcess(cmd.AutoProcess),
            Title = cmd.AutoTitle,
            TitleMode = cmd.AutoRegex ? TitleMatch.Regex : TitleMatch.Contains,
        };
        if (rule.Validate() is { } invalid)
        {
            error.WriteLine(Loc.Get(invalid));
            return 2;
        }
        var s = Apply(cmd, settings);
        s.Target = TargetKind.Window;
        var events = Channel.CreateUnbounded<(WindowEventKind Kind, nint Hwnd, WindowInfo? Info)>();
        using var watcher = new WindowWatcher();
        watcher.WindowEvent += (k, h, i) => events.Writer.TryWrite((k, h, i));
        var tracker = new AutoRecordTracker();
        output.WriteLine(Loc.Format("CliAutoWaiting", rule.ProcessName, rule.Title ?? "*"));

        Recorder? recorder = null;
        string? path = null;
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(cmd.TimeoutSeconds));
        try
        {
            while (true)
            {
                (WindowEventKind Kind, nint Hwnd, WindowInfo? Info) e;
                try
                {
                    e = await events.Reader.ReadAsync(recorder is null ? wait.Token : CancellationToken.None);
                }
                catch (OperationCanceledException)
                {
                    error.WriteLine(Loc.Format("CliAutoTimeout", cmd.TimeoutSeconds));
                    return 1;
                }
                var decision = tracker.OnEvent(e.Kind, e.Hwnd, e.Info, [rule], enabled: true, recording: recorder is not null);
                if (decision.Action == AutoAction.Start)
                {
                    try
                    {
                        var request = RecordingController.BuildRequest(s, new Selection(decision.Window, e.Info?.Title));
                        if (cmd.Output is { } o)
                            request = request with { OutputPath = Path.GetFullPath(o.EndsWith(FileNamer.Extension, StringComparison.OrdinalIgnoreCase) ? o : o + FileNamer.Extension) };
                        recorder = await Recorder.StartAsync(request);
                        path = request.OutputPath;
                        recorder.Notice += n =>
                        {
                            if (n.Key == "NoticeWindowClosed" && long.TryParse(n.Argument, out var closed))
                                events.Writer.TryWrite((WindowEventKind.Destroyed, (nint)closed, null));
                        };
                        foreach (var notice in recorder.StartNotices)
                            error.WriteLine(Loc.Format(notice.Key, notice.Argument ?? string.Empty));
                        output.WriteLine(Loc.Format("CliAutoStarted", e.Info?.Title ?? string.Empty, path));
                    }
                    catch (RecorderException ex)
                    {
                        tracker.Clear();
                        error.WriteLine(Loc.Format(ex.Key, ex.Argument ?? AppLog.FilePath));
                        return 1;
                    }
                }
                while (decision.Action == AutoAction.Switch && recorder is not null)
                {
                    try
                    {
                        await recorder.SwitchWindowAsync(decision.Window);
                        break;
                    }
                    catch (RecorderException)
                    {
                        decision = tracker.SwitchFailed(decision.Window, [rule], recording: true);
                    }
                }
                if (decision.Action == AutoAction.Stop && recorder is not null)
                {
                    var result = await recorder.StopAsync();
                    await recorder.DisposeAsync();
                    recorder = null;
                    if (result.ErrorKey is { } key)
                    {
                        error.WriteLine(Loc.Format(key, AppLog.FilePath));
                        return 1;
                    }
                    output.WriteLine(Loc.Format("CliDone", result.Path, (result.Bytes / 1024.0 / 1024.0).ToString("0.00", CultureInfo.CurrentCulture),
                        result.Duration.TotalSeconds.ToString("0.00", CultureInfo.CurrentCulture), result.VideoFrames, result.DroppedFrames));
                    return Probe(path!, output, error);
                }
            }
        }
        finally
        {
            if (recorder is not null)
            {
                try { await recorder.StopAsync(); } catch (Exception) { }
                await recorder.DisposeAsync();
            }
        }
    }

    /// <summary>Lo de la línea de órdenes encima de los ajustes guardados (sin guardarlo).</summary>
    public static RecorderSettings Apply(CliCommand cmd, RecorderSettings saved)
    {
        var s = saved.Clone();
        if (cmd.Target is { } t) s.Target = t;
        if (cmd.Quality is { } q) s.Quality = q;
        if (cmd.HasCustomQuality)
        {
            s.CustomQuality = cmd.ApplyQuality(s.EffectiveQuality()).Normalize();
            s.Quality = QualityPreset.Custom;
        }
        if (cmd.SystemAudio is { } a) s.SystemAudio = a;
        if (cmd.AppName is { } app) s.AppProcessName = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app[..^4] : app;
        if (cmd.Microphone is { } mic) s.MicEnabled = mic;
        if (cmd.Output is { } o && Path.GetDirectoryName(Path.GetFullPath(o)) is { } dir) s.OutputFolder = dir;
        return s;
    }

    private static int Probe(string path, TextWriter output, TextWriter error)
    {
        if (!File.Exists(path))
        {
            error.WriteLine(Loc.Format("CliFileNotFound", path));
            return 1;
        }
        try
        {
            var p = Mp4Probe.Probe(path);
            output.WriteLine(p.ToString());
            var ok = p.HasVideo && p.VideoSamples > 0 && (!p.HasAudio || p.AudioSamples > 0);
            output.WriteLine(Loc.Get(ok ? "CliProbeOk" : "CliProbeBad"));
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            AppLog.Write($"--probe {path}: {ex}");
            error.WriteLine(Loc.Format("CliProbeBad", path));
            return 1;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
