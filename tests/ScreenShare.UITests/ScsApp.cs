using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;

namespace SocScreenShare.UITests;

/// <summary>
/// Una instancia de la aplicación para una prueba: el exe Debug en modo aislado (SOC_SANDBOX con una
/// carpeta nueva), con utilidades para esperar ventanas, buscar por AutomationId y capturar sin robar
/// el foco (PrintWindow). Como WsmApp de sOC WSManager.
/// </summary>
public sealed class ScsApp : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _testName;
    private int _shot;

    public Application App { get; }
    public UIA3Automation Automation { get; } = new();
    public Window Main { get; private set; }
    public ConditionFactory Cf => Automation.ConditionFactory;
    public string DataFolder { get; }
    public string VideosFolder => Path.Combine(DataFolder, "videos");

    private ScsApp(string testName, string? settingsJson)
    {
        _testName = testName;
        DataFolder = Path.Combine(Path.GetTempPath(), "sOCScreenShare-uitests", $"{testName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(Path.Combine(DataFolder, "settings.json"), settingsJson ??
            $$"""{ "language": "es", "guideShown": true, "lastSeenVersion": "{{AppVersion}}", "systemAudio": "none" }""");
        var psi = new ProcessStartInfo(ExePath, "--size 760x900") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExePath)! };
        psi.Environment["SOC_SANDBOX"] = DataFolder;
        App = Application.Launch(psi);
        Main = Retry.WhileNull(() => App.GetMainWindow(Automation, TimeSpan.FromSeconds(1)), Timeout, throwOnTimeout: true).Result!;
        Main.WaitUntilClickable(Timeout);
    }

    public static ScsApp Launch(string? settingsJson = null, [System.Runtime.CompilerServices.CallerMemberName] string testName = "") =>
        new(testName, settingsJson);

    public static string AppVersion => FileVersionInfo.GetVersionInfo(ExePath).FileVersion ?? "0";

    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>El exe Debug (SCREENSHARE_EXE lo cambia; tiene que ser Debug: en Release no hay modo aislado).</summary>
    public static string ExePath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable("SCREENSHARE_EXE") is { Length: > 0 } custom
                ? custom
                : Path.Combine(RepoRoot, "src", "ScreenShare.App", "bin", "Debug", "net10.0-windows10.0.22621.0", "sOCScreenShare.exe");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Compila antes la aplicación en Debug. Buscado en {path}");
            return path;
        }
    }

    public static string ArtifactsFolder { get; } = Directory.CreateDirectory(Path.Combine(RepoRoot, "tests", "ScreenShare.UITests", "artifacts")).FullName;

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ScreenShare.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("ScreenShare.slnx");
    }

    public AutomationElement ById(AutomationElement parent, string id) =>
        Retry.WhileNull(() => parent.FindFirstDescendant(Cf.ByAutomationId(id)), Timeout, throwOnTimeout: true, timeoutMessage: $"No aparece {id}").Result!;

    public AutomationElement? TryById(AutomationElement parent, string id) => parent.FindFirstDescendant(Cf.ByAutomationId(id));

    public Button Button(AutomationElement parent, string id) => ById(parent, id).AsButton();

    public static void Press(Button button) => button.Patterns.Invoke.Pattern.Invoke();

    public static void Select(AutomationElement e) => e.Patterns.SelectionItem.Pattern.Select();

    public Window WaitModal(Window? owner = null)
    {
        var w = owner ?? Main;
        var modal = Retry.WhileNull(() => w.ModalWindows.FirstOrDefault(), Timeout, throwOnTimeout: true, timeoutMessage: "No se abre el diálogo").Result!;
        modal.WaitUntilClickable(Timeout);
        return modal;
    }

    public void WaitNoModal(Window? owner = null) =>
        Retry.WhileTrue(() => (owner ?? Main).ModalWindows.Length > 0, Timeout, throwOnTimeout: true, timeoutMessage: "El diálogo no se cierra");

    /// <summary>La ventana principal vigente (al cambiar de idioma se rehace).</summary>
    public Window CurrentMain()
    {
        Main = Retry.WhileNull(() => Automation.GetDesktop()
            .FindAllChildren(Cf.ByProcessId(App.ProcessId))
            .FirstOrDefault(w => w.FindFirstDescendant(Cf.ByAutomationId("RecordButton")) is not null)?.AsWindow(),
            Timeout, throwOnTimeout: true, timeoutMessage: "No está la ventana principal").Result!;
        return Main;
    }

    public string Settings() => File.ReadAllText(Path.Combine(DataFolder, "settings.json"));

    public string Capture(AutomationElement window, string step)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        GetWindowRect(hwnd, out var r);
        var (w, h) = (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try { PrintWindow(hwnd, hdc, 2); }
            finally { g.ReleaseHdc(hdc); }
        }
        var file = Path.Combine(ArtifactsFolder, $"{_testName}-{++_shot:00}-{step}.png");
        bmp.Save(file, ImageFormat.Png);
        return file;
    }

    public void Dispose()
    {
        try
        {
            foreach (var modal in CurrentMain().ModalWindows)
                modal.Close();
            App.Close();
            var p = Process.GetProcessById(App.ProcessId);
            if (!p.WaitForExit(8000))
                p.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            try { Process.GetProcessById(App.ProcessId).Kill(entireProcessTree: true); } catch { }
        }
        App.Dispose();
        Automation.Dispose();
        try { Directory.Delete(DataFolder, recursive: true); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
