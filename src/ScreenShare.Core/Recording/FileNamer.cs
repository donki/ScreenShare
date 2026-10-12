using System.Globalization;

namespace SocScreenShare.Recording;

/// <summary>
/// Nombre del fichero de cada grabación: «Grabación 2026-10-12 15.30.05.mp4» (con la palabra en el
/// idioma de la aplicación). Sin dos puntos, que Windows no admite, y sin pisar nada: si ya existe se
/// añade « (2)», « (3)»…
/// </summary>
public static class FileNamer
{
    public const string Extension = ".mp4";

    public static string BaseName(string prefix, DateTime when) =>
        Sanitize(prefix) + " " + when.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture);

    public static string NewPath(string folder, string prefix, DateTime when, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var name = BaseName(prefix, when);
        var path = Path.Combine(folder, name + Extension);
        for (var n = 2; exists(path); n++)
            path = Path.Combine(folder, $"{name} ({n}){Extension}");
        return path;
    }

    /// <summary>Quita lo que Windows no deja poner en un nombre de fichero.</summary>
    public static string Sanitize(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(text.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "ScreenShare" : clean;
    }
}
