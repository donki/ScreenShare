using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SocScreenShare.Capture;
using SocScreenShare.Video;

namespace SocScreenShare.Engine;

/// <summary>Las llamadas a Win32 que hacen falta para listar pantallas y ventanas.</summary>
internal static unsafe partial class Native
{
    public const int WdaNone = 0;
    public const int WdaExcludeFromCapture = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    public delegate bool MonitorEnumProc(nint monitor, nint hdc, ref RECT rect, nint data);
    public delegate bool WindowEnumProc(nint hwnd, nint data);

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] public static extern bool EnumWindows(WindowEnumProc proc, nint data);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hwnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(nint hwnd, int affinity);
    [DllImport("user32.dll")] public static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint period);

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const uint GwOwner = 4;
    private const int DwmwaCloaked = 14;
    private static readonly nint PerMonitorAwareV2 = -4;

    /// <summary>Mientras dure, este hilo ve los píxeles de verdad aunque el proceso no declare DPI por monitor (las pruebas).</summary>
    public readonly struct DpiScope : IDisposable
    {
        private readonly nint _old;
        public DpiScope() => _old = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        public void Dispose()
        {
            if (_old != 0)
                SetThreadDpiAwarenessContext(_old);
        }
    }

    public static RectI ToRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        using var dpi = new DpiScope();
        var list = new List<(nint Handle, MONITORINFOEX Info)>();
        EnumDisplayMonitors(0, 0, (nint m, nint hdc, ref RECT r, nint d) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(m, ref info))
                list.Add((m, info));
            return true;
        }, 0);
        // Numeradas de izquierda a derecha y de arriba abajo, como las ve una persona.
        return [.. list
            .OrderBy(x => x.Info.rcMonitor.Left).ThenBy(x => x.Info.rcMonitor.Top)
            .Select((x, i) => new MonitorInfo(x.Info.szDevice, i + 1, ToRect(x.Info.rcMonitor), (x.Info.dwFlags & 1) != 0, x.Handle))];
    }

    public static IReadOnlyList<WindowInfo> Windows()
    {
        using var dpi = new DpiScope();
        var list = new List<WindowInfo>();
        var names = new Dictionary<int, string>();
        EnumWindows((h, _) =>
        {
            try { list.Add(Describe(h, names)); } catch (Exception) { }
            return true;
        }, 0);
        return list;
    }

    public static WindowInfo Describe(nint h, Dictionary<int, string>? names = null)
    {
        var len = GetWindowTextLength(h);
        var sb = new StringBuilder(len + 1);
        if (len > 0)
            GetWindowText(h, sb, sb.Capacity);
        GetWindowThreadProcessId(h, out var pid);
        names ??= [];
        if (!names.TryGetValue(pid, out var name))
        {
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
            catch (Exception) { name = string.Empty; }
            names[pid] = name;
        }
        var ex = (long)GetWindowLongPtr(h, GwlExStyle);
        var cloaked = DwmGetWindowAttribute(h, DwmwaCloaked, out var c, sizeof(int)) == 0 && c != 0;
        GetWindowRect(h, out var rect);
        return new WindowInfo(h, sb.ToString(), pid, name, IsWindowVisible(h), cloaked,
            (ex & WsExToolWindow) != 0, GetWindow(h, GwOwner) != 0, IsIconic(h), ToRect(rect));
    }
}
