using SocScreenShare.Video;

namespace SocScreenShare.Capture;

/// <summary>Qué se graba.</summary>
public enum TargetKind
{
    Monitor,
    AllMonitors,
    Window,
}

/// <summary>Una pantalla, con su nombre de dispositivo de Windows (<c>\\.\DISPLAY1</c>), que es lo que se guarda en los ajustes.</summary>
public sealed record MonitorInfo(string Id, int Number, RectI Bounds, bool Primary, nint Handle = 0)
{
    public string SizeText => $"{Bounds.Width}×{Bounds.Height}";
}

/// <summary>Una ventana de nivel superior tal como la ve Windows.</summary>
public sealed record WindowInfo(
    nint Handle,
    string Title,
    int ProcessId,
    string ProcessName,
    bool Visible,
    bool Cloaked,
    bool ToolWindow,
    bool HasOwner,
    bool Minimized,
    RectI Bounds);

/// <summary>Qué ventanas se ofrecen para grabar: las que una persona reconoce como ventanas.</summary>
public static class WindowFilter
{
    // Ventanas del propio Windows que están siempre ahí y no son lo que nadie quiere grabar.
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "LockApp",
    };

    public static bool IsRecordable(WindowInfo w, int ownProcessId) =>
        w.Visible && !w.Cloaked && !w.ToolWindow && !w.HasOwner &&
        w.ProcessId != ownProcessId &&
        !string.IsNullOrWhiteSpace(w.Title) &&
        !ShellProcesses.Contains(w.ProcessName) &&
        (w.Minimized || (w.Bounds.Width >= 40 && w.Bounds.Height >= 40));

    public static IReadOnlyList<WindowInfo> Recordable(IEnumerable<WindowInfo> windows, int ownProcessId) =>
        [.. windows.Where(w => IsRecordable(w, ownProcessId))
            .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)];
}

/// <summary>Cuentas sobre la disposición de las pantallas.</summary>
public static class MonitorLayout
{
    /// <summary>La pantalla guardada si sigue conectada; si no, la principal; si no, la primera.</summary>
    public static MonitorInfo? Resolve(IReadOnlyList<MonitorInfo> monitors, string? savedId) =>
        monitors.FirstOrDefault(m => savedId is not null && string.Equals(m.Id, savedId, StringComparison.OrdinalIgnoreCase))
        ?? monitors.FirstOrDefault(m => m.Primary)
        ?? monitors.FirstOrDefault();

    /// <summary>El rectángulo que abarca todas las pantallas (escritorio virtual).</summary>
    public static RectI VirtualBounds(IEnumerable<MonitorInfo> monitors) => RectI.Union(monitors.Select(m => m.Bounds));

    /// <summary>
    /// Para dibujar el selector: cada pantalla escalada para que todo quepa en
    /// <paramref name="width"/>×<paramref name="height"/>, centrado, con su proporción real.
    /// </summary>
    public static IReadOnlyList<(MonitorInfo Monitor, double X, double Y, double W, double H)> Map(
        IReadOnlyList<MonitorInfo> monitors, double width, double height, double gap = 4)
    {
        var all = VirtualBounds(monitors);
        if (all.IsEmpty)
            return [];
        var scale = Math.Min(width / all.Width, height / all.Height);
        var offsetX = (width - all.Width * scale) / 2;
        var offsetY = (height - all.Height * scale) / 2;
        return [.. monitors.Select(m => (m,
            offsetX + (m.Bounds.X - all.X) * scale + gap / 2,
            offsetY + (m.Bounds.Y - all.Y) * scale + gap / 2,
            Math.Max(1, m.Bounds.Width * scale - gap),
            Math.Max(1, m.Bounds.Height * scale - gap)))];
    }

    /// <summary>Dónde va cada pantalla dentro del lienzo de «todas las pantallas».</summary>
    public static (int X, int Y) OffsetInCanvas(MonitorInfo monitor, RectI virtualBounds) =>
        (monitor.Bounds.X - virtualBounds.X, monitor.Bounds.Y - virtualBounds.Y);
}
