using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;

namespace SocScreenShare.UITests;

/// <summary>
/// Recorridos de la interfaz sobre el exe Debug en modo aislado (General §8.7). Cada prueba arranca su
/// instancia con una carpeta propia; la grabación es real (la pantalla, unos segundos y sin sonido) y
/// el vídeo va a esa carpeta, que se borra.
/// </summary>
public sealed class MainWindowTests
{
    [Fact]
    public void Arranca_en_modo_aislado_con_los_mandos_y_los_atajos_a_la_vista()
    {
        using var app = ScsApp.Launch();
        Assert.Contains("[SOC_SANDBOX]", app.Main.Title);
        Assert.True(app.Button(app.Main, "RecordButton").IsEnabled);
        Assert.False(app.Button(app.Main, "PauseButton").IsEnabled);
        Assert.False(app.Button(app.Main, "StopButton").IsEnabled);
        Assert.Equal("Ctrl+Mayús+F9", app.ById(app.Main, "RecordHotkeyText").Name);
        Assert.Equal("Ctrl+Mayús+F10", app.ById(app.Main, "StopHotkeyText").Name);
        Assert.False(app.ById(app.Main, "StartupCheck").IsEnabled);   // Debug: nunca arranca con Windows
        app.Capture(app.Main, "principal");
    }

    [Fact]
    public void Elegir_que_grabar_cambia_el_selector_y_el_sonido()
    {
        using var app = ScsApp.Launch();
        Assert.NotNull(app.ById(app.Main, "Monitor1"));
        ScsApp.Select(app.ById(app.Main, "WindowTile"));
        Assert.True(Retry.WhileFalse(() => !app.ById(app.Main, "WindowList").IsOffscreen, ScsApp.Timeout).Result, "no sale la lista de ventanas");
        Assert.False(app.ById(app.Main, "WindowAudioCheck").IsOffscreen);   // al grabar una ventana: su sonido
        Assert.Equal(ToggleState.On, app.ById(app.Main, "WindowAudioCheck").AsCheckBox().ToggleState);
        app.Capture(app.Main, "ventana");
        ScsApp.Select(app.ById(app.Main, "AllTile"));
        Assert.True(Retry.WhileFalse(() => app.Settings().Contains("\"target\": \"allMonitors\""), ScsApp.Timeout).Result);
        ScsApp.Select(app.ById(app.Main, "MonitorTile"));
        Assert.True(Retry.WhileFalse(() => app.Settings().Contains("\"target\": \"monitor\""), ScsApp.Timeout).Result);
    }

    [Fact]
    public void Grabar_dos_segundos_parar_y_ver_la_ultima_grabacion()
    {
        using var app = ScsApp.Launch();
        ScsApp.Press(app.Button(app.Main, "RecordButton"));
        Assert.True(Retry.WhileFalse(() => app.Button(app.Main, "StopButton").IsEnabled, ScsApp.Timeout).Result, "no empieza a grabar");
        Assert.False(app.Button(app.Main, "RecordButton").IsEnabled);
        ScsApp.Press(app.Button(app.Main, "PauseButton"));
        Thread.Sleep(300);
        ScsApp.Press(app.Button(app.Main, "PauseButton"));   // seguir
        Thread.Sleep(2000);
        app.Capture(app.Main, "grabando");
        ScsApp.Press(app.Button(app.Main, "StopButton"));
        var name = Retry.WhileNull(() => app.TryById(app.Main, "LastNameText") is { IsOffscreen: false } e ? e.Name : null, ScsApp.Timeout).Result;
        Assert.False(string.IsNullOrEmpty(name), "no sale la última grabación");
        var file = Path.Combine(app.VideosFolder, name!);
        Assert.True(File.Exists(file));
        Assert.True(new FileInfo(file).Length > 10_000);
        Assert.True(app.Button(app.Main, "RecordButton").IsEnabled);
        app.Capture(app.Main, "guardada");
    }

    [Fact]
    public void Idioma_cambia_al_momento()
    {
        using var app = ScsApp.Launch();
        Assert.Equal("Grabar", app.Button(app.Main, "RecordButton").Name);
        ScsApp.Press(app.Button(app.Main, "LanguageButton"));
        Assert.True(Retry.WhileFalse(() => app.CurrentMain().FindFirstDescendant(app.Cf.ByAutomationId("RecordButton"))?.Name == "Record", ScsApp.Timeout).Result);
        Assert.Contains("\"language\": \"en\"", app.Settings());
        app.Capture(app.CurrentMain(), "ingles");
    }

    [Fact]
    public void Acerca_de_y_novedades()
    {
        using var app = ScsApp.Launch();
        ScsApp.Press(app.Button(app.Main, "AboutButton"));
        var about = app.WaitModal();
        Assert.Contains("v", app.ById(about, "VersionLabel").Name);
        app.Capture(about, "acerca-de");
        ScsApp.Press(app.Button(about, "NewsButton"));
        var news = app.WaitModal(about);
        app.Capture(news, "novedades");
        ScsApp.Press(app.Button(news, "CloseButton"));
        app.WaitNoModal(about);
        ScsApp.Press(app.Button(about, "CloseButton"));
        app.WaitNoModal();
    }

    [Fact]
    public void La_guia_sale_la_primera_vez_y_se_recorre()
    {
        using var app = ScsApp.Launch("""{ "language": "es", "systemAudio": "none" }""");
        var guide = app.WaitModal();
        for (var i = 0; i < 4; i++)
        {
            app.Capture(guide, $"paso-{i + 1}");
            ScsApp.Press(app.Button(guide, "GuideNextButton"));
        }
        app.WaitNoModal();
        Assert.Contains("\"guideShown\": true", app.Settings());
    }

    [Fact]
    public void Regla_de_grabacion_automatica_se_crea_y_se_guarda()
    {
        using var app = ScsApp.Launch();
        ScsApp.Press(app.Button(app.Main, "AutoRulesButton"));
        var rules = app.WaitModal();
        ScsApp.Press(app.Button(rules, "AddRuleButton"));
        app.ById(rules, "RuleAppBox").AsTextBox().Text = "Teams.exe";
        app.ById(rules, "RuleTitleBox").AsTextBox().Text = "Reunión";
        app.Capture(rules, "regla");
        ScsApp.Press(app.Button(rules, "SaveButton"));
        app.WaitNoModal();
        var s = app.Settings();
        Assert.Contains("\"processName\": \"Teams\"", s);
        Assert.Contains("\"title\": \"Reunión\"", s);
        Assert.Contains("1 de 1", app.ById(app.Main, "AutoSummaryText").Name);
    }

    [Fact]
    public void Una_regla_sin_aplicacion_no_se_guarda_y_se_avisa()
    {
        using var app = ScsApp.Launch();
        ScsApp.Press(app.Button(app.Main, "AutoRulesButton"));
        var rules = app.WaitModal();
        ScsApp.Press(app.Button(rules, "AddRuleButton"));
        ScsApp.Press(app.Button(rules, "SaveButton"));
        var alert = app.WaitModal(rules);
        app.Capture(alert, "aviso");
        ScsApp.Press(app.Button(alert, "OkButton"));
        app.WaitNoModal(rules);
        ScsApp.Press(app.Button(rules, "CancelButton"));
        app.WaitNoModal();
        Assert.DoesNotContain("processName", app.Settings());
    }

    [Fact]
    public void Volver_a_los_atajos_de_siempre()
    {
        using var app = ScsApp.Launch("""{ "language": "es", "guideShown": true, "lastSeenVersion": "9999.0.0.0", "systemAudio": "none", "startHotkey": "Alt+F7", "stopHotkey": "Alt+F8" }""");
        Assert.Equal("Alt+F7", app.ById(app.Main, "StartHotkeyBox").AsTextBox().Text);
        ScsApp.Press(app.Button(app.Main, "ResetHotkeysButton"));
        Assert.True(Retry.WhileFalse(() => app.ById(app.Main, "StartHotkeyBox").AsTextBox().Text == "Ctrl+Mayús+F9", ScsApp.Timeout).Result);
        Assert.Contains("\"startHotkey\": \"Ctrl+Shift+F9\"", app.Settings());
    }

    [Fact]
    public void Calidad_personalizada_en_su_dialogo_sin_rehacer_la_ventana()
    {
        using var app = ScsApp.Launch();
        var mainHandle = app.Main.Properties.NativeWindowHandle.Value;
        var before = app.ById(app.Main, "EstimateText").Name;
        ScsApp.Press(app.Button(app.Main, "QualityButton"));
        var dialog = app.WaitModal();
        Assert.Equal("Óptima para compartir (ligera: hasta 1080p, 30 imágenes/s, H.264)", app.ById(dialog, "QualityPresetCombo").AsComboBox().SelectedItem?.Name);
        ScsApp.Select(Item(app, dialog, "ResolutionCombo", "720p (HD)"));
        ScsApp.Select(Item(app, dialog, "FpsCombo", "24 imágenes/s"));
        ScsApp.Select(Item(app, dialog, "ChannelsCombo", "Mono"));
        Assert.Equal("Personalizada", app.ById(dialog, "QualityPresetCombo").AsComboBox().SelectedItem?.Name);
        Assert.False(Item(app, dialog, "AudioKbpsCombo", "320 kbit/s (el codificador AAC de Windows no lo admite)").IsEnabled);
        app.Capture(dialog, "calidad");
        ScsApp.Press(app.Button(dialog, "SaveButton"));
        app.WaitNoModal();
        var s = app.Settings();
        Assert.Contains("\"quality\": \"custom\"", s);
        Assert.Contains("\"resolution\": \"p720\"", s);
        Assert.Contains("\"fps\": 24", s);
        Assert.Contains("\"audioChannels\": 1", s);
        Assert.Equal(mainHandle, app.CurrentMain().Properties.NativeWindowHandle.Value);   // la misma ventana, sin rehacer
        Assert.NotEqual(before, app.ById(app.Main, "EstimateText").Name);
        Assert.Contains("24", app.ById(app.Main, "EstimateText").Name);
    }

    /// <summary>Un elemento de un desplegable por su texto (el desplegable se abre y se cierra por patrones).</summary>
    private static AutomationElement Item(ScsApp app, AutomationElement parent, string comboId, string name)
    {
        var combo = app.ById(parent, comboId).AsComboBox();
        combo.Expand();
        var item = Retry.WhileNull(() => combo.FindFirstDescendant(app.Cf.ByName(name)), ScsApp.Timeout, throwOnTimeout: true, timeoutMessage: $"No está «{name}» en {comboId}").Result!;
        return item;
    }
}
