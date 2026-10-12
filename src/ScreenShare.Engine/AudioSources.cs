using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SocScreenShare.Audio;
using SocScreenShare.Recording;
using SocScreenShare.Settings;

namespace SocScreenShare.Engine;

/// <summary>Un dispositivo de audio de Windows para los desplegables.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault)
{
    public override string ToString() => Name;
}

/// <summary>Una aplicación que se puede grabar por separado (por su nombre de proceso).</summary>
public sealed record AudioApp(string ProcessName, string Title, int ProcessId);

/// <summary>Listas de dispositivos y aplicaciones, y si Windows sabe grabar una aplicación sola.</summary>
public static class AudioCatalog
{
    /// <summary>La captura del sonido de una sola aplicación llegó con Windows 10 20348 / Windows 11.</summary>
    public static bool AppCaptureSupported => Environment.OSVersion.Version.Build >= 20348;

    public static IReadOnlyList<AudioDevice> Outputs() => List(DataFlow.Render);

    public static IReadOnlyList<AudioDevice> Inputs() => List(DataFlow.Capture);

    private static IReadOnlyList<AudioDevice> List(DataFlow flow)
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            string? def = null;
            try
            {
                using var d = e.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                def = d.ID;
            }
            catch (Exception) { }
            var list = new List<AudioDevice>();
            foreach (var dev in e.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (dev)
                    list.Add(new AudioDevice(dev.ID, dev.FriendlyName, dev.ID == def));
            }
            return [.. list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex)
        {
            AppLog.Write($"dispositivos de audio ({flow}): {ex.Message}");
            return [];
        }
    }

    /// <summary>Aplicaciones con ventana, una por nombre de proceso.</summary>
    public static IReadOnlyList<AudioApp> Apps()
    {
        var own = Environment.ProcessId;
        return [.. Devices.Windows()
            .Where(w => w.ProcessName.Length > 0)
            .GroupBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AudioApp(g.Key, g.First().Title, g.First().ProcessId))
            .OrderBy(a => a.ProcessName, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// El proceso cuyo árbol se graba para una aplicación: el que tiene ventana (en los navegadores
    /// es el principal, del que cuelgan los procesos que suenan).
    /// </summary>
    public static int? ResolveProcess(string processName)
    {
        var withWindow = Devices.Windows().FirstOrDefault(w => string.Equals(w.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
        if (withWindow is not null)
            return withWindow.ProcessId;
        var any = Process.GetProcessesByName(processName);
        try { return any.Length > 0 ? any[0].Id : null; }
        finally { foreach (var p in any) p.Dispose(); }
    }
}

/// <summary>Qué hay que capturar de audio en una grabación.</summary>
public sealed record AudioPlan(
    SystemAudioMode SystemMode,
    string? OutputDeviceId,
    int? AppProcessId,
    float SystemGain,
    bool Microphone,
    string? MicDeviceId,
    float MicGain)
{
    public bool HasAny => SystemMode != SystemAudioMode.None || Microphone;
}

/// <summary>
/// Una captura WASAPI (salida de un dispositivo, una aplicación o un micrófono) conectada a su
/// tubería de la mezcla. Si el dispositivo desaparece a media grabación, ese origen se queda en
/// silencio y la grabación sigue.
/// </summary>
internal sealed class AudioSource : IAsyncDisposable
{
    private readonly WasapiRecorder _recorder;
    private readonly AudioSourcePipeline _pipeline;

    private AudioSource(string name, WasapiRecorder recorder, AudioSourcePipeline pipeline)
    {
        Name = name;
        _recorder = recorder;
        _pipeline = pipeline;
        _recorder.DataAvailable += OnData;
        _recorder.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                AppLog.Write($"audio «{Name}» se ha cortado: {e.Exception.Message}");
                Stopped?.Invoke(this);
            }
        };
    }

    public string Name { get; }
    public AudioSourcePipeline Pipeline => _pipeline;
    public event Action<AudioSource>? Stopped;

    public static async Task<AudioSource> CreateAsync(string name, AudioKind kind, string? deviceId, int processId, RecordingClock clock, AudioMixer mixer, MixerInput input)
    {
        var builder = new WasapiRecorderBuilder().WithSharedMode();
        MMDevice? device = null;
        switch (kind)
        {
            case AudioKind.Output:
                device = Find(DataFlow.Render, deviceId);
                builder = builder.WithDevice(device).WithLoopbackCapture();
                break;
            case AudioKind.App:
                builder = builder.WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree)
                    .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.DefaultSampleRate, 2));
                break;
            case AudioKind.Microphone:
                device = Find(DataFlow.Capture, deviceId);
                builder = builder.WithDevice(device);
                break;
        }
        var recorder = await builder.BuildAsync().ConfigureAwait(false);
        var format = ToPcm(recorder.WaveFormat);
        return new AudioSource(name, recorder, new AudioSourcePipeline(format, clock, mixer, input));
    }

    public void Start() => _recorder.StartRecording();

    private void OnData(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        try
        {
            // WASAPI da la hora de captura en unidades de 100 ns del mismo contador (QPC) que el reloj.
            _pipeline.OnPacket(data, flags.HasFlag(AudioClientBufferFlags.Silent), qpcPosition);
        }
        catch (Exception ex)
        {
            AppLog.Write($"audio «{Name}»: {ex.Message}");
        }
    }

    private static MMDevice Find(DataFlow flow, string? id)
    {
        using var e = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                var d = e.GetDevice(id);
                if (d.State == DeviceState.Active)
                    return d;
                d.Dispose();
            }
            catch (Exception)
            {
                // El dispositivo guardado ya no está: se usa el predeterminado.
            }
        }
        return e.GetDefaultAudioEndpoint(flow, Role.Multimedia);
    }

    internal static PcmFormat ToPcm(WaveFormat wf)
    {
        var isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat
            || (wf is WaveFormatExtensible ext && ext.SubFormat == NAudio.MediaFoundation.AudioSubtypes.MFAudioFormat_Float);
        return new PcmFormat(wf.SampleRate, wf.Channels, wf.BitsPerSample, isFloat);
    }

    public async ValueTask DisposeAsync()
    {
        _recorder.DataAvailable -= OnData;
        try { _recorder.StopRecording(); } catch (Exception) { }
        try { await _recorder.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }
}

internal enum AudioKind
{
    Output,
    App,
    Microphone,
}
