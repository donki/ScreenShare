using System.Text.RegularExpressions;
using SocScreenShare.Capture;

namespace SocScreenShare.AutoRecord;

/// <summary>Cómo se compara el título de la ventana.</summary>
public enum TitleMatch
{
    /// <summary>El título contiene el texto (sin distinguir mayúsculas).</summary>
    Contains,

    /// <summary>El título cumple la expresión regular (sin distinguir mayúsculas).</summary>
    Regex,
}

/// <summary>
/// Una regla de grabación automática: cuando se abre una ventana de esta aplicación (y, si se
/// indica, con este título), se graba esa ventana con su sonido; al cerrarse, se para y se guarda.
/// </summary>
public sealed class AutoRecordRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;

    /// <summary>Nombre del ejecutable, sin «.exe» (p. ej. «Teams»).</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>Filtro del título; vacío = cualquier ventana de la aplicación.</summary>
    public string? Title { get; set; }

    public TitleMatch TitleMode { get; set; } = TitleMatch.Contains;

    /// <summary>Parar y guardar cuando la ventana se cierra o se esconde.</summary>
    public bool StopOnClose { get; set; } = true;

    /// <summary>«Teams.exe», «C:\…\Teams.exe» o «Teams» → «Teams».</summary>
    public static string NormalizeProcess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        var file = Path.GetFileName(name.Trim());
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }

    /// <summary>Null si la regla vale; si no, la clave de <c>Loc</c> del motivo.</summary>
    public string? Validate()
    {
        if (NormalizeProcess(ProcessName).Length == 0)
            return "ErrRuleNoApp";
        if (TitleMode == TitleMatch.Regex && !string.IsNullOrEmpty(Title))
        {
            try { _ = new Regex(Title, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { return "ErrRuleBadRegex"; }
        }
        return null;
    }

    public AutoRecordRule Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        ProcessName = ProcessName,
        Title = Title,
        TitleMode = TitleMode,
        StopOnClose = StopOnClose,
    };
}

/// <summary>¿Esta ventana cumple alguna regla?</summary>
public static class AutoRecordMatcher
{
    /// <summary>La primera regla activa que cumple la ventana, o null.</summary>
    public static AutoRecordRule? Match(IEnumerable<AutoRecordRule> rules, WindowInfo window)
    {
        // Solo ventanas que una persona ve como ventana: visibles, con título, que no son herramientas.
        if (!window.Visible || window.Cloaked || window.ToolWindow || string.IsNullOrWhiteSpace(window.Title))
            return null;
        foreach (var rule in rules)
            if (Matches(rule, window))
                return rule;
        return null;
    }

    public static bool Matches(AutoRecordRule rule, WindowInfo window)
    {
        if (!rule.Enabled || rule.Validate() is not null)
            return false;
        if (!string.Equals(AutoRecordRule.NormalizeProcess(rule.ProcessName), window.ProcessName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(rule.Title))
            return true;
        if (rule.TitleMode == TitleMatch.Contains)
            return window.Title.Contains(rule.Title, StringComparison.CurrentCultureIgnoreCase);
        try
        {
            return Regex.IsMatch(window.Title, rule.Title, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>Lo que Windows avisa de una ventana.</summary>
public enum WindowEventKind
{
    Shown,
    TitleChanged,
    Hidden,
    Destroyed,
}

/// <summary>Qué hacer ante un aviso.</summary>
public enum AutoAction
{
    None,
    Start,
    Stop,
}

public sealed record AutoDecision(AutoAction Action, AutoRecordRule? Rule = null, nint Window = 0)
{
    public static readonly AutoDecision Nothing = new(AutoAction.None);
}

/// <summary>
/// El estado de la grabación automática: qué ventana se está grabando por qué regla, y qué ventanas
/// ya dispararon (una ventana graba una vez; si se para a mano no vuelve a empezar sola mientras siga
/// abierta, aunque cambie de título). Nunca empieza una segunda grabación si ya hay una en marcha.
/// </summary>
public sealed class AutoRecordTracker
{
    private readonly HashSet<nint> _handled = [];

    /// <summary>La ventana que se graba por una regla (0 si ninguna).</summary>
    public nint CurrentWindow { get; private set; }
    public AutoRecordRule? CurrentRule { get; private set; }

    /// <param name="window">Lo que se sabe de la ventana (null si ya no existe).</param>
    /// <param name="enabled">El interruptor general.</param>
    /// <param name="recording">Si ya hay una grabación (automática o a mano).</param>
    public AutoDecision OnEvent(WindowEventKind kind, nint hwnd, WindowInfo? window, IReadOnlyList<AutoRecordRule> rules, bool enabled, bool recording)
    {
        switch (kind)
        {
            case WindowEventKind.Hidden:
            case WindowEventKind.Destroyed:
                _handled.Remove(hwnd);
                if (hwnd != 0 && hwnd == CurrentWindow)
                {
                    var rule = CurrentRule;
                    var stop = rule?.StopOnClose == true && recording;
                    if (kind == WindowEventKind.Destroyed || stop)
                        Clear();
                    return stop ? new AutoDecision(AutoAction.Stop, rule, hwnd) : AutoDecision.Nothing;
                }
                return AutoDecision.Nothing;

            default:
                if (!enabled || recording || window is null || _handled.Contains(hwnd))
                    return AutoDecision.Nothing;
                var match = AutoRecordMatcher.Match(rules, window);
                if (match is null)
                    return AutoDecision.Nothing;
                _handled.Add(hwnd);
                CurrentWindow = hwnd;
                CurrentRule = match;
                return new AutoDecision(AutoAction.Start, match, hwnd);
        }
    }

    /// <summary>La grabación automática no llegó a empezar o ya ha terminado (por lo que sea).</summary>
    public void Clear()
    {
        CurrentWindow = 0;
        CurrentRule = null;
    }
}
