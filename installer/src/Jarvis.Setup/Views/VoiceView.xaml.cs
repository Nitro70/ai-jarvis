using System.Windows;
using System.Windows.Controls;
using Jarvis.Setup.Models;

namespace Jarvis.Setup.Views;

public partial class VoiceView : UserControl
{
    private InstallConfig? _cfg;

    public VoiceView() { InitializeComponent(); }

    public void Bind(InstallConfig cfg)
    {
        _cfg = cfg;
        DataContext = cfg;
        ModeVoice.IsChecked = cfg.Mode == "voice";
        ModeText.IsChecked  = cfg.Mode != "voice";

        foreach (var raw in SttCombo.Items)
        {
            if (raw is ComboBoxItem cbi && (string?)cbi.Tag == cfg.Voice.SttModel)
            {
                SttCombo.SelectedItem = cbi;
                break;
            }
        }
        if (SttCombo.SelectedItem == null) SttCombo.SelectedIndex = 1; // base.en
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_cfg == null) return;
        _cfg.Mode = ModeVoice.IsChecked == true ? "voice" : "text";
    }

    private void SttCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_cfg == null) return;
        if (SttCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
            _cfg.Voice.SttModel = tag;
    }
}
