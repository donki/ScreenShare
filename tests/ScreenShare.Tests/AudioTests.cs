using System.Buffers.Binary;
using SocScreenShare.Audio;
using SocScreenShare.Recording;

namespace SocScreenShare.Tests;

public sealed class PcmConverterTests
{
    private static readonly PcmFormat Float48 = new(48000, 2, 32, true);

    [Fact]
    public void Coma_flotante_estereo_pasa_tal_cual()
    {
        var data = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), -0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(12), 0f);
        var outp = new float[4];
        Assert.Equal(2, PcmConverter.ToStereoFloat(data, Float48, outp));
        Assert.Equal([0.5f, -0.25f, 1f, 0f], outp);
    }

    [Fact]
    public void Mono_de_16_bits_se_duplica_en_los_dos_canales()
    {
        var data = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), -32768);
        var outp = new float[4];
        Assert.Equal(2, PcmConverter.ToStereoFloat(data, new PcmFormat(16000, 1, 16, false), outp));
        Assert.Equal([0.5f, 0.5f, -1f, -1f], outp);
    }

    [Fact]
    public void Veinticuatro_y_treinta_y_dos_bits_enteros()
    {
        var d24 = new byte[] { 0x00, 0x00, 0x40, 0x00, 0x00, 0xC0 };   // +0,5 y -0,5
        var outp = new float[2];
        PcmConverter.ToStereoFloat(d24, new PcmFormat(48000, 2, 24, false), outp);
        Assert.Equal(0.5f, outp[0], 3);
        Assert.Equal(-0.5f, outp[1], 3);
        var d32 = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(d32.AsSpan(0), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(d32.AsSpan(4), int.MinValue);
        PcmConverter.ToStereoFloat(d32, new PcmFormat(48000, 2, 32, false), outp);
        Assert.Equal(1f, outp[0], 3);
        Assert.Equal(-1f, outp[1], 3);
    }

    [Fact]
    public void Con_mas_de_dos_canales_quedan_los_delanteros()
    {
        var data = new byte[6 * 4];
        for (var c = 0; c < 6; c++)
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(c * 4), c / 10f);
        var outp = new float[2];
        Assert.Equal(1, PcmConverter.ToStereoFloat(data, new PcmFormat(48000, 6, 32, true), outp));
        Assert.Equal([0f, 0.1f], outp);
        Assert.Equal(24, new PcmFormat(48000, 6, 32, true).BytesPerFrame);
    }

    [Fact]
    public void No_escribe_mas_de_lo_que_cabe_y_rechaza_formatos_raros()
    {
        var outp = new float[2];
        Assert.Equal(1, PcmConverter.ToStereoFloat(new byte[32], Float48, outp));
        Assert.Throws<NotSupportedException>(() => PcmConverter.ToStereoFloat(new byte[4], new PcmFormat(48000, 2, 8, false), outp));
        Assert.False(new PcmFormat(0, 2, 16, false).IsSupported);
        Assert.False(new PcmFormat(48000, 2, 16, true).IsSupported);
        Assert.Equal(3, PcmConverter.FramesIn(12, new PcmFormat(48000, 2, 16, false)));
    }

    [Fact]
    public void Estereo_a_mono_es_la_media()
    {
        var mono = new float[3];
        Assert.Equal(2, PcmConverter.DownmixToMono([1f, 0f, -0.5f, -0.5f], mono));
        Assert.Equal([0.5f, -0.5f, 0f], mono);
        Assert.Equal(1, PcmConverter.DownmixToMono([1f, 1f, 1f, 1f], new float[1]));
    }

    [Fact]
    public void A_16_bits_recorta_lo_que_se_pasa()
    {
        var pcm = new byte[8];
        PcmConverter.ToPcm16([1f, -1f, 2f, 0f], pcm);
        Assert.Equal(32767, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(0)));
        Assert.Equal(-32768, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(2)));
        Assert.Equal(32767, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(6)));
        Assert.Throws<ArgumentException>(() => PcmConverter.ToPcm16([1f, 1f], new byte[2]));
    }
}

public sealed class ResamplerTests
{
    private static float[] Ramp(int frames, int start = 0)
    {
        var a = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            a[i * 2] = a[i * 2 + 1] = start + i;
        return a;
    }

    [Fact]
    public void A_la_misma_frecuencia_no_toca_nada()
    {
        var r = new LinearResampler(48000, 48000);
        Assert.True(r.IsPassThrough);
        var outp = new float[20];
        Assert.Equal(10, r.Process(Ramp(10), outp));
        Assert.Equal(Ramp(10), outp);
    }

    [Fact]
    public void De_24_a_48_kHz_intercala_valores_y_no_corta_entre_paquetes()
    {
        var r = new LinearResampler(24000, 48000);
        var all = new List<float>();
        var buf = new float[r.MaxOutputFrames(4) * 2];
        var n = r.Process(Ramp(4, 0), buf);
        all.AddRange(buf.Take(n * 2).Where((_, i) => i % 2 == 0));
        n = r.Process(Ramp(4, 4), buf);
        all.AddRange(buf.Take(n * 2).Where((_, i) => i % 2 == 0));
        // Rampa 0..7 a doble frecuencia: 0, 0.5, 1, … sin saltos en la frontera de paquetes.
        for (var i = 0; i < all.Count; i++)
            Assert.Equal(i * 0.5f, all[i], 3);
        Assert.InRange(all.Count, 13, 15);
    }

    [Fact]
    public void De_44_1_a_48_kHz_da_el_numero_de_tramas_esperado()
    {
        var r = new LinearResampler(44100, 48000);
        long total = 0;
        var buf = new float[r.MaxOutputFrames(441) * 2];
        for (var p = 0; p < 100; p++)
            total += r.Process(Ramp(441), buf);
        Assert.InRange(total, 47_990, 48_010);   // 1 s de entrada → 1 s de salida
    }

    [Fact]
    public void Paquete_vacio_reinicio_y_frecuencias_no_validas()
    {
        var r = new LinearResampler(16000, 48000);
        Assert.Equal(0, r.Process([], new float[4]));
        r.Process(Ramp(2), new float[64]);
        r.Reset();
        var buf = new float[64];
        r.Process(Ramp(2, 10), buf);
        Assert.Equal(10f, buf[0]);   // tras reiniciar empieza en el primer valor del paquete
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinearResampler(0, 48000));
    }
}

public sealed class TimelineTests
{
    [Fact]
    public void Lo_escrito_se_lee_en_su_sitio_y_lo_que_falta_es_silencio()
    {
        var b = new TimelineAudioBuffer(100);
        b.Write(2, [1f, 1f, 2f, 2f]);
        var mix = new float[10];
        b.ReadAdd(mix, 1f);
        Assert.Equal([0f, 0f, 0f, 0f, 1f, 1f, 2f, 2f, 0f, 0f], mix);
        Assert.Equal(5, b.ReadPosition);
    }

    [Fact]
    public void Lo_que_llega_tarde_o_demasiado_pronto_se_descarta()
    {
        var b = new TimelineAudioBuffer(4);
        b.ReadAdd(new float[4], 1f);   // lee 2 tramas
        b.Write(0, [9f, 9f]);           // tarde
        b.Write(10, [9f, 9f]);          // más allá de la capacidad
        Assert.Equal(2, b.DroppedFrames);
        var mix = new float[8];
        b.ReadAdd(mix, 1f);
        Assert.All(mix, v => Assert.Equal(0f, v));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineAudioBuffer(0));
    }

    [Fact]
    public void El_anillo_da_la_vuelta_y_lo_leido_se_borra()
    {
        var b = new TimelineAudioBuffer(3);
        for (var round = 0; round < 5; round++)
        {
            b.Write(round * 2, [round, round, round, round]);
            var mix = new float[4];
            b.ReadAdd(mix, 0.5f);
            Assert.All(mix, v => Assert.Equal(round * 0.5f, v));
        }
    }

    [Fact]
    public void Paquetes_seguidos_van_pegados_y_un_hueco_se_recoloca()
    {
        var p = new SourcePlacement(10);
        Assert.Equal(1000, p.Place(1000, 480));
        Assert.Equal(1480, p.Place(1485, 480));   // imprecisión pequeña: pegado
        Assert.Equal(1960, p.Place(1950, 480));
        Assert.Equal(5000, p.Place(5000, 480));   // tras un silencio: a su hora
        p.Reset();
        Assert.Equal(5470, p.Place(5470, 10));
        Assert.Equal(10, p.Tolerance);
    }
}

public sealed class MixerTests
{
    [Fact]
    public void Mezcla_con_el_volumen_de_cada_uno_y_recorta()
    {
        var m = new AudioMixer(latencyFrames: 0);
        Assert.Equal(48000, m.SampleRate);
        var a = m.Add("a", 16);
        var b = m.Add("b", 16);
        a.Buffer.Write(0, [0.5f, 0.5f, 0.9f, 0.9f]);
        b.Buffer.Write(0, [0.25f, 0.25f, 0.9f, 0.9f]);
        b.Gain = 2f;
        var mix = new float[4];
        m.Mix(mix);
        Assert.Equal([1f, 1f, 1f, 1f], mix);   // 0,5 + 0,5 y 0,9 + 1,8 recortado
        Assert.Equal(2, m.WrittenFrames);
        Assert.Equal(2, m.Inputs.Count);
    }

    [Fact]
    public void Un_origen_desactivado_no_suena_pero_avanza()
    {
        var m = new AudioMixer(latencyFrames: 0);
        var a = m.Add("a", 8);
        a.Buffer.Write(0, [0.5f, 0.5f]);
        a.Enabled = false;
        var mix = new float[2];
        m.Mix(mix);
        Assert.Equal([0f, 0f], mix);
        Assert.Equal(1, a.Buffer.ReadPosition);
        a.Gain = 5f;
        Assert.Equal(2f, a.Gain);
        a.Gain = -1f;
        Assert.Equal(0f, a.Gain);
        Assert.Equal("a", a.Name);
    }

    [Fact]
    public void Entrega_al_ritmo_del_reloj_con_su_margen()
    {
        var m = new AudioMixer();   // 150 ms
        Assert.Equal(7200, m.LatencyFrames);
        Assert.Equal(0, m.FramesDue(5000));
        Assert.Equal(48000 - 7200, m.FramesDue(48000));
        Assert.Equal(48000, m.FramesDue(48000, final: true));
        Assert.Equal(48000, m.FrameAt(10_000_000));
        Assert.Equal(10_000_000, m.TicksAt(48000));
        var cd = new AudioMixer(44100);
        Assert.Equal(6615, cd.LatencyFrames);
        Assert.Equal(44100, cd.FrameAt(10_000_000));
        Assert.Equal(176_400, cd.Add("x").Buffer.ReadPosition + 176_400);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioMixer(0));
    }
}

public sealed class PipelineTests
{
    private long _now = 100_000_000;

    [Fact]
    public void Cada_paquete_va_a_su_hora_y_lo_de_la_pausa_no_se_graba()
    {
        var clock = new RecordingClock(() => _now);
        var mixer = new AudioMixer(latencyFrames: 0);
        var input = mixer.Add("sys");
        var p = new AudioSourcePipeline(new PcmFormat(48000, 2, 32, true), clock, mixer, input);
        var packet = new byte[480 * 8];
        for (var i = 0; i < 480 * 2; i++)
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(i * 4), 0.25f);

        p.OnPacket(packet, false, _now - 10);   // antes de empezar
        Assert.Equal(1, p.Packets);
        Assert.Equal(0f, p.LastPeak);

        var start = _now;
        clock.Start();
        p.OnPacket(packet, false, start + 10_000_000);   // a 1 s
        Assert.Equal(0.25f, p.LastPeak);
        var mix = new float[48480 * 2];
        mixer.Mix(mix);
        Assert.Equal(0f, mix[47999 * 2]);
        Assert.Equal(0.25f, mix[48000 * 2]);
        Assert.Equal(0.25f, mix[48479 * 2 + 1]);

        _now = start + 20_000_000;
        clock.Pause();
        p.OnPacket(packet, false, start + 25_000_000);   // en pausa
        Assert.Equal(0f, p.LastPeak);
        Assert.Equal(new PcmFormat(48000, 2, 32, true), p.Format);
    }

    [Fact]
    public void Silencio_marcado_paquete_vacio_y_sin_hora()
    {
        var clock = new RecordingClock(() => _now);
        var mixer = new AudioMixer(latencyFrames: 0);
        var p = new AudioSourcePipeline(new PcmFormat(44100, 1, 16, false), clock, mixer, mixer.Add("mic"));
        clock.Start();
        _now += 10_000_000;
        var packet = new byte[441 * 2];
        packet.AsSpan().Fill(0x40);
        p.OnPacket(packet, true, _now);
        Assert.Equal(0f, p.LastPeak);
        p.OnPacket([], false, _now);
        p.OnPacket(packet, false, 0);   // sin hora: se toma la de ahora
        Assert.True(p.LastPeak > 0);
        Assert.Throws<NotSupportedException>(() => new AudioSourcePipeline(new PcmFormat(48000, 2, 8, false), clock, mixer, mixer.Add("x")));
    }

    [Fact]
    public void A_44_1_kHz_la_mezcla_va_a_su_frecuencia()
    {
        var clock = new RecordingClock(() => _now);
        var mixer = new AudioMixer(44100, 0);
        var p = new AudioSourcePipeline(new PcmFormat(48000, 2, 32, true), clock, mixer, mixer.Add("sys"));
        clock.Start();
        var start = _now;
        var packet = new byte[4800 * 8];
        p.OnPacket(packet, false, start + 10_000_000);
        Assert.InRange(mixer.Inputs[0].Buffer.ReadPosition, 0, 0);
        var mix = new float[44100 * 2 + 200];
        mixer.Mix(mix);   // la trama 44100 es el segundo 1 a 44,1 kHz
        Assert.Equal(44100 + 100, mixer.WrittenFrames);
    }
}

public sealed class ToneDetectorTests
{
    [Fact]
    public void Encuentra_el_tono_que_hay_y_no_el_que_no()
    {
        const int rate = 48000;
        var s = new float[rate];
        for (var i = 0; i < s.Length; i++)
            s[i] = 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
        Assert.Equal(0.3, ToneDetector.Amplitude(s, rate, 440), 2);
        Assert.True(ToneDetector.Amplitude(s, rate, 1000) < 0.001);
        Assert.Equal(0, ToneDetector.Amplitude([], rate, 440));
        Assert.Equal([1f, 0f], ToneDetector.ToMono([1f, 1f, 1f, -1f]));
    }
}
