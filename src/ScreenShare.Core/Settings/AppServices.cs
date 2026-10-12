using System.Globalization;
using System.Text.Json;

namespace SocScreenShare.Settings;

/// <summary>
/// Registro de errores en <c>%LOCALAPPDATA%\sOCScreenShare\errors.log</c> (General §6.9 y §6.12): lo
/// técnico, con la traza, va aquí. Al pasar de 1 MB se guarda como <c>errors.old.log</c>.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sOCScreenShare");

    public static string FilePath => Path.Combine(Folder, "errors.log");

    public static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1024 * 1024)
                    File.Move(FilePath, Path.Combine(Folder, "errors.old.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>Novedades de las cinco últimas versiones (General §6.7), de <c>whatsnew.json</c> incrustado.</summary>
public static class WhatsNew
{
    public const int MaxVersions = 5;

    public sealed record Release(string Version, DateTime? Date, IReadOnlyList<string> Items);

    public static IReadOnlyList<Release> Load(string language, Stream? source = null)
    {
        try
        {
            using var stream = source ?? typeof(WhatsNew).Assembly.GetManifestResourceStream("SocScreenShare.whatsnew.json");
            if (stream is null)
                return [];
            using var doc = JsonDocument.Parse(stream);
            var list = new List<Release>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var items = entry.TryGetProperty(language, out var arr) || entry.TryGetProperty("en", out arr)
                    ? arr.EnumerateArray().Select(i => i.GetString() ?? string.Empty).Where(i => i.Length > 0).ToList()
                    : [];
                DateTime? date = entry.TryGetProperty("date", out var d)
                    && DateTime.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed : null;
                list.Add(new Release(entry.GetProperty("version").GetString() ?? string.Empty, date, items));
            }
            return [.. list.Take(MaxVersions)];
        }
        catch (Exception ex)
        {
            AppLog.Write($"novedades ilegibles: {ex.Message}");
            return [];
        }
    }

    /// <summary><c>2026.10.12.0</c> y <c>2026.10.12.00</c> son la misma.</summary>
    public static bool SameVersion(string? a, string? b) =>
        a is not null && b is not null && Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va == vb : string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>Qué sale solo al arrancar: la guía la primera vez de todas; las novedades al cambiar de versión.</summary>
    public static (bool Guide, bool News) OnStartup(RecorderSettings s, string current) =>
        (!s.GuideShown, s.GuideShown && s.LastSeenVersion is not null && !SameVersion(s.LastSeenVersion, current));

    /// <summary>La versión del ensamblado con el formato del CHANGELOG (año.mes.día.n).</summary>
    public static string CurrentVersion()
    {
        var v = typeof(WhatsNew).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
    }
}
