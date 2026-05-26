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

        SelectComboByTag(SttCombo,  cfg.Voice.SttModel,  defaultIndex: 1);  // base.en
        SelectComboByTag(WakeCombo, cfg.Voice.WakeModel, defaultIndex: 0);  // tiny.en
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
        if (_cfg == null) return;
        _cfg.Mode = ModeVoice.IsChecked == true ? "voice" : "text";
    }

    private void SttCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_cfg == null) return;
        if (SttCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
            _cfg.Voice.SttModel = tag;
    }

    private void WakeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_cfg == null) return;
        if (WakeCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string tag)
            _cfg.Voice.WakeModel = tag;
    }
}
