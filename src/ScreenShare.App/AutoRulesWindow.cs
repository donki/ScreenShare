using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using SocScreenShare.App.Services;
using SocScreenShare.AutoRecord;
using SocScreenShare.Engine;
using SocScreenShare.Localization;
using SocScreenShare.Settings;

namespace SocScreenShare.App;

/// <summary>
/// Las reglas de la grabación automática: cada una con su aplicación (de las abiertas o de un .exe),
/// un filtro opcional por título (contiene o expresión regular), su interruptor y si para al
/// cerrarse la ventana. Se trabaja sobre una copia y se guarda al pulsar el botón de guardar; lo que
/// no vale se avisa y no se guarda (General §6.8).
/// </summary>
public sealed class AutoRulesWindow : Window
{
    private readonly ObservableCollection<RuleRow> _rows;
    private readonly ListBox _list = new();
    private readonly TextBox _app = new();
    private readonly TextBox _title = new();
    private readonly ComboBox _mode = new();
    private readonly CheckBox _stopOnClose = new();
    private readonly CheckBox _enabled = new();
    private readonly StackPanel _editor = new();
    private readonly TextBlock _empty = new();
    private bool _loading;

    /// <summary>Una regla en la lista (copia de trabajo).</summary>
    public sealed class RuleRow(AutoRecordRule rule) : System.ComponentModel.INotifyPropertyChanged
    {
        public AutoRecordRule Rule { get; } = rule;

        public bool Enabled
        {
            get => Rule.Enabled;
            set { Rule.Enabled = value; Changed(nameof(Enabled)); }
        }

        public string Text => (Rule.ProcessName.Length > 0 ? Rule.ProcessName : Loc.Get("RuleNoApp"))
            + (string.IsNullOrEmpty(Rule.Title) ? string.Empty : " · " + (Rule.TitleMode == TitleMatch.Regex ? "/" + Rule.Title + "/" : "«" + Rule.Title + "»"));

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public void Refresh() => Changed(nameof(Text));

        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    }

    public AutoRulesWindow()
    {
        Title = Loc.Get("AutoRulesTitle");
        Width = 640;
        Height = 620;
        MinWidth = 480;
        MinHeight = 480;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("PageBackground");
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);

        _rows = new(App.Settings.AutoRules.Select(r => new RuleRow(r.Clone())));

        // ---- lista
        _list.Style = (Style)FindResource("PickList");
        _list.ItemContainerStyle = (Style)FindResource("PickItem");
        _list.Height = 170;
        _list.ItemsSource = _rows;
        _list.ItemTemplate = RowTemplate();
        AutomationProperties.SetAutomationId(_list, "RuleList");
        _list.SelectionChanged += (_, _) => ShowSelected();

        var add = IconButton("", "RuleAdd", "AddRuleButton", "IconButton");
        add.Click += (_, _) =>
        {
            var row = new RuleRow(new AutoRecordRule());
            _rows.Add(row);
            _list.SelectedItem = row;
            _app.Focus();
        };
        var delete = IconButton("", "RuleDelete", "DeleteRuleButton", "GhostDangerIconButton");
        delete.Click += (_, _) =>
        {
            if (_list.SelectedItem is RuleRow row && PromptWindow.Confirm(this, Loc.Get("ConfirmTitle"), Loc.Get("RuleDeleteConfirm"), danger: true))
                _rows.Remove(row);
        };
        var listHeader = new Grid();
        listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        listHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        listHeader.Children.Add(new TextBlock { Text = Loc.Get("AutoRulesHint"), Style = (Style)FindResource("HintText"), VerticalAlignment = VerticalAlignment.Center });
        var listButtons = new StackPanel { Orientation = Orientation.Horizontal };
        listButtons.Children.Add(add);
        listButtons.Children.Add(delete);
        Grid.SetColumn(listButtons, 1);
        listHeader.Children.Add(listButtons);

        // ---- editor de la regla elegida
        _app.Style = (Style)FindResource("Field");
        AutomationProperties.SetAutomationId(_app, "RuleAppBox");
        _app.LostFocus += (_, _) => Apply();
        var pick = IconButton("", "RulePickApp", "PickAppButton", "GhostIconButton");
        pick.Click += (_, _) => PickFromOpenApps(pick);
        var browse = IconButton("", "RuleBrowseExe", "BrowseExeButton", "GhostIconButton");
        browse.Click += (_, _) => BrowseExe();
        var appRow = new Grid();
        appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        appRow.Children.Add(_app);
        Grid.SetColumn(pick, 1);
        Grid.SetColumn(browse, 2);
        appRow.Children.Add(pick);
        appRow.Children.Add(browse);

        _title.Style = (Style)FindResource("Field");
        AutomationProperties.SetAutomationId(_title, "RuleTitleBox");
        _title.LostFocus += (_, _) => Apply();
        _mode.Style = (Style)FindResource("Combo");
        _mode.HorizontalAlignment = HorizontalAlignment.Stretch;
        _mode.MinWidth = 0;
        _mode.DisplayMemberPath = "Text";
        _mode.ItemsSource = new[]
        {
            new Choice<TitleMatch>(TitleMatch.Contains, Loc.Get("RuleContains")),
            new Choice<TitleMatch>(TitleMatch.Regex, Loc.Get("RuleRegex")),
        };
        AutomationProperties.SetAutomationId(_mode, "RuleModeCombo");
        _mode.SelectionChanged += (_, _) => Apply();
        _enabled.Style = (Style)FindResource("Check");
        _enabled.Content = Loc.Get("RuleEnabled");
        AutomationProperties.SetAutomationId(_enabled, "RuleEnabledCheck");
        _enabled.Click += (_, _) => Apply();
        _stopOnClose.Style = (Style)FindResource("Check");
        _stopOnClose.Content = Loc.Get("RuleStopOnClose");
        AutomationProperties.SetAutomationId(_stopOnClose, "RuleStopOnCloseCheck");
        _stopOnClose.Click += (_, _) => Apply();

        _editor.Children.Add(Label("RuleApp"));
        _editor.Children.Add(appRow);
        _editor.Children.Add(Label("RuleTitle"));
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        titleRow.Children.Add(_title);
        Grid.SetColumn(_mode, 2);
        titleRow.Children.Add(_mode);
        _editor.Children.Add(titleRow);
        _editor.Children.Add(new TextBlock { Text = Loc.Get("RuleTitleHint"), Style = (Style)FindResource("HintText"), Margin = new Thickness(0, 4, 0, 0) });
        _editor.Children.Add(_enabled);
        _editor.Children.Add(_stopOnClose);
        _empty.Text = Loc.Get("AutoNoRules");
        _empty.Style = (Style)FindResource("HintText");

        var card = new StackPanel();
        card.Children.Add(listHeader);
        card.Children.Add(new Border { Height = 8 });
        card.Children.Add(_list);
        card.Children.Add(new Border { Height = 12 });
        card.Children.Add(_empty);
        card.Children.Add(_editor);

        // ---- botones fijos abajo
        var save = IconButton("", "Save", "SaveButton", "IconButton");
        save.IsDefault = false;
        save.Click += (_, _) => SaveAndClose();
        var cancel = IconButton("", "Cancel", "CancelButton", "GhostIconButton");
        cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18, 14, 18, 16), Child = card },
        });
        Content = root;
        _list.SelectedIndex = _rows.Count > 0 ? 0 : -1;
        ShowSelected();
    }

    private DataTemplate RowTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(StackPanel));
        factory.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(RuleRow.Enabled)) { Mode = BindingMode.TwoWay });
        check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        check.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        check.SetValue(AutomationProperties.NameProperty, Loc.Get("RuleEnabled"));
        factory.AppendChild(check);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(RuleRow.Text)));
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.AppendChild(text);
        return new DataTemplate { VisualTree = factory };
    }

    private TextBlock Label(string key) => new() { Text = Loc.Get(key), Style = (Style)FindResource("FieldLabel") };

    private Button IconButton(string glyph, string tipKey, string id, string style)
    {
        var b = new Button { Style = (Style)FindResource(style), Content = glyph, ToolTip = Loc.Get(tipKey) };
        AutomationProperties.SetAutomationId(b, id);
        AutomationProperties.SetName(b, Loc.Get(tipKey));
        return b;
    }

    private void ShowSelected()
    {
        _loading = true;
        var row = _list.SelectedItem as RuleRow;
        _editor.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (row is not null)
        {
            _app.Text = row.Rule.ProcessName;
            _title.Text = row.Rule.Title ?? string.Empty;
            _mode.SelectedIndex = row.Rule.TitleMode == TitleMatch.Regex ? 1 : 0;
            _enabled.IsChecked = row.Rule.Enabled;
            _stopOnClose.IsChecked = row.Rule.StopOnClose;
        }
        _loading = false;
    }

    /// <summary>Lo de las casillas, a la regla elegida (al salir de cada casilla, General §6.8).</summary>
    private void Apply()
    {
        if (_loading || _list.SelectedItem is not RuleRow row)
            return;
        row.Rule.ProcessName = AutoRecordRule.NormalizeProcess(_app.Text);
        row.Rule.Title = string.IsNullOrWhiteSpace(_title.Text) ? null : _title.Text;
        row.Rule.TitleMode = _mode.SelectedIndex == 1 ? TitleMatch.Regex : TitleMatch.Contains;
        row.Enabled = _enabled.IsChecked == true;
        row.Rule.StopOnClose = _stopOnClose.IsChecked == true;
        row.Refresh();
    }

    private void PickFromOpenApps(Button anchor)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("TrayMenu"), PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var w in Devices.RecordableWindows().GroupBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            var window = w;
            menu.Items.Add(TrayIcon.Item($"{window.ProcessName} — {window.Title}", "", () =>
            {
                _app.Text = window.ProcessName;
                if (string.IsNullOrWhiteSpace(_title.Text))
                    _title.Text = string.Empty;
                Apply();
            }));
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(TrayIcon.Item(Loc.Get("RuleNoOpenApps"), "", () => { }, enabled: false));
        menu.IsOpen = true;
    }

    private void BrowseExe()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Loc.Get("ExeFilter") + "|*.exe", Title = Loc.Get("RuleBrowseExe") };
        if (dialog.ShowDialog(this) == true)
        {
            _app.Text = AutoRecordRule.NormalizeProcess(dialog.FileName);
            Apply();
        }
    }

    private void SaveAndClose()
    {
        Apply();
        foreach (var row in _rows)
        {
            if (row.Rule.Validate() is { } error)
            {
                _list.SelectedItem = row;
                PromptWindow.Alert(this, Loc.Get("ErrorTitle"), Loc.Get(error));
                return;
            }
        }
        App.Settings.AutoRules = [.. _rows.Select(r => r.Rule)];
        if (!App.Settings.Save())
        {
            PromptWindow.Alert(this, Loc.Get("ErrorTitle"), Loc.Format("ErrSaveSettings", AppLog.FilePath));
            return;
        }
        DialogResult = true;
    }
}
