using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SocScreenShare.App.Services;
using SocScreenShare.Engine;
using SocScreenShare.Localization;
using SocScreenShare.Recording;

namespace SocScreenShare.App;

/// <summary>
/// Calidad de salida: vídeo (resolución máxima, imágenes por segundo, códec, control y tasa de
/// bits) y audio (tasa AAC, frecuencia y canales). Los atajos «Óptima para compartir» y «Alta»
/// rellenan las opciones; tocar cualquiera las vuelve «Personalizada». Se trabaja sobre una copia,
/// con el tamaño estimado al momento, y se guarda con el botón de guardar.
/// </summary>
public sealed class QualityWindow : Window
{
    private readonly QualityOptions _q;
    private readonly Func<(int Width, int Height)?> _source;
    private readonly bool _hevc = EncoderCatalog.HevcAvailable;
    private readonly ComboBox _preset = new();
    private readonly ComboBox _resolution = new();
    private readonly ComboBox _fps = new();
    private readonly ComboBox _codec = new();
    private readonly ComboBox _rate = new();
    private readonly CheckBox _auto = new();
    private readonly TextBox _mbps = new();
    private readonly Slider _quality = new();
    private readonly TextBlock _qualityText = new();
    private readonly Grid _mbpsRow = new();
    private readonly Grid _qualityRow = new();
    private readonly ComboBox _audioKbps = new();
    private readonly ComboBox _sampleRate = new();
    private readonly ComboBox _channels = new();
    private readonly TextBlock _estimate = new();
    private bool _loading;

    /// <param name="current">Las opciones de ahora (no se tocan hasta guardar).</param>
    /// <param name="source">Tamaño de lo que se va a grabar, para la estimación (null si no se sabe).</param>
    public QualityWindow(QualityOptions current, Func<(int Width, int Height)?> source)
    {
        _q = current.Clone().Normalize();
        _source = source;
        Title = Loc.Get("QualityTitle");
        Width = 560;
        Height = 760;
        MinWidth = 460;
        MinHeight = 520;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)FindResource("PageBackground");
        SourceInitialized += (_, _) => ThemeManager.ApplyToWindow(this);

        Combo(_preset, "QualityPresetCombo", [
            (QualityPreset.Share, Loc.Get("QualityShare"), true),
            (QualityPreset.High, Loc.Get("QualityHigh"), true),
            (QualityPreset.Custom, Loc.Get("QualityCustom"), true)]);
        Combo(_resolution, "ResolutionCombo", [
            (MaxResolution.Original, Loc.Get("ResOriginal"), true),
            (MaxResolution.P2160, "2160p (4K)", true),
            (MaxResolution.P1440, "1440p", true),
            (MaxResolution.P1080, "1080p (Full HD)", true),
            (MaxResolution.P720, "720p (HD)", true),
            (MaxResolution.P480, "480p", true)]);
        Combo(_fps, "FpsCombo", [.. QualityOptions.FpsChoices.Select(f => ((object)f, Loc.Format("FpsItem", f), true))]);
        Combo(_codec, "CodecCombo", [
            (VideoCodec.H264, Loc.Get("CodecH264"), true),
            (VideoCodec.Hevc, Loc.Get(_hevc ? "CodecHevc" : "CodecHevcMissing"), _hevc)]);
        Combo(_rate, "RateCombo", [
            (RateControl.Vbr, Loc.Get("RateVbr"), true),
            (RateControl.Cbr, Loc.Get("RateCbr"), true),
            (RateControl.Quality, Loc.Get("RateQuality"), true)]);
        Combo(_audioKbps, "AudioKbpsCombo", [.. QualityOptions.AudioKbpsChoices.Select(k =>
            ((object)k, QualityOptions.IsAacSupported(k) ? Loc.Format("KbpsItem", k) : Loc.Format("KbpsUnsupported", k), QualityOptions.IsAacSupported(k)))]);
        Combo(_sampleRate, "SampleRateCombo", [(44100, "44,1 kHz", true), (48000, "48 kHz", true)]);
        Combo(_channels, "ChannelsCombo", [(1, Loc.Get("ChannelsMono"), true), (2, Loc.Get("ChannelsStereo"), true)]);

        _auto.Style = (Style)FindResource("Check");
        _auto.Content = Loc.Get("BitrateAuto");
        AutomationProperties.SetAutomationId(_auto, "AutoBitrateCheck");
        _auto.Click += (_, _) => Changed();
        _mbps.Style = (Style)FindResource("Field");
        AutomationProperties.SetAutomationId(_mbps, "MbpsBox");
        AutomationProperties.SetName(_mbps, Loc.Get("BitrateMbps"));
        _mbps.LostFocus += (_, _) => ApplyMbps();
        _mbps.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyMbps(); };
        _quality.Style = (Style)FindResource("Volume");
        AutomationProperties.SetAutomationId(_quality, "QualityLevelSlider");
        _quality.ValueChanged += (_, _) => Changed();
        _qualityText.Style = (Style)FindResource("HintText");
        _qualityText.TextAlignment = TextAlignment.Right;
        _qualityText.VerticalAlignment = VerticalAlignment.Center;
        _estimate.Style = (Style)FindResource("BodyText");
        _estimate.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetAutomationId(_estimate, "QualityEstimateText");

        Row(_mbpsRow, Label("BitrateMbps", top: 0), _mbps);
        _qualityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _qualityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        _qualityRow.Children.Add(_quality);
        Grid.SetColumn(_qualityText, 1);
        _qualityRow.Children.Add(_qualityText);

        var video = new StackPanel();
        video.Children.Add(new TextBlock { Text = Loc.Get("QualityVideo"), Style = (Style)FindResource("CardTitle") });
        video.Children.Add(Label("ResolutionLabel"));
        video.Children.Add(_resolution);
        video.Children.Add(Label("FpsLabel"));
        video.Children.Add(_fps);
        video.Children.Add(Label("CodecLabel"));
        video.Children.Add(_codec);
        if (!_hevc)
            video.Children.Add(Hint("CodecHevcHint"));
        video.Children.Add(Label("RateLabel"));
        video.Children.Add(_rate);
        video.Children.Add(Hint("RateHint"));
        video.Children.Add(_auto);
        video.Children.Add(_mbpsRow);
        video.Children.Add(Label("QualityLevelLabel"));
        video.Children.Add(_qualityRow);

        var audio = new StackPanel();
        audio.Children.Add(new TextBlock { Text = Loc.Get("QualityAudio"), Style = (Style)FindResource("CardTitle") });
        audio.Children.Add(Label("AudioKbpsLabel"));
        audio.Children.Add(_audioKbps);
        audio.Children.Add(Hint("AudioKbpsHint"));
        audio.Children.Add(Label("SampleRateLabel"));
        audio.Children.Add(_sampleRate);
        audio.Children.Add(Label("ChannelsLabel"));
        audio.Children.Add(_channels);

        var list = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        list.Children.Add(Card(new StackPanel { Children = { Label("QualityPresetLabel", top: 0), _preset, Hint("QualityPresetHint") } }));
        list.Children.Add(Card(video));
        list.Children.Add(Card(audio));

        var save = IconButton("", "Save", "SaveButton", "IconButton");
        save.Click += (_, _) => { ApplyMbps(); DialogResult = true; };
        var cancel = IconButton("", "Cancel", "CancelButton", "GhostIconButton");
        cancel.IsCancel = true;
        var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _estimate.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(_estimate);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetColumn(buttons, 1);
        bottom.Children.Add(buttons);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
        Content = root;

        _preset.SelectionChanged += (_, _) => OnPreset();
        foreach (var c in new[] { _resolution, _fps, _codec, _rate, _audioKbps, _sampleRate, _channels })
            c.SelectionChanged += (_, _) => Changed();
        Show(_q);
    }

    /// <summary>Lo elegido (válido solo si el diálogo devuelve true).</summary>
    public QualityOptions Result => _q.Clone().Normalize();

    private void OnPreset()
    {
        if (_loading || Selected<QualityPreset>(_preset) is not { } p || p == QualityPreset.Custom)
            return;
        Show(QualityOptions.ForPreset(p));
    }

    /// <summary>Pinta las opciones en los controles sin disparar cambios.</summary>
    private void Show(QualityOptions q)
    {
        _loading = true;
        Copy(q, _q);
        Select(_preset, _q.Detect());
        Select(_resolution, _q.Resolution);
        Select(_fps, _q.Fps);
        Select(_codec, _q.Codec);
        Select(_rate, _q.RateControl);
        Select(_audioKbps, _q.AudioKbps);
        Select(_sampleRate, _q.AudioSampleRate);
        Select(_channels, _q.AudioChannels);
        _auto.IsChecked = _q.AutoBitrate;
        _mbps.Text = _q.VideoMbps.ToString("0.#", CultureInfo.CurrentCulture);
        _quality.Minimum = 0;
        _quality.Maximum = 100;
        _quality.Value = _q.QualityLevel;
        _loading = false;
        Refresh();
    }

    /// <summary>Un control ha cambiado: a la copia y, si ya no es un atajo, «Personalizada».</summary>
    private void Changed()
    {
        if (_loading)
            return;
        if (Selected<MaxResolution>(_resolution) is { } r) _q.Resolution = r;
        if (Selected<int>(_fps) is { } f) _q.Fps = f;
        if (Selected<VideoCodec>(_codec) is { } c) _q.Codec = c;
        if (Selected<RateControl>(_rate) is { } rc) _q.RateControl = rc;
        if (Selected<int>(_audioKbps) is { } k) _q.AudioKbps = k;
        if (Selected<int>(_sampleRate) is { } sr) _q.AudioSampleRate = sr;
        if (Selected<int>(_channels) is { } ch) _q.AudioChannels = ch;
        _q.AutoBitrate = _auto.IsChecked == true;
        _q.QualityLevel = (int)Math.Round(_quality.Value);
        _loading = true;
        Select(_preset, _q.Detect());
        _loading = false;
        Refresh();
    }

    /// <summary>Los Mbps escritos se aplican al salir de la casilla o con Intro; si no valen, se avisa (General §6.8).</summary>
    private void ApplyMbps()
    {
        if (_q.AutoBitrate || _q.RateControl == RateControl.Quality)
            return;
        var text = _mbps.Text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < QualityOptions.MinMbps || v > QualityOptions.MaxMbps)
        {
            PromptWindow.Alert(this, Loc.Get("ErrorTitle"), Loc.Format("ErrMbps", QualityOptions.MinMbps, QualityOptions.MaxMbps));
            _mbps.Text = _q.VideoMbps.ToString("0.#", CultureInfo.CurrentCulture);
            return;
        }
        _q.VideoMbps = Math.Round(v, 1);
        Changed();
    }

    private void Refresh()
    {
        var quality = _q.RateControl == RateControl.Quality;
        _rate.IsEnabled = true;
        _auto.IsEnabled = !quality;
        _mbpsRow.IsEnabled = !quality && !_q.AutoBitrate;
        _qualityRow.IsEnabled = quality;
        _qualityText.Text = _q.QualityLevel.ToString(CultureInfo.CurrentCulture);
        var (w, h) = _source() ?? (1920, 1080);
        var effective = _q.Clone();
        effective.Codec = _q.EffectiveCodec(_hevc);
        var plan = Presets.For(effective, w, h);
        var mb = plan.BytesPerMinute(withAudio: true) / 1024.0 / 1024.0;
        _estimate.Text = Loc.Format(quality ? "QualityEstimateApprox" : "QualityEstimate", plan.Width, plan.Height, plan.Fps,
            (plan.VideoBitrate / 1_000_000.0).ToString("0.0", CultureInfo.CurrentCulture), mb.ToString("0", CultureInfo.CurrentCulture));
        if (_q.AutoBitrate && !_mbps.IsKeyboardFocusWithin)
            _mbps.Text = (plan.VideoBitrate / 1_000_000.0).ToString("0.0", CultureInfo.CurrentCulture);
    }

    // ------------------------------------------------------------------ piezas

    private static void Copy(QualityOptions from, QualityOptions to)
    {
        to.Resolution = from.Resolution;
        to.Fps = from.Fps;
        to.AutoBitrate = from.AutoBitrate;
        to.VideoMbps = from.VideoMbps;
        to.Codec = from.Codec;
        to.RateControl = from.RateControl;
        to.QualityLevel = from.QualityLevel;
        to.AudioKbps = from.AudioKbps;
        to.AudioSampleRate = from.AudioSampleRate;
        to.AudioChannels = from.AudioChannels;
    }

    private void Combo(ComboBox combo, string id, (object Value, string Text, bool Enabled)[] items)
    {
        combo.Style = (Style)FindResource("Combo");
        combo.HorizontalAlignment = HorizontalAlignment.Stretch;
        combo.MinWidth = 0;
        AutomationProperties.SetAutomationId(combo, id);
        foreach (var (value, text, enabled) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = value, IsEnabled = enabled };
            AutomationProperties.SetName(item, text);
            combo.Items.Add(item);
        }
    }

    private static T? Selected<T>(ComboBox combo) where T : struct => (combo.SelectedItem as ComboBoxItem)?.Tag is T v ? v : null;

    private static void Select(ComboBox combo, object value) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value));

    private static void Row(Grid grid, UIElement label, UIElement field)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(label);
        Grid.SetRow(field, 1);
        grid.Children.Add(field);
    }

    private TextBlock Label(string key, double top = 10) =>
        new() { Text = Loc.Get(key), Style = (Style)FindResource("FieldLabel"), Margin = new Thickness(0, top, 0, 4) };

    private TextBlock Hint(string key) => new() { Text = Loc.Get(key), Style = (Style)FindResource("HintText"), Margin = new Thickness(0, 4, 0, 0) };

    private Border Card(UIElement child) =>
        new() { Style = (Style)FindResource("Card"), Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 0, 0, 10), Child = child };

    private Button IconButton(string glyph, string tipKey, string id, string style)
    {
        var b = new Button { Style = (Style)FindResource(style), Content = glyph, ToolTip = Loc.Get(tipKey) };
        AutomationProperties.SetAutomationId(b, id);
        AutomationProperties.SetName(b, Loc.Get(tipKey));
        return b;
    }
}
