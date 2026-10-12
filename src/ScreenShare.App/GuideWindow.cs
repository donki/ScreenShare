using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SocScreenShare.App.Services;
using SocScreenShare.Localization;
using SocScreenShare.Settings;

namespace SocScreenShare.App;

/// <summary>
/// Guía de configuración paso a paso (General §6.10): carpeta de destino, sonido de Windows, permiso
/// del micrófono, atajos y arranque con Windows. Cada paso explica qué hace, tiene un botón que lo
/// hace o abre la pantalla donde se hace, y su estado se vuelve a mirar al volver a la ventana. Sale
/// sola la primera vez y luego desde el botón de ayuda; los botones de avanzar van fijos abajo.
/// </summary>
public sealed class GuideWindow : Window
{
    public enum StepState
    {
        Done,
        Pending,
        Optional,
    }

    private sealed record Step(string Title, string Text, string ActionKey, string Glyph, Func<StepState> State, Action Action);

    private readonly List<Step> _steps;
    private readonly TextBlock _counter = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _text = new();
    private readonly TextBlock _state = new();
    private readonly Button _action;
    private readonly Button _back;
    private readonly Button _next;
    private int _index;

    public GuideWindow()
    {
        Title = Loc.Get("GuideTitle");
        Width = 560;
        Height = 460;
        MinWidth = 440;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("PageBackground");
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);

        var s = App.Settings;
        _steps =
        [
            new(Loc.Get("GuideFolderTitle"), Loc.Get("GuideFolderText"), "BrowseFolder", "",
                () => RecorderSettings.CheckFolder(s.OutputFolder) is null ? StepState.Done : StepState.Pending,
                () =>
                {
                    var d = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("BrowseFolder") };
                    if (d.ShowDialog(this) == true && RecorderSettings.CheckFolder(d.FolderName) is null)
                    {
                        s.OutputFolder = d.FolderName;
                        s.Save();
                    }
                }),
            new(Loc.Get("GuideSoundTitle"), Loc.Get("GuideSoundText"), "GuideSoundAction", "",
                () => StepState.Optional,
                () => OpenSettings("ms-settings:sound")),
            new(Loc.Get("GuideMicTitle"), Loc.Get("GuideMicText"), "GuideMicAction", "",
                () => !s.MicEnabled ? StepState.Optional : MicrophoneAllowed() ? StepState.Done : StepState.Pending,
                () => OpenSettings("ms-settings:privacy-microphone")),
            new(Loc.Get("GuideAutoTitle"), Loc.Get("GuideAutoText"), "GuideAutoAction", "",
                () => WindowsStartup.IsAvailable && WindowsStartup.IsEnabled() ? StepState.Done : StepState.Optional,
                () =>
                {
                    if (!WindowsStartup.IsAvailable)
                        PromptWindow.Alert(this, Loc.Get("GuideAutoTitle"), Loc.Get("StartupUnavailable"));
                    else
                        WindowsStartup.Set(!WindowsStartup.IsEnabled());
                }),
        ];

        _counter.Style = (Style)FindResource("HintText");
        _title.Style = (Style)FindResource("CardTitle");
        _title.Margin = new Thickness(0, 6, 0, 8);
        _text.Style = (Style)FindResource("BodyText");
        _state.Style = (Style)FindResource("HintText");
        _state.Margin = new Thickness(0, 12, 0, 0);
        _state.FontWeight = FontWeights.SemiBold;
        _action = new Button { Style = (Style)FindResource("OutlineButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        AutomationProperties.SetAutomationId(_action, "GuideActionButton");
        _action.Click += (_, _) =>
        {
            _steps[_index].Action();
            Show(_index);
        };

        var card = new StackPanel();
        card.Children.Add(_counter);
        card.Children.Add(_title);
        card.Children.Add(_text);
        card.Children.Add(_action);
        card.Children.Add(_state);

        _back = Nav("", "GuideBack", "GuideBackButton", () => Show(_index - 1));
        _next = Nav("", "GuideNext", "GuideNextButton", () => { if (_index < _steps.Count - 1) Show(_index + 1); else Close(); });
        var close = Nav("", "Close", "CloseButton", Close);
        close.Style = (Style)FindResource("GhostIconButton");
        _back.Style = (Style)FindResource("GhostIconButton");

        var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(close);
        close.HorizontalAlignment = HorizontalAlignment.Left;
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(_back);
        right.Children.Add(_next);
        buttons.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(20, 16, 20, 18), Child = card },
        });
        Content = root;
        Show(0);
        Activated += (_, _) => Show(_index);
    }

    /// <summary>
    /// ¿Deja Windows a las aplicaciones de escritorio usar el micrófono? (Configuración → Privacidad →
    /// Micrófono). Sin la clave, se da por permitido, que es lo de fábrica.
    /// </summary>
    public static bool MicrophoneAllowed()
    {
        try
        {
            const string key = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            using var all = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
            using var desktop = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key + @"\NonPackaged");
            return !string.Equals(all?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(desktop?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void OpenSettings(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            AppLog.Write($"abrir {uri}: {ex.Message}");
            PromptWindow.Alert(this, Loc.Get("ErrorTitle"), Loc.Format("ErrOpen", uri));
        }
    }

    private Button Nav(string glyph, string tipKey, string id, Action click)
    {
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = glyph, ToolTip = Loc.Get(tipKey) };
        AutomationProperties.SetAutomationId(b, id);
        AutomationProperties.SetName(b, Loc.Get(tipKey));
        b.Click += (_, _) => click();
        return b;
    }

    private void Show(int index)
    {
        _index = Math.Clamp(index, 0, _steps.Count - 1);
        var step = _steps[_index];
        _counter.Text = Loc.Format("GuideStep", _index + 1, _steps.Count);
        _title.Text = step.Title;
        _text.Text = step.Text;
        _action.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock { Text = step.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                new TextBlock { Text = Loc.Get(step.ActionKey), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        AutomationProperties.SetName(_action, Loc.Get(step.ActionKey));
        StepState state;
        try { state = step.State(); }
        catch (Exception) { state = StepState.Pending; }
        _state.Text = Loc.Get(state switch { StepState.Done => "GuideDone", StepState.Optional => "GuideOptional", _ => "GuidePending" });
        _state.Foreground = (Brush)FindResource(state switch { StepState.Done => "Success", StepState.Optional => "TextSecondary", _ => "Warning" });
        _back.IsEnabled = _index > 0;
        _next.Content = _index == _steps.Count - 1 ? "" : "";
        _next.ToolTip = Loc.Get(_index == _steps.Count - 1 ? "GuideFinish" : "GuideNext");
    }
}
