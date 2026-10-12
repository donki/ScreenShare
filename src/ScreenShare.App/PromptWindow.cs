using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SocScreenShare.App.Services;
using SocScreenShare.Localization;

namespace SocScreenShare.App;

/// <summary>
/// Los diálogos pequeños —aviso y confirmación— con el aspecto de la aplicación en vez
/// de los del sistema (constitución 24 y E.1). Se montan en código porque son variantes de lo mismo.
/// </summary>
public sealed class PromptWindow : Window
{

    private PromptWindow(Window? owner, string title, string message, Kind kind)
    {
        if (owner is { IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
        }
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = Owner is null;
        Background = (Brush)FindResource("PageBackground");
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = message, Style = (Style)FindResource("BodyText") });
        var card = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18, 14, 18, 16), Child = stack };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button
        {
            Style = (Style)FindResource(kind == Kind.Danger ? "DangerIconButton" : "IconButton"),
            Content = kind switch { Kind.Danger => "", Kind.Confirm => "", _ => "" },
            ToolTip = Loc.Get(kind == Kind.Danger ? "Delete" : "Ok"),
            IsDefault = true,
            IsCancel = kind == Kind.Alert,
        };
        AutomationProperties.SetAutomationId(ok, "OkButton");
        AutomationProperties.SetName(ok, Loc.Get(kind == Kind.Danger ? "Delete" : "Ok"));
        ok.Click += (_, _) => Accept();
        if (kind != Kind.Alert)
        {
            var cancel = new Button { Style = (Style)FindResource("GhostIconButton"), Content = "", ToolTip = Loc.Get("Cancel"), IsCancel = true };
            AutomationProperties.SetAutomationId(cancel, "CancelButton");
            AutomationProperties.SetName(cancel, Loc.Get("Cancel"));
            buttons.Children.Add(cancel);
        }
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(card);
        root.Children.Add(buttons);
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; };
    }

    private enum Kind
    {
        Alert,
        Confirm,
        Danger,
    }

    private void Accept() => DialogResult = true;

    public static void Alert(Window? owner, string title, string message) =>
        new PromptWindow(owner, title, message, Kind.Alert).ShowDialog();

    public static bool Confirm(Window? owner, string title, string message, bool danger) =>
        new PromptWindow(owner, title, message, danger ? Kind.Danger : Kind.Confirm).ShowDialog() == true;
}
