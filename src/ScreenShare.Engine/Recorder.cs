using SocScreenShare.Audio;
using SocScreenShare.Capture;
using SocScreenShare.Recording;
using SocScreenShare.Settings;
using Windows.Graphics.Capture;

namespace SocScreenShare.Engine;

/// <summary>Qué se graba y dónde.</summary>
public sealed record RecordingRequest(
    TargetKind Target,
    MonitorInfo? Monitor,
    IReadOnlyList<MonitorInfo> Monitors,
    nint Window,
    QualityOptions Quality,
    bool Cursor,
    AudioPlan Audio,
    string OutputPath);

/// <summary>Cómo ha ido una grabación.</summary>
public sealed record RecordingResult(
    string Path,
    TimeSpan Duration,
    long Bytes,
    long VideoFrames,
    long DroppedFrames,
    bool HasAudio,
    bool HardwareEncoder,
    bool GpuConversion,
    EncodingPlan Plan,
    string? ErrorKey);

/// <summary>Avisos durante la grabación, con su clave de <c>Loc</c> y el dato que la completa.</summary>
public sealed record RecorderNotice(string Key, string? Argument = null);

/// <summary>Un fallo previsible al empezar, ya con su texto localizado en <see cref="Key"/>.</summary>
public sealed class RecorderException(string key, string? argument = null, Exception? inner = null)
    : Exception(key, inner)
{
    public string Key { get; } = key;
    public string? Argument { get; } = argument;
}

/// <summary>
/// Una grabación en marcha: captura, conversión y codificación en un hilo a ritmo fijo, mezcla de
/// audio en otro, los dos colgados del mismo reloj (con pausas). <see cref="StartAsync"/> la arranca y
/// <see cref="StopAsync"/> la cierra dejando un MP4 válido incluso si algo falló por el camino.
/// </summary>
public sealed class Recorder : IAsyncDisposable
{
    private const int MaxTexture = 16384;

    private readonly RecordingRequest _request;
    private readonly RecordingClock _clock = new();
    private AudioMixer _mixer = new();
    private readonly List<AudioSource> _sources = [];
    private GraphicsDevice? _gd;
    private ScreenCanvas? _canvas;
    private FrameConverter? _converter;
    private Mp4Writer? _writer;
    private Thread? _videoThread, _audioThread;
    private volatile bool _stopVideo, _stopAudio;
    private long _videoEndTicks;
    private long _frames, _dropped;
    private Exception? _failure;
    private MixerInput? _systemInput, _micInput;
    private AudioSource? _systemSource, _micSource;
    private int _stopped;

    private Recorder(RecordingRequest request) => _request = request;

    public EncodingPlan Plan { get; private set; } = new(2, 2, 30, 1, 1);
    public TimeSpan Elapsed => _clock.Elapsed;
    public bool IsPaused => _clock.IsPaused;
    public bool HasAudio => _writer?.HasAudio == true;
    public bool HardwareEncoder => _writer?.IsHardware == true;

    /// <summary>Pico reciente del sonido del equipo y del micrófono (0-1), para los medidores.</summary>
    public float SystemPeak => _systemSource?.Pipeline.LastPeak ?? 0;
    public float MicPeak => _micSource?.Pipeline.LastPeak ?? 0;

    /// <summary>Avisos (un origen de audio que no arranca o se corta, la ventana que se cierra…).</summary>
    public event Action<RecorderNotice>? Notice;

    /// <summary>Lo que no se pudo poner en marcha al empezar (para avisar después).</summary>
    public IReadOnlyList<RecorderNotice> StartNotices => _startNotices;
    private readonly List<RecorderNotice> _startNotices = [];

    public void SetSystemGain(float gain) { if (_systemInput is not null) _systemInput.Gain = gain; }
    public void SetMicGain(float gain) { if (_micInput is not null) _micInput.Gain = gain; }

    public void Pause() => _clock.Pause();
    public void Resume() => _clock.Resume();

    public static Task<Recorder> StartAsync(RecordingRequest request) => Task.Run(async () =>
    {
        var r = new Recorder(request);
        try
        {
            await r.SetUpAsync().ConfigureAwait(false);
            return r;
        }
        catch
        {
            await r.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    });

    private async Task SetUpAsync()
    {
        if (!Devices.CaptureSupported)
            throw new RecorderException("ErrCaptureUnsupported");
        var folder = Path.GetDirectoryName(_request.OutputPath);
        if (RecorderSettings.CheckFolder(folder) is { } folderError)
            throw new RecorderException(folderError, folder);

        // 1. Qué se captura y de qué tamaño es el lienzo.
        var (parts, canvasW, canvasH, sourceW, sourceH) = BuildParts();
        if (canvasW > MaxTexture || canvasH > MaxTexture)
            throw new RecorderException("ErrTooLarge");
        var quality = _request.Quality.Clone().Normalize();
        if (quality.Codec == VideoCodec.Hevc && !EncoderCatalog.HevcAvailable)
        {
            _startNotices.Add(new RecorderNotice("NoticeHevcUnavailable"));
            quality.Codec = VideoCodec.H264;
        }
        Plan = Presets.For(quality, sourceW, sourceH);
        _mixer = new AudioMixer(Plan.AudioSampleRate);

        // 2. Tarjeta gráfica, lienzo y conversión.
        _gd = new GraphicsDevice();
        _canvas = new ScreenCanvas(_gd, parts, canvasW, canvasH, _request.Target == TargetKind.Window, _request.Cursor);
        _canvas.SourceClosed += () => Notice?.Invoke(new RecorderNotice(_request.Target == TargetKind.Window ? "NoticeWindowClosed" : "NoticeMonitorGone"));
        _converter = new FrameConverter(_gd, _canvas, Plan.Width, Plan.Height, Plan.Fps);

        // 3. Audio: cada origen por separado; el que no arranca se avisa y la grabación sigue sin él.
        var audio = _request.Audio;
        if (audio.SystemMode == SystemAudioMode.All)
        {
            _systemInput = _mixer.Add("system");
            _systemInput.Gain = audio.SystemGain;
            _systemSource = await TryAudio("system", AudioKind.Output, audio.OutputDeviceId, 0, _systemInput, "NoticeSystemAudioFailed").ConfigureAwait(false);
        }
        else if (audio.SystemMode == SystemAudioMode.App)
        {
            _systemInput = _mixer.Add("app");
            _systemInput.Gain = audio.SystemGain;
            if (!AudioCatalog.AppCaptureSupported)
                _startNotices.Add(new RecorderNotice("NoticeAppAudioUnsupported"));
            else if (audio.AppProcessId is not { } pid)
                _startNotices.Add(new RecorderNotice("NoticeAppNotRunning"));
            else
                _systemSource = await TryAudio("app", AudioKind.App, null, pid, _systemInput, "NoticeAppAudioFailed").ConfigureAwait(false);
        }
        if (audio.Microphone)
        {
            _micInput = _mixer.Add("mic");
            _micInput.Gain = audio.MicGain;
            _micSource = await TryAudio("mic", AudioKind.Microphone, audio.MicDeviceId, 0, _micInput, "NoticeMicFailed").ConfigureAwait(false);
        }

        // 4. El fichero (con pista de audio solo si algún origen funciona). Si HEVC no arranca, H.264.
        try
        {
            try
            {
                _writer = new Mp4Writer(_request.OutputPath, Plan, withAudio: _sources.Count > 0);
            }
            catch (InvalidOperationException) when (Plan.Codec == VideoCodec.Hevc)
            {
                quality.Codec = VideoCodec.H264;
                Plan = Presets.For(quality, sourceW, sourceH);
                _startNotices.Add(new RecorderNotice("NoticeHevcFailed"));
                _writer = new Mp4Writer(_request.OutputPath, Plan, withAudio: _sources.Count > 0);
            }
        }
        catch (Exception ex)
        {
            throw new RecorderException(ex is UnauthorizedAccessException or IOException ? "ErrFolderNotWritable" : "ErrNoEncoder", folder, ex);
        }
        if (!_writer.RateControlApplied)
            _startNotices.Add(new RecorderNotice("NoticeRateControlIgnored"));

        // 5. En marcha: primero la imagen (se espera la primera un momento para no empezar en negro).
        _canvas.Start();
        _canvas.FirstFrame.Wait(TimeSpan.FromSeconds(1));
        foreach (var s in _sources)
        {
            try { s.Start(); }
            catch (Exception ex)
            {
                AppLog.Write($"audio «{s.Name}» no arranca: {ex}");
                _startNotices.Add(new RecorderNotice(s == _micSource ? "NoticeMicFailed" : "NoticeSystemAudioFailed"));
            }
        }
        _clock.Start();
        _videoThread = new Thread(VideoLoop) { IsBackground = true, Name = "sOCScreenShare vídeo", Priority = ThreadPriority.AboveNormal };
        _audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "sOCScreenShare audio", Priority = ThreadPriority.AboveNormal };
        _videoThread.Start();
        _audioThread.Start();
    }

    private (List<CapturePart> Parts, int CanvasW, int CanvasH, int SourceW, int SourceH) BuildParts()
    {
        switch (_request.Target)
        {
            case TargetKind.Window:
                if (!Devices.IsWindowAlive(_request.Window))
                    throw new RecorderException("ErrWindowGone");
                if (Native.IsIconic(_request.Window))
                    throw new RecorderException("ErrWindowMinimized");
                GraphicsCaptureItem item;
                try { item = GraphicsDevice.ItemForWindow(_request.Window); }
                catch (Exception ex) { throw new RecorderException("ErrWindowNotCapturable", null, ex); }
                var size = item.Size;
                if (size.Width < 2 || size.Height < 2)
                    throw new RecorderException("ErrWindowMinimized");
                // El lienzo deja sitio para que la ventana crezca (hasta el escritorio entero).
                var desk = MonitorLayout.VirtualBounds(_request.Monitors);
                var cw = Math.Min(MaxTexture, Math.Max(size.Width, desk.Width)) & ~1;
                var ch = Math.Min(MaxTexture, Math.Max(size.Height, desk.Height)) & ~1;
                return ([new CapturePart(item, 0, 0)], cw, ch, size.Width, size.Height);

            case TargetKind.AllMonitors:
                if (_request.Monitors.Count == 0)
                    throw new RecorderException("ErrNoMonitor");
                var all = MonitorLayout.VirtualBounds(_request.Monitors);
                var parts = new List<CapturePart>();
                foreach (var m in _request.Monitors)
                {
                    var (x, y) = MonitorLayout.OffsetInCanvas(m, all);
                    parts.Add(new CapturePart(GraphicsDevice.ItemForMonitor(m.Handle), x, y));
                }
                return (parts, all.Width & ~1, all.Height & ~1, all.Width, all.Height);

            default:
                var monitor = _request.Monitor ?? throw new RecorderException("ErrNoMonitor");
                var mi = GraphicsDevice.ItemForMonitor(monitor.Handle);
                return ([new CapturePart(mi, 0, 0)], mi.Size.Width & ~1, mi.Size.Height & ~1, mi.Size.Width, mi.Size.Height);
        }
    }

    private async Task<AudioSource?> TryAudio(string name, AudioKind kind, string? deviceId, int pid, MixerInput input, string noticeKey)
    {
        try
        {
            var source = await AudioSource.CreateAsync(name, kind, deviceId, pid, _clock, _mixer, input).ConfigureAwait(false);
            source.Stopped += _ => Notice?.Invoke(new RecorderNotice(noticeKey == "NoticeMicFailed" ? "NoticeMicLost" : "NoticeSystemAudioLost"));
            _sources.Add(source);
            return source;
        }
        catch (Exception ex)
        {
            AppLog.Write($"audio «{name}» no disponible: {ex}");
            _startNotices.Add(new RecorderNotice(noticeKey));
            return null;
        }
    }

    // ------------------------------------------------------------------ hilos

    private void VideoLoop()
    {
        Native.timeBeginPeriod(1);
        try
        {
            var fps = Plan.Fps;
            var buffer = new byte[_converter!.BufferSize];
            long index = 0;
            long FrameTime(long i) => i * 10_000_000 / fps;
            while (!_stopVideo)
            {
                if (_clock.IsPaused)
                {
                    Thread.Sleep(10);
                    continue;
                }
                var elapsed = _clock.ElapsedTicks;
                var due = elapsed * fps / 10_000_000;
                if (index > due)
                {
                    var waitMs = (FrameTime(index) - elapsed) / 10_000;
                    Thread.Sleep((int)Math.Clamp(waitMs, 1, 50));
                    continue;
                }
                if (due > index)
                {
                    // El equipo no da abasto: se salta a la imagen que toca (el sello de tiempo sigue siendo el bueno).
                    Interlocked.Add(ref _dropped, due - index);
                    index = due;
                }
                _converter.Convert(buffer);
                _writer!.WriteVideo(buffer, FrameTime(index), FrameTime(index + 1) - FrameTime(index));
                Interlocked.Increment(ref _frames);
                index++;
            }
            Interlocked.Exchange(ref _videoEndTicks, FrameTime(index));
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
    }

    private void AudioLoop()
    {
        if (_writer?.HasAudio != true)
            return;
        try
        {
            var chunk = _mixer.SampleRate / 10;   // 100 ms
            var channels = Plan.AudioChannels;
            var mix = new float[chunk * 2];
            var mono = new float[chunk];
            var pcm = new byte[chunk * 2 * channels];
            while (true)
            {
                var final = _stopAudio;
                var elapsed = final ? Interlocked.Read(ref _videoEndTicks) : _clock.ElapsedTicks;
                var due = _mixer.FramesDue(_mixer.FrameAt(elapsed), final);
                while (due > 0)
                {
                    var n = (int)Math.Min(due, chunk);
                    var start = _mixer.WrittenFrames;
                    _mixer.Mix(mix.AsSpan(0, n * 2));
                    if (channels == 1)
                    {
                        PcmConverter.DownmixToMono(mix.AsSpan(0, n * 2), mono);
                        PcmConverter.ToPcm16(mono.AsSpan(0, n), pcm.AsSpan(0, n * 2));
                    }
                    else
                    {
                        PcmConverter.ToPcm16(mix.AsSpan(0, n * 2), pcm.AsSpan(0, n * 4));
                    }
                    _writer.WriteAudio(pcm.AsSpan(0, n * 2 * channels), _mixer.TicksAt(start), _mixer.TicksAt(start + n) - _mixer.TicksAt(start));
                    due -= n;
                }
                if (final)
                    break;
                Thread.Sleep(20);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void Fail(Exception ex)
    {
        AppLog.Write($"la grabación ha fallado: {ex}");
        if (Interlocked.CompareExchange(ref _failure, ex, null) is null)
            Notice?.Invoke(new RecorderNotice(IsDiskFull(ex) ? "NoticeDiskFull" : "NoticeRecordingFailed"));
    }

    private static bool IsDiskFull(Exception ex) => ex.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027);

    // ------------------------------------------------------------------ parar

    /// <summary>Para y cierra el fichero. Se puede llamar desde cualquier hilo y solo hace efecto una vez.</summary>
    public Task<RecordingResult> StopAsync() => Task.Run(() =>
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
            throw new InvalidOperationException("La grabación ya está parada.");
        var duration = _clock.Elapsed;
        _stopVideo = true;
        _videoThread?.Join();
        if (_clock.IsPaused)
            _clock.Resume();
        // Un momento para que lleguen los últimos paquetes de audio de antes de parar.
        if (_writer?.HasAudio == true)
            Thread.Sleep(_mixer.LatencyFrames * 1000 / _mixer.SampleRate + 50);
        _stopAudio = true;
        _audioThread?.Join();
        foreach (var s in _sources)
            s.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        _sources.Clear();
        _canvas?.Dispose();
        _canvas = null;

        string? error = _failure is null ? null : IsDiskFull(_failure) ? "NoticeDiskFull" : "NoticeRecordingFailed";
        try
        {
            if (_frames > 0)
                _writer?.Finish();
            else
                error ??= "ErrNothingRecorded";
        }
        catch (Exception ex)
        {
            AppLog.Write($"no se pudo cerrar el fichero: {ex}");
            error ??= IsDiskFull(ex) ? "NoticeDiskFull" : "ErrFinalize";
        }
        var bytes = File.Exists(_request.OutputPath) ? new FileInfo(_request.OutputPath).Length : 0;
        var videoEnd = TimeSpan.FromTicks(Interlocked.Read(ref _videoEndTicks));
        return new RecordingResult(_request.OutputPath, videoEnd > TimeSpan.Zero ? videoEnd : duration, bytes,
            Interlocked.Read(ref _frames), Interlocked.Read(ref _dropped), _writer?.HasAudio == true,
            _writer?.IsHardware == true, _converter?.UsesGpu == true, Plan, error);
    });

    public async ValueTask DisposeAsync()
    {
        _stopVideo = true;
        _stopAudio = true;
        _videoThread?.Join(TimeSpan.FromSeconds(5));
        _audioThread?.Join(TimeSpan.FromSeconds(5));
        foreach (var s in _sources)
            await s.DisposeAsync().ConfigureAwait(false);
        _sources.Clear();
        _canvas?.Dispose();
        _converter?.Dispose();
        _writer?.Dispose();
        _gd?.Dispose();
        _canvas = null;
        _converter = null;
        _writer = null;
        _gd = null;
    }
}
