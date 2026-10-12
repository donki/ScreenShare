using System.Text;
using SocScreenShare.AutoRecord;
using SocScreenShare.Capture;
using SocScreenShare.Cli;
using SocScreenShare.Input;
using SocScreenShare.Localization;
using SocScreenShare.Recording;
using SocScreenShare.Settings;
using SocScreenShare.Video;

namespace SocScreenShare.Tests;

/// <summary>Las pruebas que cambian algo estático (registro, rutas, idioma) no corren a la vez.</summary>
[CollectionDefinition("estado global")]
public sealed class GlobalStateCollection;

public sealed class VideoTests
{
    [Fact]
    public void Encuadre_centrado_con_bandas()
    {
        Assert.Equal(new RectI(0, 0, 1920, 1080), Letterbox.Fit(3840, 2160, 1920, 1080));
        Assert.Equal(new RectI(0, 270, 1920, 540), Letterbox.Fit(3840, 1080, 1920, 1080));
        Assert.Equal(new RectI(656, 0, 608, 1080), Letterbox.Fit(1080, 1920, 1920, 1080));
        Assert.Equal(new RectI(0, 0, 640, 480), Letterbox.Fit(0, 0, 640, 480));
    }

    [Fact]
    public void Rectangulos_union_y_corte()
    {
        var u = RectI.Union([new RectI(-1920, 0, 1920, 1080), new RectI(0, -200, 2560, 1440)]);
        Assert.Equal(new RectI(-1920, -200, 4480, 1440), u);
        Assert.Equal(default, RectI.Union([]));
        Assert.True(default(RectI).IsEmpty);
        Assert.Equal(new RectI(10, 10, 5, 5), new RectI(0, 0, 15, 15).Intersect(new RectI(10, 10, 100, 100)));
        Assert.Equal(default, new RectI(0, 0, 5, 5).Intersect(new RectI(10, 10, 5, 5)));
    }

    [Fact]
    public void Blanco_negro_y_colores_a_NV12_BT709()
    {
        Assert.Equal(16, Nv12Converter.Luma(0, 0, 0));
        Assert.Equal(235, Nv12Converter.Luma(255, 255, 255));
        Assert.Equal((128, 128), Nv12Converter.Chroma(128, 128, 128));
        var (u, v) = Nv12Converter.Chroma(255, 0, 0);
        Assert.True(v > 200 && u < 128);   // el rojo tira de Cr arriba
    }

    [Fact]
    public void Convierte_y_escala_con_bandas_negras()
    {
        // Origen 4x2 blanco → destino 8x8: imagen de 8x4 centrada y bandas negras arriba y abajo.
        var bgra = new byte[4 * 2 * 4];
        bgra.AsSpan().Fill(255);
        var nv12 = new byte[Nv12Converter.BufferSize(8, 8)];
        Nv12Converter.Convert(bgra, 4, 2, 16, nv12, 8, 8);
        Assert.Equal(16, nv12[0]);              // banda de arriba
        Assert.Equal(235, nv12[2 * 8 + 3]);     // imagen
        Assert.Equal(16, nv12[7 * 8 + 7]);      // banda de abajo
        Assert.Equal(128, nv12[64]);            // croma neutro
        Assert.Throws<ArgumentException>(() => Nv12Converter.Convert(bgra, 4, 2, 16, nv12, 7, 8));
        Assert.Throws<ArgumentException>(() => Nv12Converter.Convert(bgra, 4, 2, 16, new byte[10], 8, 8));
    }
}

public sealed class TargetTests
{
    private static WindowInfo W(string title = "Documento", bool visible = true, bool cloaked = false, bool tool = false,
        bool owner = false, bool minimized = false, int pid = 10, string proc = "notepad", int w = 800, int h = 600) =>
        new(1, title, pid, proc, visible, cloaked, tool, owner, minimized, new RectI(0, 0, w, h));

    [Fact]
    public void Solo_se_ofrecen_ventanas_de_verdad()
    {
        Assert.True(WindowFilter.IsRecordable(W(), 99));
        Assert.True(WindowFilter.IsRecordable(W(minimized: true, w: 160, h: 28), 99));
        Assert.False(WindowFilter.IsRecordable(W(visible: false), 99));
        Assert.False(WindowFilter.IsRecordable(W(cloaked: true), 99));
        Assert.False(WindowFilter.IsRecordable(W(tool: true), 99));
        Assert.False(WindowFilter.IsRecordable(W(owner: true), 99));
        Assert.False(WindowFilter.IsRecordable(W(title: " "), 99));
        Assert.False(WindowFilter.IsRecordable(W(pid: 99), 99));
        Assert.False(WindowFilter.IsRecordable(W(proc: "TextInputHost"), 99));
        Assert.False(WindowFilter.IsRecordable(W(w: 10, h: 10), 99));
        var list = WindowFilter.Recordable([W("b"), W("A"), W(visible: false)], 99);
        Assert.Equal(["A", "b"], list.Select(x => x.Title));
    }

    private static readonly MonitorInfo Left = new(@"\\.\DISPLAY1", 1, new RectI(-1920, 0, 1920, 1080), false);
    private static readonly MonitorInfo Main = new(@"\\.\DISPLAY2", 2, new RectI(0, 0, 2560, 1440), true);

    [Fact]
    public void La_pantalla_guardada_o_la_principal()
    {
        Assert.Equal(Left, MonitorLayout.Resolve([Left, Main], @"\\.\display1"));
        Assert.Equal(Main, MonitorLayout.Resolve([Left, Main], @"\\.\DISPLAY9"));
        Assert.Equal(Main, MonitorLayout.Resolve([Left, Main], null));
        Assert.Equal(Left, MonitorLayout.Resolve([Left], null));
        Assert.Null(MonitorLayout.Resolve([], null));
        Assert.Equal("2560×1440", Main.SizeText);
    }

    [Fact]
    public void Lienzo_de_todas_y_mapa_a_escala()
    {
        var all = MonitorLayout.VirtualBounds([Left, Main]);
        Assert.Equal(new RectI(-1920, 0, 4480, 1440), all);
        Assert.Equal((0, 0), MonitorLayout.OffsetInCanvas(Left, all));
        Assert.Equal((1920, 0), MonitorLayout.OffsetInCanvas(Main, all));
        var map = MonitorLayout.Map([Left, Main], 448, 200, gap: 0);
        Assert.Equal(2, map.Count);
        Assert.Equal(0, map[0].X, 3);
        Assert.Equal(192, map[1].X, 3);
        Assert.Equal(256, map[1].W, 3);
        Assert.Empty(MonitorLayout.Map([], 100, 100));
    }
}

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F9", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x78)]
    [InlineData("ctrl + mayús + f10", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x79)]
    [InlineData("Alt+R", HotkeyModifiers.Alt, 'R')]
    [InlineData("Control+Alt+5", HotkeyModifiers.Control | HotkeyModifiers.Alt, '5')]
    [InlineData("F24", HotkeyModifiers.None, 0x87)]
    [InlineData("Win+Pause", HotkeyModifiers.Win, 0x13)]
    [InlineData("Shift+Insert", HotkeyModifiers.Shift, 0x2D)]
    [InlineData("Ctrl+Home", HotkeyModifiers.Control, 0x24)]
    [InlineData("Ctrl+End", HotkeyModifiers.Control, 0x23)]
    [InlineData("PrintScreen", HotkeyModifiers.None, 0x2C)]
    public void Se_leen(string text, HotkeyModifiers mods, int vk) => Assert.Equal(new Hotkey(mods, vk), Hotkey.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ctrl+Shift")]
    [InlineData("R")]          // una letra suelta se comería la tecla en todo Windows
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Esc")]
    public void No_se_leen(string? text) => Assert.Null(Hotkey.Parse(text));

    [Fact]
    public void Se_escriben_en_ingles_y_se_ensenan_en_el_idioma()
    {
        var k = Hotkey.Parse("Ctrl+Shift+Alt+Win+F9")!;
        Assert.Equal("Ctrl+Shift+Alt+Win+F9", k.ToString());
        Assert.Equal("Ctrl+Mayús+Alt+Win+F9", k.Format(spanish: true));
        foreach (var t in new[] { "Ctrl+Pause", "Ctrl+Insert", "Ctrl+Home", "Ctrl+End", "PrintScreen", "Alt+Q", "Ctrl+7" })
            Assert.Equal(Hotkey.Parse(t), Hotkey.Parse(Hotkey.Parse(t)!.ToString()));
    }

    [Fact]
    public void Los_de_Windows_estan_reservados()
    {
        Assert.True(Hotkey.Parse("Win+L")!.IsReserved);
        Assert.True(Hotkey.Parse("Alt+F4")!.IsReserved);
        Assert.True(Hotkey.Parse("PrintScreen")!.IsReserved);
        Assert.True(Hotkey.Parse("F1")!.IsReserved);
        Assert.True(Hotkey.Parse("F11")!.IsReserved);
        Assert.True(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x2E).IsReserved);
        Assert.False(Hotkey.Parse(Hotkey.DefaultStart)!.IsReserved);
        Assert.False(Hotkey.Parse(Hotkey.DefaultStop)!.IsReserved);
    }

    [Fact]
    public void Los_dos_atajos_juntos_por_defecto_valen()
    {
        Assert.Null(Hotkey.ValidatePair(Hotkey.DefaultStart, Hotkey.DefaultStop));
        Assert.Equal("ErrHotkeySame", Hotkey.ValidatePair("Ctrl+Shift+F9", "ctrl+shift+f9"));
        Assert.Equal("ErrHotkeyReserved", Hotkey.ValidatePair("Win+R", "Ctrl+Shift+F10"));
        Assert.Equal("ErrHotkeyInvalid", Hotkey.ValidatePair("Ctrl+", "Ctrl+Shift+F10"));
        Assert.Equal("ErrHotkeyInvalid", Hotkey.ValidatePair("Ctrl+Shift+F9", null));
    }

    [Fact]
    public void Desde_la_tecla_pulsada()
    {
        Assert.Equal(Hotkey.Parse("Ctrl+Shift+F8"), Hotkey.TryCreate(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x77));
        Assert.Equal(Hotkey.Parse("Alt+K"), Hotkey.TryCreate(HotkeyModifiers.Alt, 'K'));
        Assert.Null(Hotkey.TryCreate(HotkeyModifiers.Control, 0x1B));   // Esc no se admite
        Assert.Null(Hotkey.TryCreate(HotkeyModifiers.None, 'K'));        // sin modificador
    }
}

public sealed class CommandLineTests
{
    [Fact]
    public void Sin_ordenes_se_abre_la_ventana()
    {
        Assert.False(CommandLine.IsCommand(["--tray"]));
        Assert.Equal(CliVerb.None, CommandLine.Parse([]).Verb);
        Assert.True(CommandLine.IsCommand(["--record", "3"]));
        Assert.True(CommandLine.IsCommand(["--auto", "x"]));
    }

    [Fact]
    public void Grabar_con_todo()
    {
        var c = CommandLine.Parse(["--record", "2.5", "--out", @"C:\v\a.mp4", "--monitor", "2", "--quality", "high", "--system-audio", "--mic"]);
        Assert.Equal(CliVerb.Record, c.Verb);
        Assert.Equal(2.5, c.Seconds);
        Assert.Equal(@"C:\v\a.mp4", c.Output);
        Assert.Equal(TargetKind.Monitor, c.Target);
        Assert.Equal(2, c.MonitorNumber);
        Assert.Equal(QualityPreset.High, c.Quality);
        Assert.Equal(SystemAudioMode.All, c.SystemAudio);
        Assert.True(c.Microphone);
        Assert.Null(c.ErrorKey);
    }

    [Fact]
    public void Ventana_aplicacion_y_sin_sonido()
    {
        var c = CommandLine.Parse(["--record", "1", "--window", "Bloc", "--app", "msedge", "--no-mic"]);
        Assert.Equal(TargetKind.Window, c.Target);
        Assert.Equal("Bloc", c.WindowTitle);
        Assert.Equal(SystemAudioMode.App, c.SystemAudio);
        Assert.Equal("msedge", c.AppName);
        Assert.False(c.Microphone);
        var n = CommandLine.Parse(["--record", "1", "--target", "all", "--no-audio"]);
        Assert.Equal(TargetKind.AllMonitors, n.Target);
        Assert.Equal(SystemAudioMode.None, n.SystemAudio);
        Assert.False(n.Microphone);
        Assert.Equal(SystemAudioMode.None, CommandLine.Parse(["--record", "1", "--target", "window", "--no-system-audio"]).SystemAudio);
        Assert.Equal(QualityPreset.Share, CommandLine.Parse(["--record", "1", "--quality", "share", "--target", "screen"]).Quality);
    }

    [Fact]
    public void Calidad_a_mano_por_la_linea_de_ordenes()
    {
        var c = CommandLine.Parse(["--record", "3", "--resolution", "720p", "--fps", "24", "--codec", "hevc", "--audio-kbps", "128",
            "--channels", "mono", "--audio-rate", "44100", "--mbps", "2.5", "--rate", "cbr"]);
        Assert.True(c.HasCustomQuality);
        var q = c.ApplyQuality(QualityOptions.ForPreset(QualityPreset.Share));
        Assert.Equal((MaxResolution.P720, 24, VideoCodec.Hevc, 128, 1, 44100, false, 2.5, RateControl.Cbr),
            (q.Resolution, q.Fps, q.Codec, q.AudioKbps, q.AudioChannels, q.AudioSampleRate, q.AutoBitrate, q.VideoMbps, q.RateControl));
        var ql = CommandLine.Parse(["--record", "1", "--quality-level", "80", "--resolution", "original", "--codec", "h264", "--channels", "stereo"])
            .ApplyQuality(new QualityOptions());
        Assert.Equal((RateControl.Quality, 80, MaxResolution.Original, 2), (ql.RateControl, ql.QualityLevel, ql.Resolution, ql.AudioChannels));
        Assert.False(CommandLine.Parse(["--record", "1"]).HasCustomQuality);
        foreach (var r in new[] { "2160", "4k", "1440", "1080", "480" })
            Assert.True(CommandLine.Parse(["--record", "1", "--resolution", r]).HasCustomQuality);
        Assert.Equal(RateControl.Vbr, CommandLine.Parse(["--record", "1", "--rate", "vbr"]).Rate);
        Assert.Equal(RateControl.Quality, CommandLine.Parse(["--record", "1", "--rate", "quality"]).Rate);
    }

    [Fact]
    public void Automatica_comprobar_ayuda_y_version()
    {
        var a = CommandLine.Parse(["--auto", "Teams.exe", "--title", "Reunión", "--regex", "--timeout", "30"]);
        Assert.Equal((CliVerb.Auto, "Teams.exe", "Reunión", true, 30.0), (a.Verb, a.AutoProcess, a.AutoTitle, a.AutoRegex, a.TimeoutSeconds));
        Assert.Equal(60, CommandLine.Parse(["--auto", "x"]).TimeoutSeconds);
        Assert.Equal(CliVerb.Probe, CommandLine.Parse(["--probe", "a.mp4"]).Verb);
        Assert.Equal(CliVerb.Record, CommandLine.Parse(["--record", "1", "--probe", "a.mp4"]).Verb);
        Assert.Equal(CliVerb.Help, CommandLine.Parse(["/?"]).Verb);
        Assert.Equal(CliVerb.Version, CommandLine.Parse(["--version"]).Verb);
    }

    [Theory]
    [InlineData("CliBadSeconds", "--record")]
    [InlineData("CliBadSeconds", "--record", "0")]
    [InlineData("CliBadSeconds", "--record", "abc")]
    [InlineData("CliBadSeconds", "--record", "999999")]
    [InlineData("CliMissingValue", "--out")]
    [InlineData("CliBadValue", "--target", "tele")]
    [InlineData("CliBadValue", "--monitor", "0")]
    [InlineData("CliMissingValue", "--window")]
    [InlineData("CliBadValue", "--quality", "ultra")]
    [InlineData("CliMissingValue", "--app")]
    [InlineData("CliMissingValue", "--probe")]
    [InlineData("CliMissingValue", "--auto")]
    [InlineData("CliMissingValue", "--title")]
    [InlineData("CliBadValue", "--timeout", "-1")]
    [InlineData("CliUnknownOption", "--bailar")]
    [InlineData("CliBadValue", "--resolution", "999")]
    [InlineData("CliBadValue", "--fps", "50")]
    [InlineData("CliBadValue", "--mbps", "0.01")]
    [InlineData("CliBadValue", "--codec", "vp9")]
    [InlineData("CliBadValue", "--rate", "abr")]
    [InlineData("CliBadValue", "--quality-level", "101")]
    [InlineData("CliBadAudioKbps", "--audio-kbps", "320")]
    [InlineData("CliBadValue", "--audio-rate", "22050")]
    [InlineData("CliBadValue", "--channels", "5.1")]
    public void Ordenes_que_no_valen(string key, params string[] args)
    {
        var c = CommandLine.Parse(args);
        Assert.Equal(CliVerb.Help, c.Verb);
        Assert.Equal(key, c.ErrorKey);
    }
}

[Collection("estado global")]
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("scs-settings").FullName;
    private readonly string _oldPath = RecorderSettings.FilePath;
    private readonly string _oldLog = AppLog.Folder;

    public SettingsTests()
    {
        RecorderSettings.FilePath = Path.Combine(_dir, "settings.json");
        AppLog.Folder = _dir;
    }

    public void Dispose()
    {
        RecorderSettings.FilePath = _oldPath;
        AppLog.Folder = _oldLog;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Sin_fichero_salen_los_de_por_defecto()
    {
        var s = RecorderSettings.Load();
        Assert.Equal(TargetKind.Monitor, s.Target);
        Assert.Equal(QualityPreset.Share, s.Quality);
        Assert.Equal(SystemAudioMode.All, s.SystemAudio);
        Assert.True(s.WindowAudio);
        Assert.False(s.MicEnabled);
        Assert.Equal(Hotkey.DefaultStart, s.StartHotkey);
        Assert.Equal(Hotkey.DefaultStop, s.StopHotkey);
        Assert.False(s.AutoRecordEnabled);
        Assert.Empty(s.AutoRules);
        Assert.True(s.EffectiveQuality().SameAs(QualityOptions.ForPreset(QualityPreset.Share)));
    }

    [Fact]
    public void La_calidad_personalizada_se_guarda_y_un_atajo_manda_sobre_lo_guardado()
    {
        var custom = new QualityOptions { Resolution = MaxResolution.P720, Fps = 24, Codec = VideoCodec.Hevc, AudioKbps = 96, AudioSampleRate = 44100, AudioChannels = 1, AutoBitrate = false, VideoMbps = 1.5 };
        new RecorderSettings { Quality = QualityPreset.Custom, CustomQuality = custom }.Save();
        var back = RecorderSettings.Load();
        Assert.Equal(QualityPreset.Custom, back.Quality);
        Assert.True(back.EffectiveQuality().SameAs(custom));
        Assert.Contains("\"codec\": \"hevc\"", File.ReadAllText(RecorderSettings.FilePath));
        // Con un atajo, lo personalizado guardado no cuenta: se rellena con el atajo.
        new RecorderSettings { Quality = QualityPreset.High, CustomQuality = custom }.Save();
        var high = RecorderSettings.Load();
        Assert.True(high.CustomQuality.SameAs(QualityOptions.ForPreset(QualityPreset.High)));
        Assert.True(high.EffectiveQuality().SameAs(QualityOptions.ForPreset(QualityPreset.High)));
    }

    [Fact]
    public void Calidad_invalida_se_corrige_al_cargar()
    {
        File.WriteAllText(RecorderSettings.FilePath, """
            { "quality": "custom", "customQuality": { "fps": 1000, "audioKbps": 256, "audioSampleRate": 8000, "audioChannels": 3, "videoMbps": -2, "resolution": 77 } }
            """);
        var q = RecorderSettings.Load().EffectiveQuality();
        Assert.Equal((60, 192, 44100, 2, 0.2, MaxResolution.P1080), (q.Fps, q.AudioKbps, q.AudioSampleRate, q.AudioChannels, q.VideoMbps, q.Resolution));
        File.WriteAllText(RecorderSettings.FilePath, """{ "quality": "custom", "customQuality": null }""");
        Assert.True(RecorderSettings.Load().EffectiveQuality().SameAs(new QualityOptions()));
    }

    [Fact]
    public void Se_guarda_y_se_recupera_todo_incluidos_atajos_y_reglas()
    {
        var s = new RecorderSettings
        {
            Language = "es", OutputFolder = @"D:\vid", Target = TargetKind.Window, MonitorId = @"\\.\DISPLAY2", Quality = QualityPreset.High,
            SystemAudio = SystemAudioMode.App, AppProcessName = "msedge", SystemVolume = 40, MicEnabled = true, MicDeviceId = "mic1", MicVolume = 70,
            WindowAudio = false, WindowVolume = 55, StartHotkey = "Alt+F7", StopHotkey = "Alt+F8", AutoRecordEnabled = true,
            AutoRules = [new AutoRecordRule { ProcessName = "Teams", Title = "Reunión", TitleMode = TitleMatch.Regex, StopOnClose = false }],
        };
        Assert.True(s.Save());
        var json = File.ReadAllText(RecorderSettings.FilePath);
        Assert.Contains("\"quality\": \"high\"", json);
        var back = RecorderSettings.Load();
        Assert.Equal(("es", @"D:\vid", TargetKind.Window, QualityPreset.High, SystemAudioMode.App, "msedge", 40), (back.Language, back.OutputFolder, back.Target, back.Quality, back.SystemAudio, back.AppProcessName, back.SystemVolume));
        Assert.Equal((true, "mic1", 70, false, 55), (back.MicEnabled, back.MicDeviceId, back.MicVolume, back.WindowAudio, back.WindowVolume));
        Assert.Equal(("Alt+F7", "Alt+F8", true), (back.StartHotkey, back.StopHotkey, back.AutoRecordEnabled));
        var rule = Assert.Single(back.AutoRules);
        Assert.Equal(("Teams", "Reunión", TitleMatch.Regex, false), (rule.ProcessName, rule.Title, rule.TitleMode, rule.StopOnClose));
        var copy = back.Clone();
        copy.AutoRules[0].Title = "otro";
        Assert.Equal("Reunión", back.AutoRules[0].Title);   // la copia es independiente
    }

    [Fact]
    public void Lo_ilegible_no_impide_arrancar_y_se_apunta()
    {
        File.WriteAllText(RecorderSettings.FilePath, "{ esto no es json");
        var s = RecorderSettings.Load();
        Assert.Equal(TargetKind.Monitor, s.Target);
        Assert.Contains("ajustes ilegibles", File.ReadAllText(AppLog.FilePath));
    }

    [Fact]
    public void Lo_que_no_vale_se_corrige_al_cargar()
    {
        File.WriteAllText(RecorderSettings.FilePath, """
            { "language": "fr", "outputFolder": "  ", "target": 7, "quality": 9, "systemAudio": 5, "systemVolume": 250,
              "micVolume": -3, "windowVolume": 400, "startHotkey": "Ctrl+Shift+F9", "stopHotkey": "Ctrl+Shift+F9",
              "monitorId": " ", "outputDeviceId": "", "micDeviceId": " ", "appProcessName": "", "windowWidth": -5,
              "autoRules": [ { "id": "", "processName": "C:\\x\\Teams.exe", "titleMode": 9 }, null ] }
            """);
        var s = RecorderSettings.Load();
        Assert.Null(s.Language);
        Assert.Equal(RecorderSettings.DefaultFolder, s.OutputFolder);
        Assert.Equal((TargetKind.Monitor, QualityPreset.Share, SystemAudioMode.All), (s.Target, s.Quality, s.SystemAudio));
        Assert.Equal((100, 0, 100), (s.SystemVolume, s.MicVolume, s.WindowVolume));
        Assert.Equal((Hotkey.DefaultStart, Hotkey.DefaultStop), (s.StartHotkey, s.StopHotkey));   // iguales: vuelven los de por defecto
        Assert.Null(s.MonitorId);
        Assert.Null(s.OutputDeviceId);
        Assert.Null(s.MicDeviceId);
        Assert.Null(s.AppProcessName);
        Assert.Equal(0, s.WindowWidth);
        var rule = Assert.Single(s.AutoRules);
        Assert.Equal("Teams", rule.ProcessName);
        Assert.Equal(TitleMatch.Contains, rule.TitleMode);
        Assert.NotEmpty(rule.Id);
    }

    [Fact]
    public void Comprobar_la_carpeta()
    {
        Assert.Null(RecorderSettings.CheckFolder(Path.Combine(_dir, "nueva", "sub")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "nueva", "sub")));
        Assert.Equal("ErrFolderEmpty", RecorderSettings.CheckFolder(" "));
        Assert.Equal("ErrFolderNotAbsolute", RecorderSettings.CheckFolder("relativa"));
        var file = Path.Combine(_dir, "soy-un-fichero");
        File.WriteAllText(file, "");
        Assert.Equal("ErrFolderNotWritable", RecorderSettings.CheckFolder(file));
    }

    [Fact]
    public void Si_no_se_puede_guardar_se_dice()
    {
        RecorderSettings.FilePath = Path.Combine(_dir, "no:valido", "settings.json");
        Assert.False(new RecorderSettings().Save());
    }

    [Fact]
    public void El_registro_rota_al_pasar_de_un_mega()
    {
        File.WriteAllText(AppLog.FilePath, new string('x', 1024 * 1024 + 10));
        AppLog.Write("nuevo");
        Assert.True(File.Exists(Path.Combine(_dir, "errors.old.log")));
        Assert.Contains("nuevo", File.ReadAllText(AppLog.FilePath));
    }
}

public sealed class AutoRecordTests
{
    private static WindowInfo Win(string proc, string title, bool visible = true, bool tool = false, nint h = 7) =>
        new(h, title, 1, proc, visible, false, tool, false, false, new RectI(0, 0, 800, 600));

    private static AutoRecordRule Rule(string proc, string? title = null, TitleMatch mode = TitleMatch.Contains, bool enabled = true, bool stop = true) =>
        new() { ProcessName = proc, Title = title, TitleMode = mode, Enabled = enabled, StopOnClose = stop };

    [Theory]
    [InlineData("Teams", "Teams.exe")]
    [InlineData("Teams", @"C:\Program Files\Teams\Teams.EXE")]
    [InlineData("Teams", "  Teams ")]
    [InlineData("", null)]
    public void Nombre_del_ejecutable(string expected, string? input) => Assert.Equal(expected, AutoRecordRule.NormalizeProcess(input));

    [Fact]
    public void Coincidencias_por_programa_y_titulo()
    {
        Assert.True(AutoRecordMatcher.Matches(Rule("teams.exe"), Win("Teams", "Chat")));
        Assert.False(AutoRecordMatcher.Matches(Rule("Teams"), Win("Zoom", "Chat")));
        Assert.True(AutoRecordMatcher.Matches(Rule("Teams", "reunión"), Win("Teams", "Reunión semanal")));
        Assert.False(AutoRecordMatcher.Matches(Rule("Teams", "reunión"), Win("Teams", "Chat")));
        Assert.True(AutoRecordMatcher.Matches(Rule("Teams", "^Reunión .*semanal$", TitleMatch.Regex), Win("Teams", "reunión muy semanal")));
        Assert.False(AutoRecordMatcher.Matches(Rule("Teams", "^Reunión$", TitleMatch.Regex), Win("Teams", "Reunión semanal")));
        Assert.False(AutoRecordMatcher.Matches(Rule("Teams", enabled: false), Win("Teams", "Chat")));
        Assert.False(AutoRecordMatcher.Matches(Rule("Teams", "(", TitleMatch.Regex), Win("Teams", "(")));   // regex mala: nunca coincide
    }

    [Fact]
    public void Se_ignoran_invisibles_herramientas_y_sin_titulo()
    {
        var rules = new[] { Rule("Teams") };
        Assert.NotNull(AutoRecordMatcher.Match(rules, Win("Teams", "Chat")));
        Assert.Null(AutoRecordMatcher.Match(rules, Win("Teams", "Chat", visible: false)));
        Assert.Null(AutoRecordMatcher.Match(rules, Win("Teams", "Chat", tool: true)));
        Assert.Null(AutoRecordMatcher.Match(rules, Win("Teams", "")));
        Assert.Null(AutoRecordMatcher.Match([], Win("Teams", "Chat")));
    }

    [Fact]
    public void Validar_reglas()
    {
        Assert.Equal("ErrRuleNoApp", Rule("").Validate());
        Assert.Equal("ErrRuleBadRegex", Rule("Teams", "[", TitleMatch.Regex).Validate());
        Assert.Null(Rule("Teams", "[", TitleMatch.Contains).Validate());
        var r = Rule("Teams", "x");
        var c = r.Clone();
        Assert.Equal((r.Id, r.ProcessName, r.Title), (c.Id, c.ProcessName, c.Title));
        Assert.NotSame(r, c);
    }

    [Fact]
    public void Empieza_al_abrirse_y_para_al_cerrarse()
    {
        var t = new AutoRecordTracker();
        var rules = new[] { Rule("Teams", "Reunión") };
        Assert.Equal(AutoAction.None, t.OnEvent(WindowEventKind.Shown, 7, Win("Teams", "Chat"), rules, true, false).Action);
        var start = t.OnEvent(WindowEventKind.TitleChanged, 7, Win("Teams", "Reunión semanal"), rules, true, false);
        Assert.Equal((AutoAction.Start, (nint)7), (start.Action, start.Window));
        Assert.Equal(7, t.CurrentWindow);
        Assert.Same(rules[0], t.CurrentRule);
        // Otra ventana que cambia de título no hace nada; la misma tampoco vuelve a empezar.
        Assert.Equal(AutoAction.None, t.OnEvent(WindowEventKind.Hidden, 8, null, rules, true, true).Action);
        Assert.Equal(AutoAction.None, t.OnEvent(WindowEventKind.TitleChanged, 7, Win("Teams", "Reunión 2"), rules, true, true).Action);
        var stop = t.OnEvent(WindowEventKind.Destroyed, 7, null, rules, true, true);
        Assert.Equal(AutoAction.Stop, stop.Action);
        Assert.Equal(0, t.CurrentWindow);
    }

    [Fact]
    public void Nunca_una_segunda_grabacion_ni_con_el_interruptor_apagado()
    {
        var t = new AutoRecordTracker();
        var rules = new[] { Rule("Teams") };
        Assert.Equal(AutoAction.None, t.OnEvent(WindowEventKind.Shown, 7, Win("Teams", "Chat"), rules, enabled: true, recording: true).Action);
        Assert.Equal(AutoAction.None, t.OnEvent(WindowEventKind.Shown, 7, Win("Teams", "Chat"), rules, enabled: false, recording: false).Action);
        Assert.Equal(AutoAction.None, t.OnEvent(WindowEventKind.Shown, 7, null, rules, true, false).Action);
    }

    [Fact]
    public void Parada_a_mano_no_vuelve_a_empezar_hasta_que_la_ventana_se_cierra()
    {
        var t = new AutoRecordTracker();
        var rules = new[] { Rule("Teams") };
        Assert.Equal(AutoAction.Start, t.OnEvent(WindowEventKind.Shown, 7, Win("Teams", "Chat"), rules, true, false).Action);
        t.Clear();   // se paró a mano
        Assert.Equal(AutoAction.None, t.OnEvent(WindowEventKind.TitleChanged, 7, Win("Teams", "Chat 2"), rules, true, false).Action);
        Assert.Equal(AutoAction.None, t.OnEvent(WindowEventKind.Destroyed, 7, null, rules, true, false).Action);
        Assert.Equal(AutoAction.Start, t.OnEvent(WindowEventKind.Shown, 7, Win("Teams", "Chat"), rules, true, false).Action);   // el mismo identificador, otra ventana
    }

    [Fact]
    public void Sin_parar_al_cerrarse_la_grabacion_sigue()
    {
        var t = new AutoRecordTracker();
        var rules = new[] { Rule("Teams", stop: false) };
        t.OnEvent(WindowEventKind.Shown, 7, Win("Teams", "Chat"), rules, true, false);
        Assert.Equal(AutoAction.None, t.OnEvent(WindowEventKind.Hidden, 7, null, rules, true, true).Action);
        Assert.Equal(7, t.CurrentWindow);   // escondida no es cerrada: sigue siendo la suya
        Assert.Equal(AutoAction.None, t.OnEvent(WindowEventKind.Destroyed, 7, null, rules, true, true).Action);
        Assert.Equal(0, t.CurrentWindow);
    }

    [Fact]
    public void Esconderse_tambien_para_si_la_regla_lo_dice()
    {
        var t = new AutoRecordTracker();
        var rules = new[] { Rule("Teams") };
        t.OnEvent(WindowEventKind.Shown, 7, Win("Teams", "Chat"), rules, true, false);
        Assert.Equal(AutoAction.Stop, t.OnEvent(WindowEventKind.Hidden, 7, null, rules, true, true).Action);
        Assert.Equal(AutoAction.None, t.OnEvent(WindowEventKind.Destroyed, 7, null, rules, true, false).Action);
    }
}

[Collection("estado global")]
public sealed class TextTests
{
    [Fact]
    public void Las_traducciones_tienen_las_mismas_claves_y_ninguna_vacia()
    {
        Assert.Equal(Loc.EnglishTable.Keys.Order(), Loc.SpanishTable.Keys.Order());
        Assert.All(Loc.EnglishTable.Values.Concat(Loc.SpanishTable.Values), v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    [Fact]
    public void Las_claves_que_usa_el_codigo_existen()
    {
        var root = FindRoot();
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file, Encoding.UTF8);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"Loc\.(?:Get|Format)\(""([A-Za-z0-9]+)""|\{loc:T ([A-Za-z0-9]+)\}|""((?:Err|Notice)[A-Za-z0-9]+)"""))
                used.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        }
        var missing = used.Where(k => !Loc.EnglishTable.ContainsKey(k)).Order().ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Cambiar_de_idioma_avisa_y_lo_que_falta_no_ensena_la_clave()
    {
        var before = Loc.Language;
        var changes = 0;
        void OnChange() => changes++;
        Loc.LanguageChanged += OnChange;
        try
        {
            Loc.Use("es");
            Loc.Use("es");
            Assert.Equal("Grabar", Loc.Get("ActionRecord"));
            Loc.Toggle();
            Assert.Equal("en", Loc.Language);
            Assert.Equal("Record", Loc.Get("ActionRecord"));
            Assert.Equal(string.Empty, Loc.Get("NoExiste"));
            Assert.Equal("Screen 2 · 1920×1080", Loc.Format("MonitorItem", 2, "1920×1080"));
            Assert.Equal("{0} needs a value.", Loc.Format("CliMissingValue"));   // faltan argumentos: el texto tal cual
            Loc.Use("xx");
            Assert.Equal("en", Loc.Language);
            Assert.True(changes >= 1);
        }
        finally
        {
            Loc.LanguageChanged -= OnChange;
            Loc.Use(before);
        }
    }

    internal static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ScreenShare.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("ScreenShare.slnx");
    }
}

[Collection("estado global")]
public sealed class WhatsNewTests
{
    [Fact]
    public void Lee_las_novedades_incrustadas_en_los_dos_idiomas()
    {
        var es = WhatsNew.Load("es");
        var en = WhatsNew.Load("en");
        Assert.NotEmpty(es);
        Assert.Equal(es.Count, en.Count);
        Assert.True(WhatsNew.SameVersion(es[0].Version, WhatsNew.CurrentVersion()), "whatsnew.json debe empezar por la versión actual");
        Assert.NotEqual(es[0].Items[0], en[0].Items[0]);
    }

    [Fact]
    public void Como_mucho_cinco_y_lo_ilegible_no_rompe()
    {
        var old = AppLog.Folder;
        AppLog.Folder = Directory.CreateTempSubdirectory("scs-log").FullName;   // el registro de verdad no se toca
        try
        {
        var json = "[" + string.Join(",", Enumerable.Range(1, 7).Select(i => $$"""{"version":"1.0.0.{{i}}","date":"2026-01-0{{i}}","en":["x{{i}}"]}""")) + "]";
        var list = WhatsNew.Load("es", new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(5, list.Count);
        Assert.Equal("x1", list[0].Items[0]);   // sin español, el inglés
        Assert.Equal(new DateTime(2026, 1, 1), list[0].Date);
        Assert.Empty(WhatsNew.Load("es", new MemoryStream("no"u8.ToArray())));
        }
        finally
        {
            try { Directory.Delete(AppLog.Folder, true); } catch { }
            AppLog.Folder = old;
        }
    }

    [Fact]
    public void Que_sale_solo_al_arrancar()
    {
        Assert.Equal((true, false), WhatsNew.OnStartup(new RecorderSettings(), "2026.10.12.0"));
        Assert.Equal((false, true), WhatsNew.OnStartup(new RecorderSettings { GuideShown = true, LastSeenVersion = "2026.10.1.0" }, "2026.10.12.0"));
        Assert.Equal((false, false), WhatsNew.OnStartup(new RecorderSettings { GuideShown = true, LastSeenVersion = "2026.10.12.00" }, "2026.10.12.0"));
        Assert.True(WhatsNew.SameVersion("abc", "abc"));
        Assert.False(WhatsNew.SameVersion(null, "1.0"));
    }
}
