using System.Globalization;

namespace SocScreenShare.Recording;

/// <summary>
/// Cuánto debe durar una grabación: horas, minutos y segundos en una casilla cada uno. Cero es sin
/// límite. Una casilla vacía o con letras cuenta como cero, y lo que pase de 99:59:59 se queda ahí.
/// </summary>
public static class RecordingLimit
{
    public const int MaxSeconds = 99 * 3600 + 59 * 60 + 59;

    public static int ToSeconds(string? hours, string? minutes, string? seconds)
    {
        var total = Number(hours) * 3600L + Number(minutes) * 60L + Number(seconds);
        return (int)Math.Min(total, MaxSeconds);
    }

    public static (int Hours, int Minutes, int Seconds) Split(int total)
    {
        total = Math.Clamp(total, 0, MaxSeconds);
        return (total / 3600, total / 60 % 60, total % 60);
    }

    /// <summary>«01:30:00», como el contador de la ventana.</summary>
    public static string Format(int total)
    {
        var (h, m, s) = Split(total);
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00}");
    }

    public static bool Reached(TimeSpan elapsed, int limitSeconds) => limitSeconds > 0 && elapsed.TotalSeconds >= limitSeconds;

    private static long Number(string? text) =>
        long.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Min(n, MaxSeconds) : 0;
}
