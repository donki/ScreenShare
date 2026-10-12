using System.Globalization;
using SocScreenShare.Capture;
using SocScreenShare.Recording;
using SocScreenShare.Settings;

namespace SocScreenShare.Cli;

public enum CliVerb
{
    /// <summary>Sin órdenes: se abre la ventana.</summary>
    None,
    Help,
    Record,
    Probe,
    Version,

    /// <summary>Esperar a que se abra una ventana y grabarla hasta que se cierre.</summary>
    Auto,
}

/// <summary>
/// Lo que se pide por la línea de órdenes. Lo que no se diga sale de los ajustes guardados, así que
/// <c>--record 5</c> graba cinco segundos con lo mismo que la ventana.
/// </summary>
public sealed record CliCommand
{
    public CliVerb Verb { get; init; }
    public double Seconds { get; init; }
    public string? Output { get; init; }
    public TargetKind? Target { get; init; }
    public int? MonitorNumber { get; init; }
    public string? WindowTitle { get; init; }
    public QualityPreset? Quality { get; init; }
    public SystemAudioMode? SystemAudio { get; init; }
    public string? AppName { get; init; }
    public bool? Microphone { get; init; }
    public string? ProbePath { get; init; }
    public string? AutoProcess { get; init; }
    public string? AutoTitle { get; init; }
    public bool AutoRegex { get; init; }
    public double TimeoutSeconds { get; init; } = 60;

    // Calidad a mano (cualquiera de ellas la vuelve «personalizada»).
    public MaxResolution? Resolution { get; init; }
    public int? Fps { get; init; }
    public double? VideoMbps { get; init; }
    public VideoCodec? Codec { get; init; }
    public RateControl? Rate { get; init; }
    public int? QualityLevel { get; init; }
    public int? AudioKbps { get; init; }
    public int? AudioRate { get; init; }
    public int? Channels { get; init; }

    public bool HasCustomQuality => Resolution is not null || Fps is not null || VideoMbps is not null || Codec is not null
        || Rate is not null || QualityLevel is not null || AudioKbps is not null || AudioRate is not null || Channels is not null;

    /// <summary>Las opciones de calidad con lo indicado encima de <paramref name="baseline"/>.</summary>
    public QualityOptions ApplyQuality(QualityOptions baseline)
    {
        var q = baseline.Clone();
        if (Resolution is { } r) q.Resolution = r;
        if (Fps is { } f) q.Fps = f;
        if (VideoMbps is { } m) { q.VideoMbps = m; q.AutoBitrate = false; }
        if (Codec is { } c) q.Codec = c;
        if (Rate is { } rc) q.RateControl = rc;
        if (QualityLevel is { } ql) { q.QualityLevel = ql; q.RateControl = RateControl.Quality; }
        if (AudioKbps is { } k) q.AudioKbps = k;
        if (AudioRate is { } ar) q.AudioSampleRate = ar;
        if (Channels is { } ch) q.AudioChannels = ch;
        return q;
    }

    /// <summary>Clave de <c>Loc</c> del error, si la orden no vale.</summary>
    public string? ErrorKey { get; init; }
    public string? ErrorArgument { get; init; }
}

public static class CommandLine
{
    public const double MaxSeconds = 4 * 3600;

    /// <summary>¿Hay que trabajar por consola (sin ventana)?</summary>
    public static bool IsCommand(IReadOnlyList<string> args) =>
        args.Any(a => a is "--record" or "--probe" or "--auto" or "--help" or "-h" or "/?" or "--version");

    public static CliCommand Parse(IReadOnlyList<string> args)
    {
        var cmd = new CliCommand();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (a)
            {
                case "--help" or "-h" or "/?":
                    return new CliCommand { Verb = CliVerb.Help };
                case "--version":
                    return new CliCommand { Verb = CliVerb.Version };
                case "--record":
                    var s = Next();
                    if (s is null || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) || secs <= 0 || secs > MaxSeconds)
                        return Error("CliBadSeconds", s);
                    cmd = cmd with { Verb = CliVerb.Record, Seconds = secs };
                    break;
                case "--out":
                    var o = Next();
                    if (string.IsNullOrWhiteSpace(o))
                        return Error("CliMissingValue", a);
                    cmd = cmd with { Output = o };
                    break;
                case "--target":
                    TargetKind? t = Next()?.ToLowerInvariant() switch
                    {
                        "monitor" or "screen" => TargetKind.Monitor,
                        "all" => TargetKind.AllMonitors,
                        "window" => TargetKind.Window,
                        _ => null,
                    };
                    if (t is null)
                        return Error("CliBadValue", a);
                    cmd = cmd with { Target = t };
                    break;
                case "--monitor":
                    if (!int.TryParse(Next(), out var n) || n < 1)
                        return Error("CliBadValue", a);
                    cmd = cmd with { MonitorNumber = n, Target = cmd.Target ?? TargetKind.Monitor };
                    break;
                case "--window":
                    var w = Next();
                    if (string.IsNullOrWhiteSpace(w))
                        return Error("CliMissingValue", a);
                    cmd = cmd with { WindowTitle = w, Target = TargetKind.Window };
                    break;
                case "--quality":
                    QualityPreset? q = Next()?.ToLowerInvariant() switch
                    {
                        "share" => QualityPreset.Share,
                        "high" => QualityPreset.High,
                        _ => null,
                    };
                    if (q is null)
                        return Error("CliBadValue", a);
                    cmd = cmd with { Quality = q };
                    break;
                case "--system-audio":
                    cmd = cmd with { SystemAudio = SystemAudioMode.All };
                    break;
                case "--no-audio":
                    cmd = cmd with { SystemAudio = SystemAudioMode.None, Microphone = false };
                    break;
                case "--no-system-audio":
                    cmd = cmd with { SystemAudio = SystemAudioMode.None };
                    break;
                case "--app":
                    var app = Next();
                    if (string.IsNullOrWhiteSpace(app))
                        return Error("CliMissingValue", a);
                    cmd = cmd with { SystemAudio = SystemAudioMode.App, AppName = app };
                    break;
                case "--mic":
                    cmd = cmd with { Microphone = true };
                    break;
                case "--no-mic":
                    cmd = cmd with { Microphone = false };
                    break;
                case "--auto":
                    var proc = Next();
                    if (string.IsNullOrWhiteSpace(proc))
                        return Error("CliMissingValue", a);
                    cmd = cmd with { Verb = CliVerb.Auto, AutoProcess = proc };
                    break;
                case "--title":
                    var title = Next();
                    if (string.IsNullOrEmpty(title))
                        return Error("CliMissingValue", a);
                    cmd = cmd with { AutoTitle = title };
                    break;
                case "--regex":
                    cmd = cmd with { AutoRegex = true };
                    break;
                case "--timeout":
                    var ts = Next();
                    if (ts is null || !double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out var tsec) || tsec <= 0 || tsec > MaxSeconds)
                        return Error("CliBadValue", a);
                    cmd = cmd with { TimeoutSeconds = tsec };
                    break;
                case "--resolution":
                    MaxResolution? res = Next()?.ToLowerInvariant().TrimEnd('p') switch
                    {
                        "original" => MaxResolution.Original,
                        "2160" or "4k" => MaxResolution.P2160,
                        "1440" => MaxResolution.P1440,
                        "1080" => MaxResolution.P1080,
                        "720" => MaxResolution.P720,
                        "480" => MaxResolution.P480,
                        _ => null,
                    };
                    if (res is null)
                        return Error("CliBadValue", a);
                    cmd = cmd with { Resolution = res };
                    break;
                case "--fps":
                    if (!int.TryParse(Next(), out var fps) || !QualityOptions.FpsChoices.Contains(fps))
                        return Error("CliBadValue", a);
                    cmd = cmd with { Fps = fps };
                    break;
                case "--mbps":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mbps) || mbps < QualityOptions.MinMbps || mbps > QualityOptions.MaxMbps)
                        return Error("CliBadValue", a);
                    cmd = cmd with { VideoMbps = mbps };
                    break;
                case "--codec":
                    VideoCodec? codec = Next()?.ToLowerInvariant() switch
                    {
                        "h264" or "avc" => VideoCodec.H264,
                        "hevc" or "h265" => VideoCodec.Hevc,
                        _ => null,
                    };
                    if (codec is null)
                        return Error("CliBadValue", a);
                    cmd = cmd with { Codec = codec };
                    break;
                case "--rate":
                    RateControl? rate = Next()?.ToLowerInvariant() switch
                    {
                        "cbr" => RateControl.Cbr,
                        "vbr" => RateControl.Vbr,
                        "quality" => RateControl.Quality,
                        _ => null,
                    };
                    if (rate is null)
                        return Error("CliBadValue", a);
                    cmd = cmd with { Rate = rate };
                    break;
                case "--quality-level":
                    if (!int.TryParse(Next(), out var ql) || ql is < 0 or > 100)
                        return Error("CliBadValue", a);
                    cmd = cmd with { QualityLevel = ql };
                    break;
                case "--audio-kbps":
                    if (!int.TryParse(Next(), out var kbps) || !QualityOptions.IsAacSupported(kbps))
                        return Error("CliBadAudioKbps", null);
                    cmd = cmd with { AudioKbps = kbps };
                    break;
                case "--audio-rate":
                    if (!int.TryParse(Next(), out var ar) || !QualityOptions.SampleRates.Contains(ar))
                        return Error("CliBadValue", a);
                    cmd = cmd with { AudioRate = ar };
                    break;
                case "--channels":
                    int? ch = Next()?.ToLowerInvariant() switch { "1" or "mono" => 1, "2" or "stereo" => 2, _ => null };
                    if (ch is null)
                        return Error("CliBadValue", a);
                    cmd = cmd with { Channels = ch };
                    break;
                case "--probe":
                    var p = Next();
                    if (string.IsNullOrWhiteSpace(p))
                        return Error("CliMissingValue", a);
                    cmd = cmd with { ProbePath = p, Verb = cmd.Verb == CliVerb.Record ? CliVerb.Record : CliVerb.Probe };
                    break;
                default:
                    return Error("CliUnknownOption", a);
            }
        }
        return cmd;
    }

    private static CliCommand Error(string key, string? arg) => new() { Verb = CliVerb.Help, ErrorKey = key, ErrorArgument = arg };
}
