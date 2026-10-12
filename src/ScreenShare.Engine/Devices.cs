using SocScreenShare.Capture;
using SocScreenShare.Settings;
using Windows.Graphics.Capture;

namespace SocScreenShare.Engine;

/// <summary>Pantallas y ventanas que se pueden grabar ahora mismo.</summary>
public static class Devices
{
    public static IReadOnlyList<MonitorInfo> Monitors() => Native.Monitors();

    /// <summary>Todas las ventanas de nivel superior (sin filtrar).</summary>
    public static IReadOnlyList<WindowInfo> Windows() => Native.Windows();

    /// <summary>Las ventanas que se ofrecen para grabar (sin las de esta aplicación).</summary>
    public static IReadOnlyList<WindowInfo> RecordableWindows() => WindowFilter.Recordable(Native.Windows(), Environment.ProcessId);

    public static bool IsWindowAlive(nint hwnd) => hwnd != 0 && Native.IsWindow(hwnd);

    /// <summary>El proceso de una ventana (0 si ya no existe).</summary>
    public static int ProcessOf(nint hwnd)
    {
        if (hwnd == 0 || !Native.IsWindow(hwnd))
            return 0;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>Lo que se sabe ahora de una ventana (null si ya no existe).</summary>
    public static WindowInfo? Describe(nint hwnd) => hwnd != 0 && Native.IsWindow(hwnd) ? Native.Describe(hwnd) : null;

    /// <summary>¿Este Windows sabe capturar pantallas (Windows 10 2004 o posterior)?</summary>
    public static bool CaptureSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch (Exception ex)
            {
                AppLog.Write($"captura de pantalla no disponible: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Que una ventana de esta aplicación no salga en la grabación (ni en ninguna otra captura).
    /// Desde Windows 10 2004; en versiones anteriores no hace nada.
    /// </summary>
    public static void ExcludeFromCapture(nint hwnd, bool exclude)
    {
        if (hwnd != 0)
            Native.SetWindowDisplayAffinity(hwnd, exclude ? Native.WdaExcludeFromCapture : Native.WdaNone);
    }
}
