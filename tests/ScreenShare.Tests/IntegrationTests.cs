using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using SocScreenShare.Audio;
using SocScreenShare.AutoRecord;
using SocScreenShare.Capture;
using SocScreenShare.Engine;
using SocScreenShare.Recording;
using SocScreenShare.Settings;

namespace SocScreenShare.Tests;

/// <summary>Solo corre con <c>SOC_SCS_INTEGRATION=1</c> (General §8.6): usa la pantalla y el sonido de verdad.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SOC_SCS_INTEGRATION") != "1")
            Skip = "Integración: SOC_SCS_INTEGRATION=1 (graba la pantalla unos segundos y emite tonos suaves).";
    }
}

/// <summary>
/// El motor de punta a punta: graba de verdad en una carpeta temporal (que se borra) y comprueba el
/// MP4 decodificándolo. Los tonos los emite el programa de prueba propio, muy bajos (amplitud 0,03).
/// </summary>
[Collection("integración")]
public sealed class IntegrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("scs-int").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static string ToneApp => Path.Combine(TextTests.FindRoot(), "tests", "ScreenShare.ToneApp", "bin",
#if DEBUG
        "Debug",
#else
        "Release",
#endif
        "net10.0-windows", "sOCScreenShareToneApp.exe");

    private static Process Tone(string args) => Process.Start(new ProcessStartInfo(ToneApp, args) { UseShellExecute = false })!;

    [IntegrationFact]
    public async Task Graba_la_pantalla_principal_dos_segundos_en_un_MP4_valido()
    {
        var monitors = Devices.Monitors();
        Assert.NotEmpty(monitors);
        var path = Path.Combine(_dir, "pantalla.mp4");
        var req = new RecordingRequest(TargetKind.Monitor, MonitorLayout.Resolve(monitors, null), monitors, 0, QualityOptions.ForPreset(QualityPreset.Share), true,
            new AudioPlan(SystemAudioMode.All, null, null, 1f, false, null, 1f), path);
        await using var r = await Recorder.StartAsync(req);
        r.SetSystemGain(0.8f);
        r.SetMicGain(0.5f);
        r.Pause();
        Assert.True(r.IsPaused);
        r.Resume();
        await Task.Delay(2000);
        var result = await r.StopAsync();
        Assert.Null(result.ErrorKey);
        Assert.True(result.VideoFrames >= 50);
        var p = Mp4Probe.Probe(path);
        Assert.True(p.HasVideo && p.VideoSamples > 0);
        Assert.Equal((r.Plan.Width, r.Plan.Height), (p.Width, p.Height));
        Assert.True(p.HasAudio && p.AudioSamples > 0);
        Assert.InRange(p.Duration.TotalSeconds, 1.5, 3.5);
        await Assert.ThrowsAsync<InvalidOperationException>(r.StopAsync);
    }

    [IntegrationFact]
    public async Task Todas_las_pantallas_sin_sonido_y_en_calidad_alta()
    {
        var monitors = Devices.Monitors();
        var path = Path.Combine(_dir, "todas.mp4");
        var req = new RecordingRequest(TargetKind.AllMonitors, null, monitors, 0, QualityOptions.ForPreset(QualityPreset.High), false,
            new AudioPlan(SystemAudioMode.None, null, null, 1f, false, null, 1f), path);
        await using var r = await Recorder.StartAsync(req);
        Assert.False(r.HasAudio);
        await Task.Delay(1500);
        var result = await r.StopAsync();
        Assert.Null(result.ErrorKey);
        var p = Mp4Probe.Probe(path);
        Assert.False(p.HasAudio);
        Assert.Equal(60, p.Fps, 0);
    }

    [IntegrationFact]
    public async Task Una_ventana_graba_solo_el_sonido_de_su_proceso()
    {
        Assert.True(AudioCatalog.AppCaptureSupported, "Este Windows no tiene captura por proceso.");
        var title = "SCS prueba " + Guid.NewGuid().ToString("N")[..6];
        using var a = Tone($"--title \"{title}\" --freq 440 --seconds 6 --amp 0.03 --delay 0.3");
        using var b = Tone("--no-window --freq 1000 --seconds 6 --amp 0.03 --delay 0.3");
        WindowInfo? w = null;
        for (var i = 0; i < 50 && w is null; i++)
        {
            await Task.Delay(100);
            w = Devices.RecordableWindows().FirstOrDefault(x => x.Title == title);
        }
        Assert.NotNull(w);
        var path = Path.Combine(_dir, "ventana.mp4");
        var req = new RecordingRequest(TargetKind.Window, null, Devices.Monitors(), w.Handle, new QualityOptions(), true,
            new AudioPlan(SystemAudioMode.App, null, Devices.ProcessOf(w.Handle), 1f, false, null, 1f), path);
        await using (var r = await Recorder.StartAsync(req))
        {
            Assert.Empty(r.StartNotices);
            await Task.Delay(3000);
            Assert.Null((await r.StopAsync()).ErrorKey);
        }
        a.WaitForExit(10000);
        b.WaitForExit(10000);
        var (stereo, rate) = Mp4Probe.ReadAudio(path);
        var mono = ToneDetector.ToMono(stereo);
        Assert.True(ToneDetector.Amplitude(mono, rate, 440) > 0.005, "falta el tono de la ventana");
        Assert.True(ToneDetector.Amplitude(mono, rate, 1000) < 0.001, "se ha colado el sonido de otro proceso");
    }

    [IntegrationFact]
    public async Task El_vigilante_ve_abrirse_y_cerrarse_una_ventana_y_la_regla_la_graba()
    {
        var title = "SCS auto " + Guid.NewGuid().ToString("N")[..6];
        var events = Channel.CreateUnbounded<(WindowEventKind Kind, nint Hwnd, WindowInfo? Info)>();
        using var watcher = new WindowWatcher();
        Assert.True(watcher.IsRunning);
        watcher.WindowEvent += (k, h, i) => events.Writer.TryWrite((k, h, i));
        var tracker = new AutoRecordTracker();
        var rules = new[] { new AutoRecordRule { ProcessName = "sOCScreenShareToneApp.exe", Title = title } };
        using var tone = Tone($"--title \"{title}\" --freq 440 --seconds 2.5 --amp 0.03 --delay 0.5");
        Recorder? r = null;
        string path = Path.Combine(_dir, "auto.mp4");
        RecordingResult? result = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (result is null)
        {
            var e = await events.Reader.ReadAsync(timeout.Token);
            var d = tracker.OnEvent(e.Kind, e.Hwnd, e.Info, rules, true, r is not null);
            if (d.Action == AutoAction.Start)
            {
                r = await Recorder.StartAsync(new RecordingRequest(TargetKind.Window, null, Devices.Monitors(), d.Window, new QualityOptions(), true,
                    new AudioPlan(SystemAudioMode.App, null, Devices.ProcessOf(d.Window), 1f, false, null, 1f), path));
            }
            else if (d.Action == AutoAction.Stop && r is not null)
            {
                result = await r.StopAsync();
                await r.DisposeAsync();
            }
        }
        Assert.Null(result.ErrorKey);
        var (stereo, rate) = Mp4Probe.ReadAudio(path);
        Assert.True(ToneDetector.Amplitude(ToneDetector.ToMono(stereo), rate, 440) > 0.005);
        Assert.InRange(result.Duration.TotalSeconds, 1.5, 6);
    }

    [IntegrationFact]
    public async Task Calidad_personalizada_sale_con_esos_parametros_exactos()
    {
        var monitors = Devices.Monitors();
        var hevc = EncoderCatalog.HevcAvailable;
        var q = new QualityOptions { Resolution = MaxResolution.P720, Fps = 24, Codec = VideoCodec.Hevc, AudioKbps = 128, AudioSampleRate = 44100, AudioChannels = 1 };
        var path = Path.Combine(_dir, "personalizada.mp4");
        await using var r = await Recorder.StartAsync(new RecordingRequest(TargetKind.Monitor, MonitorLayout.Resolve(monitors, null), monitors, 0, q, true,
            new AudioPlan(SystemAudioMode.All, null, null, 1f, false, null, 1f), path));
        if (!hevc)
            Assert.Contains(r.StartNotices, n => n.Key == "NoticeHevcUnavailable");
        await Task.Delay(2000);
        Assert.Null((await r.StopAsync()).ErrorKey);
        var p = Mp4Probe.Probe(path);
        Assert.Equal(hevc ? "HEVC" : "H264", p.VideoCodec);
        Assert.Equal(720, Math.Min(p.Width, p.Height));
        Assert.Equal(24, p.Fps, 0);
        Assert.Equal((44100, 1, 128), (p.AudioRate, p.AudioChannels, p.AudioKbps));
        Assert.True(p.VideoKbps > 0);
        _ = EncoderCatalog.HevcHardware;
        _ = EncoderCatalog.H264Hardware;
    }

    [IntegrationFact]
    public async Task Tasa_constante_a_mano_y_calidad_constante_se_graban()
    {
        var monitors = Devices.Monitors();
        foreach (var q in new[]
        {
            new QualityOptions { Resolution = MaxResolution.P480, RateControl = RateControl.Cbr, AutoBitrate = false, VideoMbps = 1 },
            new QualityOptions { Resolution = MaxResolution.P480, RateControl = RateControl.Quality, QualityLevel = 50 },
        })
        {
            var path = Path.Combine(_dir, $"tasa-{q.RateControl}.mp4");
            await using var r = await Recorder.StartAsync(new RecordingRequest(TargetKind.Monitor, monitors[0], monitors, 0, q, false,
                new AudioPlan(SystemAudioMode.None, null, null, 1f, false, null, 1f), path));
            await Task.Delay(1000);
            Assert.Null((await r.StopAsync()).ErrorKey);
            Assert.Equal(480, Math.Min(Mp4Probe.Probe(path).Width, Mp4Probe.Probe(path).Height));
        }
    }

    [IntegrationFact]
    public void Dispositivos_pantallas_y_ventanas_se_listan()
    {
        Assert.True(Devices.CaptureSupported);
        Assert.NotEmpty(Devices.Monitors());
        Assert.NotEmpty(Devices.Windows());
        _ = AudioCatalog.Outputs();
        _ = AudioCatalog.Inputs();
        _ = AudioCatalog.Apps();
        Assert.Null(AudioCatalog.ResolveProcess("no-existe-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(0, Devices.ProcessOf(0));
        Assert.Null(Devices.Describe(0));
        Assert.False(Devices.IsWindowAlive(0));
    }

    [IntegrationFact]
    public async Task Errores_previsibles_al_empezar()
    {
        var monitors = Devices.Monitors();
        var none = new AudioPlan(SystemAudioMode.None, null, null, 1f, false, null, 1f);
        var e1 = await Assert.ThrowsAsync<RecorderException>(() => Recorder.StartAsync(
            new RecordingRequest(TargetKind.Window, null, monitors, 0, new QualityOptions(), true, none, Path.Combine(_dir, "x.mp4"))));
        Assert.Equal("ErrWindowGone", e1.Key);
        var e2 = await Assert.ThrowsAsync<RecorderException>(() => Recorder.StartAsync(
            new RecordingRequest(TargetKind.Monitor, null, monitors, 0, new QualityOptions(), true, none, Path.Combine(_dir, "x.mp4"))));
        Assert.Equal("ErrNoMonitor", e2.Key);
        var e3 = await Assert.ThrowsAsync<RecorderException>(() => Recorder.StartAsync(
            new RecordingRequest(TargetKind.AllMonitors, null, [], 0, new QualityOptions(), true, none, Path.Combine(_dir, "x.mp4"))));
        Assert.Equal("ErrNoMonitor", e3.Key);
        var e4 = await Assert.ThrowsAsync<RecorderException>(() => Recorder.StartAsync(
            new RecordingRequest(TargetKind.Monitor, monitors[0], monitors, 0, new QualityOptions(), true, none, "rel\\x.mp4")));
        Assert.Equal("ErrFolderNotAbsolute", e4.Key);
    }

    [IntegrationFact]
    public async Task Aplicacion_que_no_esta_abierta_se_avisa_y_no_graba_otro_sonido()
    {
        var monitors = Devices.Monitors();
        var path = Path.Combine(_dir, "sin-app.mp4");
        await using var r = await Recorder.StartAsync(new RecordingRequest(TargetKind.Monitor, monitors[0], monitors, 0, new QualityOptions(), true,
            new AudioPlan(SystemAudioMode.App, null, null, 1f, false, null, 1f), path));
        Assert.Contains(r.StartNotices, n => n.Key == "NoticeAppNotRunning");
        Assert.False(r.HasAudio);
        await Task.Delay(500);
        Assert.Null((await r.StopAsync()).ErrorKey);
        Assert.Contains("samples", Mp4Probe.Probe(path).ToString(), StringComparison.Ordinal);
        _ = string.Format(CultureInfo.InvariantCulture, "{0}", r.Elapsed);
    }
}
