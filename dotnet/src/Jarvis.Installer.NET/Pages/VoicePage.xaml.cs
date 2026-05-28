using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Jarvis.Installer.NET.Pages;

public partial class VoicePage : Page, IWizardPage
{
    private InstallerState? _state;
    private bool _bound;

    public VoicePage() { InitializeComponent(); }
    public string StepTitle => "Voice & mode";
    public string NextLabel => "Next";
    public bool CanGoBack => true;
    public bool CanGoNext => true;
    public bool HideCancel => false;

    public void Bind(InstallerState state, MainWindow host)
    {
        _state = state;
        _bound = false;
        try
        {
            var cfg = state.Config;
            ModeVoice.IsChecked = cfg.Mode == "voice";
            ModeText.IsChecked  = cfg.Mode != "voice";

            WakeWordBox.Text = cfg.Voice.WakeWord;
            FollowUpBox.Text = cfg.Voice.FollowUpSeconds.ToString(CultureInfo.InvariantCulture);
            AlwaysOn.IsChecked = cfg.Voice.AlwaysOn;
            TtsEnabled.IsChecked = cfg.Voice.Tts.Enabled;
            TtsCombo.Text = cfg.Voice.Tts.Voice;

            SelectComboByTag(SttCombo,  cfg.Voice.Stt.Model,     defaultIndex: 1);
            SelectComboByTag(WakeCombo, cfg.Voice.Stt.WakeModel, defaultIndex: 0);
        }
        finally
        {
            _bound = true;
        }
    }

    private static void SelectComboByTag(ComboBox box, string? tagValue, int defaultIndex)
    {
        foreach (var raw in box.Items)
        {
            if (raw is ComboBoxItem cbi && (string?)cbi.Tag == tagValue)
            {
                box.SelectedItem = cbi;
                return;
            }
        }
        if (box.Items.Count > defaultIndex) box.SelectedIndex = defaultIndex;
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.Config.Mode = ModeVoice.IsChecked == true ? "voice" : "text";
    }

    private void WakeWord_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.Config.Voice.WakeWord = WakeWordBox.Text;
    }

    private void SttCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_bound || _state == null) return;
        if (SttCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
            _state.Config.Voice.Stt.Model = tag;
    }

    private void WakeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_bound || _state == null) return;
        if (WakeCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
            _state.Config.Voice.Stt.WakeModel = tag;
    }

    private void AlwaysOn_Changed(object sender, RoutedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.Config.Voice.AlwaysOn = AlwaysOn.IsChecked == true;
    }

    private void FollowUp_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_bound || _state == null) return;
        if (double.TryParse(FollowUpBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            _state.Config.Voice.FollowUpSeconds = v;
    }

    private void TtsEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.Config.Voice.Tts.Enabled = TtsEnabled.IsChecked == true;
    }

    private void TtsCombo_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.Config.Voice.Tts.Voice = TtsCombo.Text;
    }

    public Task<bool> OnNextAsync() => Task.FromResult(true);
}
