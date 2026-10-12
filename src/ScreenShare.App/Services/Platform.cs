using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using SocScreenShare.Localization;
using SocScreenShare.Settings;

namespace SocScreenShare.App.Services;

/// <summary>La extensión <c>loc:T</c> del XAML: el texto traducido de una clave.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}

/// <summary>Tema claro u oscuro siguiendo al de Windows, con los nombres de recurso de App.xaml.</summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDark { get; private set; }

    public static void Apply()
    {
        IsDark = PrefersDark();
        var r = Application.Current.Resources;
        if (IsDark)
        {
            Set(r, "PageBackground", "#141318");
            Set(r, "CardBackground", "#201F27");
            Set(r, "Separator", "#48454F");
            Set(r, "TextPrimary", "#E6E1E9");
            Set(r, "TextSecondary", "#C7C4D8");
            Set(r, "WarningSurface", "#33291A");
            Set(r, "SelectionSurface", "#2E2A55");
            Set(r, "PrimaryLight", "#8F88FF");
            Set(r, "PrimaryText", "#A9A3FF");
        }
        else
        {
            Set(r, "PageBackground", "#F8F9FA");
            Set(r, "CardBackground", "#FFFFFF");
            Set(r, "Separator", "#C7C4D8");
            Set(r, "TextPrimary", "#191C1D");
            Set(r, "TextSecondary", "#464555");
            Set(r, "WarningSurface", "#FFF4E5");
            Set(r, "SelectionSurface", "#E4E1FF");
            Set(r, "PrimaryLight", "#635BF2");
            Set(r, "PrimaryText", "#3525CD");
        }
    }

    private static readonly Lazy<ImageSource?> AppIcon = new(() =>
    {
        try { return System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/appicon.ico")); }
        catch (Exception) { return null; }
    });

    /// <summary>
    /// El icono de la aplicación en la barra de título y en la de tareas, y la barra de título
    /// siguiendo al tema (la pinta Windows: se le pide por DWM).
    /// </summary>
    public static void ApplyToWindow(Window window)
    {
        if (window.Icon is null && AppIcon.Value is { } icon)
            window.Icon = icon;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        if (App.Controller?.Machine.IsBusy == true)
            Engine.Devices.ExcludeFromCapture(handle, true);
    }

    /// <summary>
    /// Mientras se graba, las ventanas de la aplicación no salen en el vídeo; el resto del tiempo se
    /// ven en cualquier captura (excluirlas siempre las dejaba en negro en capturas y pantallas compartidas).
    /// </summary>
    public static void ExcludeAllFromCapture(bool exclude)
    {
        foreach (Window w in Application.Current.Windows)
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero)
                Engine.Devices.ExcludeFromCapture(h, exclude);
        }
    }

    private static bool PrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Set(ResourceDictionary resources, string key, string hex) =>
        resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>
/// Modo aislado para las pruebas de interfaz (solo Debug, General §8.4): con <c>SOC_SANDBOX</c> los
/// ajustes, el registro y los vídeos van a una carpeta temporal; no hay bandeja, ni instancia única,
/// ni atajo global, y las ventanas no se activan al abrir (no roban el foco).
/// </summary>
public static class Sandbox
{
    public const string Variable = "SOC_SANDBOX";

#if DEBUG
    public static bool IsOn { get; } = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 };
#else
    public static bool IsOn => false;
#endif

    public static string? Folder { get; private set; }

    public static void Apply()
    {
        if (!IsOn)
            return;
        var value = Environment.GetEnvironmentVariable(Variable)!.Trim();
        Folder = Path.IsPathFullyQualified(value) ? value : Path.Combine(Path.GetTempPath(), "sOCScreenShare-sandbox");
        Directory.CreateDirectory(Folder);
        RecorderSettings.FilePath = Path.Combine(Folder, "settings.json");
        RecorderSettings.DefaultFolder = Path.Combine(Folder, "videos");
        AppLog.Folder = Folder;
        foreach (var type in typeof(Sandbox).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Window)) && !t.IsAbstract))
            Window.ShowActivatedProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(false));
    }
}

/// <summary>
/// Una sola instancia por sesión (General §8.3): manda la versión nueva; con la misma versión, la
/// abierta se enseña y avisa; si no contesta en unos segundos, arranca esta igual. Igual que en
/// sOC WSManager y sOC Credentials: si se toca uno, mirar los otros.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "Local\\sOCScreenShare.SingleInstance";
    private const string ShownEventName = "Local\\sOCScreenShare.Shown";
    private static Mutex? _mutex;

    public static uint ShowMessage { get; } = RegisterWindowMessage("sOCScreenShare.Show");

    public static bool Claim()
    {
        CloseOlderVersions();
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
                return true;
            _mutex.Dispose();
            _mutex = null;
        }
        catch (Exception)
        {
            return true;
        }
        try
        {
            using var shown = new EventWaitHandle(false, EventResetMode.AutoReset, ShownEventName);
            shown.Reset();
            AllowSetForegroundWindow(-1);
            for (var i = 0; i < 10; i++)
            {
                PostMessage(new IntPtr(0xFFFF), ShowMessage, IntPtr.Zero, IntPtr.Zero);
                if (shown.WaitOne(TimeSpan.FromSeconds(1)))
                    return false;
                if (!OtherInstanceAlive())
                    break;
            }
        }
        catch (Exception) { }
        try { _mutex = new Mutex(initiallyOwned: false, MutexName); } catch (Exception) { }
        return true;
    }

    private static void CloseOlderVersions()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            var mine = VersionOf(Environment.ProcessPath);
            if (mine is null)
                return;
            foreach (var other in Process.GetProcessesByName(me.ProcessName))
            {
                using (other)
                {
                    if (other.Id == me.Id)
                        continue;
                    try
                    {
                        var theirs = VersionOf(other.MainModule?.FileName);
                        if (theirs is null || theirs >= mine)
                            continue;
                        other.Kill();
                        other.WaitForExit(5000);
                    }
                    catch (Exception) { }
                }
            }
        }
        catch (Exception) { }
    }

    private static Version? VersionOf(string? exe) =>
        string.IsNullOrEmpty(exe) || !File.Exists(exe) ? null
        : Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var v) ? v : null;

    private static bool OtherInstanceAlive()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            return Process.GetProcessesByName(me.ProcessName).Any(p => { using (p) return p.Id != me.Id && !p.HasExited; });
        }
        catch (Exception) { return false; }
    }

    public static void NotifyShown()
    {
        try
        {
            using var shown = new EventWaitHandle(false, EventResetMode.AutoReset, ShownEventName);
            shown.Set();
        }
        catch (Exception) { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
