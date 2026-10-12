using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SocScreenShare.AutoRecord;
using SocScreenShare.Capture;
using SocScreenShare.Input;
using SocScreenShare.Recording;

namespace SocScreenShare.Settings;

/// <summary>Qué sonido del equipo se graba.</summary>
public enum SystemAudioMode
{
    /// <summary>Ninguno.</summary>
    None,

    /// <summary>Todo lo que suena por un dispositivo de salida (altavoces, auriculares, la pantalla con altavoces…).</summary>
    All,

    /// <summary>Solo lo que suena desde una aplicación (y sus procesos hijos).</summary>
    App,
}

/// <summary>
/// Los ajustes, en <c>%AppData%\sOCScreenShare\settings.json</c>. Se guardan al cambiar cualquier
/// cosa en la ventana. Un fichero ilegible no impide arrancar: se apunta en el registro y se usan
/// los de por defecto; un valor fuera de rango se corrige al cargar.
/// </summary>
public sealed class RecorderSettings
{
    public const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Legible a mano: «Ctrl+Shift+F9» y «Reunión», no «Ctrl+Shift…» (es un fichero local, no HTML).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [JsonIgnore]
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sOCScreenShare", "settings.json");

    /// <summary>La carpeta de vídeos del usuario + «ScreenShare».</summary>
    [JsonIgnore]
    public static string DefaultFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenShare");

    public int Schema { get; set; } = CurrentSchema;
    public string? Language { get; set; }
    public string? LastSeenVersion { get; set; }
    public bool GuideShown { get; set; }

    public string OutputFolder { get; set; } = DefaultFolder;
    public TargetKind Target { get; set; } = TargetKind.Monitor;
    public string? MonitorId { get; set; }
    /// <summary>Atajo de calidad, o <see cref="QualityPreset.Custom"/> para usar <see cref="CustomQuality"/>.</summary>
    public QualityPreset Quality { get; set; } = QualityPreset.Share;

    /// <summary>Las opciones de audio y vídeo (con un atajo elegido, sus valores).</summary>
    public QualityOptions CustomQuality { get; set; } = new();
    public bool ShowCursor { get; set; } = true;
    public bool HideWhileRecording { get; set; } = true;

    public SystemAudioMode SystemAudio { get; set; } = SystemAudioMode.All;
    /// <summary>Dispositivo de salida; null = el predeterminado de Windows en cada grabación.</summary>
    public string? OutputDeviceId { get; set; }
    /// <summary>Nombre del proceso de la aplicación cuyo sonido se graba (sin «.exe»).</summary>
    public string? AppProcessName { get; set; }
    public int SystemVolume { get; set; } = 100;

    public bool MicEnabled { get; set; }
    /// <summary>Micrófono; null = el predeterminado de Windows.</summary>
    public string? MicDeviceId { get; set; }
    public int MicVolume { get; set; } = 100;

    /// <summary>Al grabar una ventana, su sonido (el de su proceso y sus hijos); si no, ninguno del equipo.</summary>
    public bool WindowAudio { get; set; } = true;
    public int WindowVolume { get; set; } = 100;

    public string StartHotkey { get; set; } = Hotkey.DefaultStart;
    public string StopHotkey { get; set; } = Hotkey.DefaultStop;

    /// <summary>Interruptor general de la grabación automática.</summary>
    public bool AutoRecordEnabled { get; set; }
    public List<AutoRecordRule> AutoRules { get; set; } = [];

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    public static RecorderSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<RecorderSettings>(File.ReadAllText(FilePath), Json);
                if (s is not null)
                    return s.Normalize();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"ajustes ilegibles, se usan los de por defecto: {ex.Message}");
        }
        return new RecorderSettings();
    }

    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"no se pudieron guardar los ajustes: {ex.Message}");
            return false;
        }
    }

    /// <summary>Lo que se usará al grabar: los valores del atajo o los personalizados.</summary>
    public QualityOptions EffectiveQuality() =>
        Quality == QualityPreset.Custom ? CustomQuality.Clone().Normalize() : QualityOptions.ForPreset(Quality);

    /// <summary>Una copia independiente (para cambiarla sin tocar la guardada).</summary>
    public RecorderSettings Clone() =>
        JsonSerializer.Deserialize<RecorderSettings>(JsonSerializer.Serialize(this, Json), Json)!.Normalize();

    /// <summary>Corrige lo que no valga (volúmenes, enumerados, carpeta vacía, atajo ilegible, idioma).</summary>
    public RecorderSettings Normalize()
    {
        Schema = CurrentSchema;
        if (Language is not ("es" or "en"))
            Language = null;
        if (string.IsNullOrWhiteSpace(OutputFolder))
            OutputFolder = DefaultFolder;
        if (!Enum.IsDefined(Target)) Target = TargetKind.Monitor;
        if (!Enum.IsDefined(Quality)) Quality = QualityPreset.Share;
        CustomQuality = (CustomQuality ?? new QualityOptions()).Normalize();
        if (Quality != QualityPreset.Custom)
            CustomQuality = QualityOptions.ForPreset(Quality);
        if (!Enum.IsDefined(SystemAudio)) SystemAudio = SystemAudioMode.All;
        SystemVolume = Math.Clamp(SystemVolume, 0, 100);
        MicVolume = Math.Clamp(MicVolume, 0, 100);
        WindowVolume = Math.Clamp(WindowVolume, 0, 100);
        if (Hotkey.ValidatePair(StartHotkey, StopHotkey) is not null)
        {
            // Uno ilegible, reservado o los dos iguales: vuelven los de por defecto (los dos, para que no choquen).
            StartHotkey = Hotkey.DefaultStart;
            StopHotkey = Hotkey.DefaultStop;
        }
        if (double.IsNaN(WindowWidth) || WindowWidth < 0) WindowWidth = 0;
        if (double.IsNaN(WindowHeight) || WindowHeight < 0) WindowHeight = 0;
        if (string.IsNullOrWhiteSpace(MonitorId)) MonitorId = null;
        if (string.IsNullOrWhiteSpace(OutputDeviceId)) OutputDeviceId = null;
        if (string.IsNullOrWhiteSpace(MicDeviceId)) MicDeviceId = null;
        if (string.IsNullOrWhiteSpace(AppProcessName)) AppProcessName = null;
        AutoRules ??= [];
        AutoRules.RemoveAll(r => r is null);
        foreach (var r in AutoRules)
        {
            r.ProcessName = AutoRecordRule.NormalizeProcess(r.ProcessName);
            if (!Enum.IsDefined(r.TitleMode)) r.TitleMode = TitleMatch.Contains;
            if (string.IsNullOrWhiteSpace(r.Id)) r.Id = Guid.NewGuid().ToString("N");
        }
        return this;
    }

    /// <summary>
    /// ¿Se puede grabar en esta carpeta? Se crea si no existe y se prueba a escribir un fichero.
    /// Devuelve null si vale, o la clave de <c>Loc</c> del motivo.
    /// </summary>
    public static string? CheckFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return "ErrFolderEmpty";
        try
        {
            if (!Path.IsPathFullyQualified(folder))
                return "ErrFolderNotAbsolute";
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".scs-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Write($"carpeta de destino no válida ({folder}): {ex.Message}");
            return "ErrFolderNotWritable";
        }
    }
}
