using System.Runtime.InteropServices;
using SocScreenShare.AutoRecord;
using SocScreenShare.Capture;
using SocScreenShare.Settings;

namespace SocScreenShare.Engine;

/// <summary>
/// Avisa cuando una ventana de nivel superior se enseña, cambia de título, se esconde o se destruye,
/// con <c>SetWinEventHook</c> (sin sondeo). Corre en un hilo propio con su bucle de mensajes, nunca
/// en el de la interfaz: si la interfaz se atasca, los avisos siguen llegando, y al revés (en sOC
/// Credentials un gancho en el hilo de la interfaz la dejó colgada). Los avisos salen en ese hilo:
/// quien los recibe los pasa al suyo.
/// </summary>
public sealed class WindowWatcher : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectNameChange = 0x800C;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0;
    private const uint GaRoot = 2;
    private const uint WmQuit = 0x0012;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly bool _skipOwnProcess;
    private WinEventProc? _proc;   // referencia viva: si el recolector se la lleva, Windows llama a basura
    private uint _threadId;
    private nint _hookA, _hookB;

    /// <param name="skipOwnProcess">Las ventanas de esta aplicación no cuentan (false solo en pruebas).</param>
    public WindowWatcher(bool skipOwnProcess = true)
    {
        _skipOwnProcess = skipOwnProcess;
        _thread = new Thread(Run) { IsBackground = true, Name = "sOCScreenShare ventanas" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Un aviso, con lo que se sabe de la ventana (null si ya no existe). En el hilo del vigilante.</summary>
    public event Action<WindowEventKind, nint, WindowInfo?>? WindowEvent;

    public bool IsRunning => _hookA != 0;

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = OnEvent;
        var flags = WinEventOutOfContext | (_skipOwnProcess ? WinEventSkipOwnProcess : 0);
        // Dos rangos: crear…esconder (0x8000-0x8003) y el cambio de nombre (0x800C).
        _hookA = SetWinEventHook(EventObjectCreate, EventObjectHide, 0, _proc, 0, 0, flags);
        _hookB = SetWinEventHook(EventObjectNameChange, EventObjectNameChange, 0, _proc, 0, 0, flags);
        if (_hookA == 0)
            AppLog.Write("no se pudo vigilar las ventanas (SetWinEventHook)");
        _ready.Set();
        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_hookA != 0) UnhookWinEvent(_hookA);
        if (_hookB != 0) UnhookWinEvent(_hookB);
        _hookA = _hookB = 0;
    }

    private void OnEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjIdWindow || idChild != 0 || hwnd == 0)
            return;
        try
        {
            WindowEventKind kind;
            switch (evt)
            {
                case EventObjectDestroy:
                    WindowEvent?.Invoke(WindowEventKind.Destroyed, hwnd, null);
                    return;
                case EventObjectHide:
                    kind = WindowEventKind.Hidden;
                    break;
                case EventObjectNameChange:
                    kind = WindowEventKind.TitleChanged;
                    break;
                case EventObjectShow:
                case EventObjectCreate:
                    kind = WindowEventKind.Shown;
                    break;
                default:
                    return;
            }
            // Solo ventanas de nivel superior (no sus controles).
            if (GetAncestor(hwnd, GaRoot) != hwnd)
                return;
            var info = Native.IsWindow(hwnd) ? Native.Describe(hwnd) : null;
            WindowEvent?.Invoke(kind, hwnd, info);
        }
        catch (Exception ex)
        {
            AppLog.Write($"aviso de ventana: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(3));
        _ready.Dispose();
    }

    private delegate void WinEventProc(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public nint hwnd; public uint message; public nint wParam; public nint lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, nint w, nint l);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
