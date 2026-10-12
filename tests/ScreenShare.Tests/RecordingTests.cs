using SocScreenShare.Recording;

namespace SocScreenShare.Tests;

public sealed class PresetTests
{
    [Fact]
    public void Compartir_a_1080p_queda_igual_30_fps_y_bitrate_moderado()
    {
        var p = Presets.For(QualityPreset.Share, 1920, 1080);
        Assert.Equal((1920, 1080, 30, VideoCodec.H264), (p.Width, p.Height, p.Fps, p.Codec));
        Assert.Equal(4_043_520, p.VideoBitrate);   // 0,065 bits por píxel
        Assert.Equal((128_000, 16_000, 48000, 2), (p.AudioBitrate, p.AudioBytesPerSecond, p.AudioSampleRate, p.AudioChannels));
    }

    [Fact]
    public void Compartir_baja_un_4K_a_1080p_sin_deformar()
    {
        var p = Presets.For(QualityPreset.Share, 3840, 2160);
        Assert.Equal((1920, 1080), (p.Width, p.Height));
    }

    [Fact]
    public void Una_pantalla_vertical_cabe_en_1080x1920()
    {
        var p = Presets.For(QualityPreset.Share, 2160, 3840);
        Assert.Equal((1080, 1920), (p.Width, p.Height));
    }

    [Fact]
    public void Nunca_se_agranda_y_los_lados_son_pares()
    {
        var p = Presets.For(QualityPreset.Share, 801, 601);
        Assert.Equal((800, 600), (p.Width, p.Height));
        Assert.Equal(936_000, p.VideoBitrate);
    }

    [Fact]
    public void Una_ventana_diminuta_sale_con_el_minimo_que_admiten_los_codificadores()
    {
        var p = Presets.For(QualityPreset.Share, 160, 28);
        Assert.Equal((160, Presets.MinSide), (p.Width, p.Height));
        Assert.Equal(Presets.MinAutoBitrate, p.VideoBitrate);
    }

    [Fact]
    public void Alta_calidad_a_60_fps_hasta_4K()
    {
        var p = Presets.For(QualityPreset.High, 7680, 4320);
        Assert.Equal((3840, 2160, 60), (p.Width, p.Height, p.Fps));
        Assert.Equal(24_261_120, p.VideoBitrate);   // 0,065 × 0,75 por pasar de 30 imágenes/s
        Assert.Equal(24_000, p.AudioBytesPerSecond);
    }

    [Fact]
    public void Escritorio_panoramico_cabe_en_la_caja()
    {
        Assert.Equal((1920, 540), Presets.Fit(3840, 1080, 1920, 1080));
    }

    [Fact]
    public void Sin_tamano_es_un_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Presets.Fit(0, 100, 1920, 1080));
    }

    [Fact]
    public void Megas_por_minuto_con_y_sin_audio()
    {
        var p = new EncodingPlan(1920, 1080, 30, 4_000_000, 128_000);
        Assert.Equal(4_000_000 / 8L * 60, p.BytesPerMinute(false));
        Assert.Equal(4_128_000 / 8L * 60, p.BytesPerMinute(true));
    }

    [Theory]
    [InlineData(1920, 1080, 30, VideoCodec.H264, 4_043_520)]
    [InlineData(1920, 1080, 30, VideoCodec.Hevc, 2_488_320)]
    [InlineData(1280, 720, 60, VideoCodec.H264, 2_695_680)]
    [InlineData(320, 240, 15, VideoCodec.H264, Presets.MinAutoBitrate)]
    [InlineData(7680, 4320, 60, VideoCodec.H264, Presets.MaxAutoBitrate)]
    public void Bitrate_automatico_segun_resolucion_imagenes_y_codec(int w, int h, int fps, VideoCodec codec, int expected) =>
        Assert.Equal(expected, Presets.AutoBitrate(w, h, fps, codec));

    [Theory]
    [InlineData(MaxResolution.P2160, 3840, 2160)]
    [InlineData(MaxResolution.P1440, 2560, 1440)]
    [InlineData(MaxResolution.P1080, 1920, 1080)]
    [InlineData(MaxResolution.P720, 1280, 720)]
    [InlineData(MaxResolution.P480, 854, 480)]
    [InlineData(MaxResolution.Original, 4096, 2304)]
    public void Cada_resolucion_maxima_es_su_caja(MaxResolution r, int w, int h)
    {
        Assert.Equal((w, h), QualityOptions.Box(r));
        var plan = Presets.For(new QualityOptions { Resolution = r }, 7680, 4320);
        Assert.True(plan.Width <= w && plan.Height <= h);
    }

    [Fact]
    public void Personalizada_a_mano_respeta_todo()
    {
        var q = new QualityOptions
        {
            Resolution = MaxResolution.P720, Fps = 24, AutoBitrate = false, VideoMbps = 2.5, Codec = VideoCodec.Hevc,
            RateControl = RateControl.Cbr, AudioKbps = 96, AudioSampleRate = 44100, AudioChannels = 1,
        };
        var p = Presets.For(q, 2560, 1440);
        Assert.Equal(new EncodingPlan(1280, 720, 24, 2_500_000, 96_000, VideoCodec.Hevc, RateControl.Cbr, 70, 44100, 1), p);
        Assert.Equal(12_000, p.AudioBytesPerSecond);
        // Una tasa a mano fuera de rango se recorta.
        q.VideoMbps = 500;
        Assert.Equal(100_000_000, Presets.For(q, 1280, 720).VideoBitrate);
    }
}

public sealed class QualityOptionsTests
{
    [Fact]
    public void Los_atajos_rellenan_valores_y_se_reconocen()
    {
        var share = QualityOptions.ForPreset(QualityPreset.Share);
        Assert.Equal((MaxResolution.P1080, 30, true, VideoCodec.H264, RateControl.Vbr, 128, 48000, 2),
            (share.Resolution, share.Fps, share.AutoBitrate, share.Codec, share.RateControl, share.AudioKbps, share.AudioSampleRate, share.AudioChannels));
        var high = QualityOptions.ForPreset(QualityPreset.High);
        Assert.Equal((MaxResolution.P2160, 60, 192), (high.Resolution, high.Fps, high.AudioKbps));
        Assert.Equal(QualityPreset.Share, share.Detect());
        Assert.Equal(QualityPreset.High, high.Detect());
        Assert.Equal(QualityPreset.Share, QualityOptions.ForPreset(QualityPreset.Custom).Detect());
        share.Fps = 24;
        Assert.Equal(QualityPreset.Custom, share.Detect());
    }

    [Fact]
    public void Lo_que_no_cuenta_no_cambia_el_atajo()
    {
        var q = QualityOptions.ForPreset(QualityPreset.Share);
        q.VideoMbps = 9;        // con tasa automática no se usa
        q.QualityLevel = 10;    // sin calidad constante no se usa
        Assert.Equal(QualityPreset.Share, q.Detect());
        q.AutoBitrate = false;
        Assert.Equal(QualityPreset.Custom, q.Detect());
        var c = QualityOptions.ForPreset(QualityPreset.Share);
        c.RateControl = RateControl.Quality;
        var d = c.Clone();
        Assert.True(c.SameAs(d));
        d.QualityLevel = 99;
        Assert.False(c.SameAs(d));
    }

    [Fact]
    public void Normalizar_lleva_al_valor_admitido_mas_cercano()
    {
        var q = new QualityOptions
        {
            Resolution = (MaxResolution)42, Fps = 50, VideoMbps = 0.01, Codec = (VideoCodec)9, RateControl = (RateControl)9,
            QualityLevel = 150, AudioKbps = 320, AudioSampleRate = 22050, AudioChannels = 6,
        }.Normalize();
        Assert.Equal((MaxResolution.P1080, 60, 0.2, VideoCodec.H264, RateControl.Vbr, 100, 192, 44100, 2),
            (q.Resolution, q.Fps, q.VideoMbps, q.Codec, q.RateControl, q.QualityLevel, q.AudioKbps, q.AudioSampleRate, q.AudioChannels));
        var n = new QualityOptions { Fps = 20, VideoMbps = double.NaN, AudioKbps = 140, AudioChannels = 0, QualityLevel = -5 }.Normalize();
        Assert.Equal((24, 4.0, 128, 1, 0), (n.Fps, n.VideoMbps, n.AudioKbps, n.AudioChannels, n.QualityLevel));
        Assert.Equal(96, new QualityOptions { AudioKbps = 112 }.Normalize().AudioKbps);   // a la misma distancia de 96 y 128: el menor
    }

    [Fact]
    public void AAC_de_Windows_solo_admite_cuatro_tasas()
    {
        Assert.All(QualityOptions.SupportedAudioKbps, k => Assert.True(QualityOptions.IsAacSupported(k)));
        Assert.False(QualityOptions.IsAacSupported(256));
        Assert.False(QualityOptions.IsAacSupported(320));
        Assert.Equal([96, 128, 160, 192, 256, 320], QualityOptions.AudioKbpsChoices);
    }

    [Fact]
    public void HEVC_solo_si_hay_codificador()
    {
        var q = new QualityOptions { Codec = VideoCodec.Hevc };
        Assert.Equal(VideoCodec.Hevc, q.EffectiveCodec(hevcAvailable: true));
        Assert.Equal(VideoCodec.H264, q.EffectiveCodec(hevcAvailable: false));
        Assert.Equal(VideoCodec.H264, new QualityOptions().EffectiveCodec(true));
    }
}

public sealed class FileNamerTests
{
    private static readonly DateTime When = new(2026, 10, 12, 15, 30, 5);

    [Fact]
    public void Nombre_con_fecha_y_hora_sin_dos_puntos()
    {
        Assert.Equal("Grabación 2026-10-12 15.30.05", FileNamer.BaseName("Grabación", When));
        Assert.DoesNotContain(':', FileNamer.BaseName("Recording", When));
    }

    [Fact]
    public void No_pisa_lo_que_ya_existe()
    {
        var taken = new HashSet<string> { @"C:\v\Rec 2026-10-12 15.30.05.mp4", @"C:\v\Rec 2026-10-12 15.30.05 (2).mp4" };
        Assert.Equal(@"C:\v\Rec 2026-10-12 15.30.05 (3).mp4", FileNamer.NewPath(@"C:\v", "Rec", When, taken.Contains));
    }

    [Fact]
    public void Con_el_disco_de_verdad_por_defecto()
    {
        var dir = Directory.CreateTempSubdirectory("scs-names").FullName;
        try
        {
            var first = FileNamer.NewPath(dir, "Rec", When);
            File.WriteAllText(first, "");
            Assert.EndsWith("(2).mp4", FileNamer.NewPath(dir, "Rec", When));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("a/b:c*?", "a_b_c__")]
    [InlineData("   ", "ScreenShare")]
    [InlineData("fin.", "fin")]
    public void Limpia_lo_que_Windows_no_admite(string input, string expected) =>
        Assert.Equal(expected, FileNamer.Sanitize(input));
}

public sealed class ClockTests
{
    private long _now = 1_000_000;

    private RecordingClock Clock() => new(() => _now);

    [Fact]
    public void Cuenta_el_tiempo_sin_las_pausas()
    {
        var c = Clock();
        Assert.Equal(0, c.ElapsedTicks);
        c.Start();
        _now += 10_000_000;
        c.Pause();
        Assert.True(c.IsPaused);
        _now += 50_000_000;
        Assert.Equal(TimeSpan.FromSeconds(1), c.Elapsed);
        c.Resume();
        _now += 20_000_000;
        Assert.Equal(TimeSpan.FromSeconds(3), c.Elapsed);
        Assert.False(c.IsPaused);
    }

    [Fact]
    public void Coloca_cada_instante_en_la_linea_de_tiempo()
    {
        var c = Clock();
        Assert.Null(c.TimelineAt(_now));   // sin empezar
        var start = _now;
        c.Start();
        Assert.True(c.IsStarted);
        Assert.Equal(start, c.StartTime);
        Assert.Null(c.TimelineAt(start - 1));
        Assert.Equal(5_000_000, c.TimelineAt(start + 5_000_000));
        _now = start + 10_000_000;
        c.Pause();
        Assert.Null(c.TimelineAt(start + 12_000_000));   // en la pausa en curso
        _now = start + 30_000_000;
        c.Resume();
        Assert.Null(c.TimelineAt(start + 20_000_000));   // en una pausa ya terminada
        Assert.Equal(15_000_000, c.TimelineAt(start + 35_000_000));
    }

    [Fact]
    public void Pausar_o_seguir_dos_veces_no_hace_nada_y_empezar_dos_veces_es_un_error()
    {
        var c = Clock();
        c.Pause();
        c.Resume();
        Assert.Equal(0, c.ElapsedTicks);
        c.Start();
        c.Pause();
        c.Pause();
        c.Resume();
        c.Resume();
        Assert.Throws<InvalidOperationException>(c.Start);
    }

    [Fact]
    public void Del_QPC_a_100ns_sin_desbordar()
    {
        Assert.Equal(10_000_000, RecordingClock.ToHns(3_000_000, 3_000_000));
        Assert.Equal(15_000_000, RecordingClock.ToHns(15_000_000, 10_000_000));
        // Un contador enorme (años de equipo encendido) no se desborda al multiplicar.
        var huge = long.MaxValue / 2;
        Assert.Equal(huge / 10_000_000 * 10_000_000 + huge % 10_000_000, RecordingClock.ToHns(huge, 10_000_000));
        Assert.InRange(RecordingClock.QpcNow(), 1, long.MaxValue);
        var real = new RecordingClock();
        real.Start();
        Assert.InRange(real.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.True(real.Now() > 0);
    }
}

public sealed class StateMachineTests
{
    [Fact]
    public void Recorrido_completo()
    {
        var m = new RecordingStateMachine();
        var seen = new List<RecordingState>();
        m.Changed += seen.Add;
        Assert.True(m.CanStart);
        Assert.True(m.TryStart());
        Assert.False(m.TryStart());   // un doble clic no empieza dos
        Assert.False(m.CanStop);      // arrancando no se para
        Assert.True(m.Started());
        Assert.True(m.CanPause && m.CanStop && m.IsBusy);
        Assert.True(m.TryPause());
        Assert.True(m.CanResume);
        Assert.True(m.TryResume());
        Assert.True(m.TryStop());
        Assert.False(m.TryStop());
        Assert.True(m.Stopped());
        Assert.Equal(
            [RecordingState.Starting, RecordingState.Recording, RecordingState.Paused, RecordingState.Recording, RecordingState.Stopping, RecordingState.Idle],
            seen);
    }

    [Fact]
    public void Parar_desde_la_pausa_y_fallar_desde_donde_sea()
    {
        var m = new RecordingStateMachine();
        m.TryStart();
        m.Started();
        m.TryPause();
        Assert.True(m.TryStop());
        m.Stopped();
        m.TryStart();
        m.Failed();
        Assert.Equal(RecordingState.Idle, m.State);
        m.Failed();   // ya en reposo: sin aviso
        Assert.False(m.Started());
        Assert.False(m.TryPause());
        Assert.False(m.TryResume());
        Assert.False(m.Stopped());
    }
}
