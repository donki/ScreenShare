using System.Globalization;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SocScreenShare.ToneApp;

/// <summary>
/// sOCScreenShareToneApp --title T --freq 440 --seconds 4 [--amp 0.05] [--delay 0.5] [--no-window]
/// Tono senoidal suave por el dispositivo de salida predeterminado; la ventana (si la hay) se abre sin
/// activarse, abajo a la derecha, y se cierra al acabar el tono.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string Arg(string name, string def)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
        }
        var title = Arg("--title", "ScreenShare tone");
        var freq = double.Parse(Arg("--freq", "440"), CultureInfo.InvariantCulture);
        var seconds = double.Parse(Arg("--seconds", "4"), CultureInfo.InvariantCulture);
        var amp = float.Parse(Arg("--amp", "0.05"), CultureInfo.InvariantCulture);
        var delay = double.Parse(Arg("--delay", "0.5"), CultureInfo.InvariantCulture);

        var tone = new SignalGenerator(48000, 2) { Frequency = freq, Gain = amp, Type = SignalGeneratorType.Sin }
            .Take(TimeSpan.FromSeconds(seconds));
        using var output = new WasapiPlayerBuilder().WithSharedMode().Build();

        if (args.Contains("--no-window"))
        {
            Thread.Sleep(TimeSpan.FromSeconds(delay));
            output.Init(tone.ToWaveProvider());
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
                Thread.Sleep(50);
            return;
        }

        ApplicationConfiguration.Initialize();
        var form = new QuietForm { Text = title };
        form.Shown += async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(delay));
            output.Init(tone.ToWaveProvider());
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing)
                await Task.Delay(50);
            await Task.Delay(300);
            form.Close();
        };
        Application.Run(form);
    }

    /// <summary>Una ventana que se enseña sin quitarle el foco a nadie.</summary>
    private sealed class QuietForm : Form
    {
        public QuietForm()
        {
            StartPosition = FormStartPosition.Manual;
            Size = new Size(360, 220);
            var area = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(area.Right - Width - 20, area.Bottom - Height - 20);
            BackColor = Color.FromArgb(0x35, 0x25, 0xCD);
            ShowInTaskbar = true;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
                return cp;
            }
        }
    }
}
