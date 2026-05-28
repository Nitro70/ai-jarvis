using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Jarvis.Installer.NET.Pages;

public partial class ToolsPage : Page, IWizardPage
{
    private InstallerState? _state;
    private bool _bound;

    public ToolsPage() { InitializeComponent(); }
    public string StepTitle => "Tools";
    public string NextLabel => "Install";
    public bool CanGoBack => true;
    public bool CanGoNext => true;
    public bool HideCancel => false;

    public void Bind(InstallerState state, MainWindow host)
    {
        _state = state;
        _bound = false;
        try
        {
            var t = state.Config.Tools;
            ChkMemory.IsChecked       = t.Memory.Enabled;
            ChkSysInfo.IsChecked      = t.SystemInfo.Enabled;
            ChkWebBrowser.IsChecked   = t.WebBrowser.Enabled;
            ChkWinApps.IsChecked      = t.WindowsApps.Enabled;
            ChkWinState.IsChecked     = t.WindowsState.Enabled;
            ChkMusicYtmd.IsChecked    = t.MusicYtmd.Enabled;
            ChkMusicYtmdAuto.IsChecked = state.MusicYtmdAutoInstall;
            ChkDangerShell.IsChecked  = t.DangerousShell.Enabled;
            ChkDangerAck.IsChecked    = t.DangerousShell.IUnderstandTheRisks;
        }
        finally
        {
            _bound = true;
        }
    }

    private void Tool_Changed(object sender, RoutedEventArgs e)
    {
        if (!_bound || _state == null) return;
        var t = _state.Config.Tools;
        t.Memory.Enabled       = ChkMemory.IsChecked == true;
        t.SystemInfo.Enabled   = ChkSysInfo.IsChecked == true;
        t.WebBrowser.Enabled   = ChkWebBrowser.IsChecked == true;
        t.WindowsApps.Enabled  = ChkWinApps.IsChecked == true;
        t.WindowsState.Enabled = ChkWinState.IsChecked == true;
        t.MusicYtmd.Enabled    = ChkMusicYtmd.IsChecked == true;
        _state.MusicYtmdAutoInstall = ChkMusicYtmdAuto.IsChecked == true;
        t.DangerousShell.Enabled = ChkDangerShell.IsChecked == true;
        t.DangerousShell.IUnderstandTheRisks = ChkDangerAck.IsChecked == true;
    }

    public Task<bool> OnNextAsync() => Task.FromResult(true);
}
