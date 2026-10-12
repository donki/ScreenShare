using System.Runtime.InteropServices;
using System.Windows.Interop;
using SocScreenShare.Input;

namespace SocScreenShare.App.Services;

/// <summary>
/// Una ventana solo de mensajes que vive mientras la aplicación: recibe el atajo global
/// (<c>RegisterHotKey</c>) y el «enséñate» de una segunda instancia.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private const int WmHotkey = 0x0312;
    public const int StartId = 0x5301;
    public const int StopId = 0x5302;
    private const int ModNoRepeat = 0x4000;

    private readonly HwndSource _source;
    private readonly HashSet<int> _registered = [];

    public MessageWindow()
    {
        _source = new HwndSource(new HwndSourceParameters("sOCScreenShareMessages") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(Hook);
    }

    /// <summary>Se ha pulsado un atajo (<see cref="StartId"/> o <see cref="StopId"/>), funcione o no la ventana.</summary>
    public event Action<int>? HotkeyPressed;
    public event Action? ShowRequested;

    public nint Handle => _source.Handle;

    /// <summary>Registra el atajo de empezar; false si otra aplicación ya lo tiene.</summary>
    public bool RegisterStart(Hotkey hotkey) => Register(StartId, hotkey);

    /// <summary>Registra el atajo de parar; false si otra aplicación ya lo tiene.</summary>
    public bool RegisterStop(Hotkey hotkey) => Register(StopId, hotkey);

    private bool Register(int id, Hotkey hotkey)
    {
        if (_registered.Remove(id))
            UnregisterHotKey(_source.Handle, id);
        var ok = RegisterHotKey(_source.Handle, id, (int)hotkey.Modifiers | ModNoRepeat, hotkey.VirtualKey);
        if (ok)
            _registered.Add(id);
        return ok;
    }

    public void Unregister()
    {
        foreach (var id in _registered)
            UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() is StartId or StopId)
        {
            HotkeyPressed?.Invoke(wParam.ToInt32());
            handled = true;
        }
        else if ((uint)msg == SingleInstance.ShowMessage)
        {
            ShowRequested?.Invoke();
            SingleInstance.NotifyShown();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
