using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using SocScreenShare.Localization;

namespace SocScreenShare.App.Services;

/// <summary>
/// El icono del área de notificación, que solo está mientras se graba: el tiempo en el globo de
/// ayuda, clic para abrir la ventana y menú con pausar/seguir, parar y abrir. Como el de sOC
/// WSManager (Shell_NotifyIcon sobre una ventana de mensajes propia).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmTray = 0x8001;
    private const int WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmContextMenu = 0x007B;
    private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource _source;
    private readonly Func<ContextMenu> _buildMenu;
    private readonly Action _open;
    private bool _added;
    private bool _recording;
    private static readonly Lazy<IntPtr> RecordingIcon = new(LoadRecordingIcon);
    private string _tip = string.Empty;

    public TrayIcon(Func<ContextMenu> buildMenu, Action open)
    {
        _buildMenu = buildMenu;
        _open = open;
        _source = new HwndSource(new HwndSourceParameters("sOCScreenShareTray") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(Hook);
        Add();
    }

    public void SetTip(string tip)
    {
        _tip = tip.Length > 127 ? tip[..127] : tip;
        if (!_added)
            return;
        var data = Data();
        data.uFlags = 0x4;   // NIF_TIP
        data.szTip = _tip;
        Shell_NotifyIcon(1, ref data);
    }

    /// <summary>Mientras graba, el icono lleva el punto rojo.</summary>
    public void SetRecording(bool recording)
    {
        if (recording == _recording)
            return;
        _recording = recording;
        if (!_added)
            return;
        var data = Data();
        data.uFlags = 0x2;   // NIF_ICON
        data.hIcon = CurrentIcon();
        Shell_NotifyIcon(1, ref data);
    }

    private IntPtr CurrentIcon() => _recording && RecordingIcon.Value != IntPtr.Zero ? RecordingIcon.Value : LoadAppIcon();

    /// <summary>El icono «grabando» desde el PNG incrustado (Windows lo crea a partir de los bytes del PNG).</summary>
    private static IntPtr LoadRecordingIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/tray-recording.png"));
            if (info is null)
                return IntPtr.Zero;
            using var ms = new System.IO.MemoryStream();
            info.Stream.CopyTo(ms);
            var bytes = ms.ToArray();
            return CreateIconFromResourceEx(bytes, bytes.Length, true, 0x00030000, 0, 0, 0);
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    public void Notify(string title, string text)
    {
        if (!_added)
            return;
        var data = Data();
        data.uFlags = 0x10;   // NIF_INFO
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = 0x2;   // NIIF_WARNING
        Shell_NotifyIcon(1, ref data);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTray)
        {
            var evt = (int)((long)lParam & 0xFFFF);
            if (evt == WmLButtonUp)
                _open();
            else if (evt is WmRButtonUp or WmContextMenu)
                ShowMenu();
            handled = true;
        }
        else if ((uint)msg == TaskbarCreated)
        {
            _added = false;
            Add();
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var menu = _buildMenu();
        menu.Placement = PlacementMode.MousePoint;
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource popup)
            {
                SetForegroundWindow(popup.Handle);
                Engine.Devices.ExcludeFromCapture(popup.Handle, true);
            }
        };
        menu.IsOpen = true;
    }

    private void Add()
    {
        if (_added)
            return;
        var data = Data();
        data.uFlags = 0x1 | 0x2 | 0x4;
        data.uCallbackMessage = WmTray;
        data.hIcon = CurrentIcon();
        data.szTip = _tip.Length > 0 ? _tip : Loc.Get("AppTitle");
        _added = Shell_NotifyIcon(0, ref data);
    }

    private NotifyIconData Data() => new() { cbSize = Marshal.SizeOf<NotifyIconData>(), hWnd = _source.Handle, uID = 1 };

    private static IntPtr LoadAppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } path)
            {
                var large = new IntPtr[1];
                var small = new IntPtr[1];
                if (ExtractIconEx(path, 0, large, small, 1) > 0 && small[0] != IntPtr.Zero)
                    return small[0];
            }
        }
        catch (Exception) { }
        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data();
            Shell_NotifyIcon(2, ref data);
            _added = false;
        }
        _source.Dispose();
    }

    /// <summary>Un elemento del menú con su icono plano (glifo de Segoe Fluent Icons).</summary>
    public static MenuItem Item(string text, string glyph, Action action, bool enabled = true, bool danger = false)
    {
        var item = new MenuItem
        {
            Header = text,
            Style = (Style)Application.Current.FindResource("TrayItem"),
            IsEnabled = enabled,
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 14,
                Foreground = (Brush)Application.Current.FindResource(danger ? "Danger" : "PrimaryText"),
            },
        };
        item.Click += (_, _) => action();
        return item;
    }

    public static Separator Separator() => new() { Style = (Style)Application.Current.FindResource("TraySeparator") };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconFromResourceEx(byte[] data, int size, bool icon, int version, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, int count);
}
