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

    /// <summary>Seguir grabando en el mismo fichero, pero otra ventana de la misma regla.</summary>
    Switch,
}

public sealed record AutoDecision(AutoAction Action, AutoRecordRule? Rule = null, nint Window = 0)
{
    public static readonly AutoDecision Nothing = new(AutoAction.None);
}

/// <summary>
/// El estado de la grabación automática: qué ventana se está grabando por qué regla, y qué ventanas
/// ya dispararon (una ventana graba una vez; si se para a mano no vuelve a empezar sola mientras siga
/// abierta, aunque cambie de título). Nunca empieza una segunda grabación si ya hay una en marcha.
/// <para>
/// Mientras graba, la grabación sigue a la ventana más reciente de la misma regla: hay programas que
/// abren primero una ventana para elegir y lo que interesa sale en otra (TeamViewer: el selector y
/// luego la conexión). Esa otra pasa a grabarse en el mismo fichero; al cerrarse, se vuelve a la
/// anterior que siga abierta, y cuando no queda ninguna, se para. Las ventanas con dueño (diálogos
/// del programa) no se llevan la grabación.
/// </para>
/// </summary>
public sealed class AutoRecordTracker
{
    private readonly HashSet<nint> _handled = [];

    // Las ventanas de esta grabación, de la más antigua a la que se graba ahora (la última).
    private readonly List<nint> _chain = [];

    /// <summary>La ventana que se graba por una regla (0 si ninguna).</summary>
    public nint CurrentWindow => _chain.Count > 0 ? _chain[^1] : 0;
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
                if (hwnd == 0 || !_chain.Contains(hwnd))
                    return AutoDecision.Nothing;
                var rule = CurrentRule;
                if (hwnd != CurrentWindow)
                {
                    // Se va una de las de atrás: ya no se vuelve a ella.
                    _chain.Remove(hwnd);
                    return AutoDecision.Nothing;
                }
                if (_chain.Count > 1 && recording)
                {
                    _chain.RemoveAt(_chain.Count - 1);
                    return new AutoDecision(AutoAction.Switch, rule, CurrentWindow);
                }
                var stop = rule?.StopOnClose == true && recording;
                if (kind == WindowEventKind.Destroyed || stop)
                    Clear();
                return stop ? new AutoDecision(AutoAction.Stop, rule, hwnd) : AutoDecision.Nothing;

            default:
                if (!enabled || window is null || _handled.Contains(hwnd))
                    return AutoDecision.Nothing;
                if (recording)
                {
                    // Otra ventana de la regla que está grabando: la grabación se va con ella.
                    if (CurrentRule is not { } current || window.HasOwner || _chain.Contains(hwnd))
                        return AutoDecision.Nothing;
                    if (AutoRecordMatcher.Match(rules, window) is not { } same || same.Id != current.Id)
                        return AutoDecision.Nothing;
                    _handled.Add(hwnd);
                    _chain.Add(hwnd);
                    return new AutoDecision(AutoAction.Switch, current, hwnd);
                }
                var match = AutoRecordMatcher.Match(rules, window);
                if (match is null)
                    return AutoDecision.Nothing;
                _handled.Add(hwnd);
                _chain.Clear();
                _chain.Add(hwnd);
                CurrentRule = match;
                return new AutoDecision(AutoAction.Start, match, hwnd);
        }
    }

    /// <summary>
    /// No se pudo pasar a <paramref name="hwnd"/> (cerrada, minimizada…): se olvida y se dice qué
    /// hacer ahora (volver a la anterior, parar o nada).
    /// </summary>
    public AutoDecision SwitchFailed(nint hwnd, IReadOnlyList<AutoRecordRule> rules, bool recording) =>
        OnEvent(WindowEventKind.Hidden, hwnd, null, rules, enabled: true, recording);

    /// <summary>La grabación automática no llegó a empezar o ya ha terminado (por lo que sea).</summary>
    public void Clear()
    {
        _chain.Clear();
        CurrentRule = null;
    }
}
