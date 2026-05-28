using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Jarvis.Installer.NET.Pages;

public partial class DonePage : Page, IWizardPage
{
    private InstallerState? _state;

    public DonePage() { InitializeComponent(); }
    public string StepTitle => "Done";
    public string NextLabel => "Finish";
    public bool CanGoBack => false;
    public bool CanGoNext => true;
    public bool HideCancel => true;

    public void Bind(InstallerState state, MainWindow host)
    {
        _state = state;
        InstallPathText.Text = $"Installed to:\n{state.Config.InstallDir}";

        // Show the YT Music API-Server-plugin reminder only if the installer
        // actually planted a YT Music app path in the config.
        if (!string.IsNullOrWhiteSpace(state.Config.Tools.MusicYtmd.ExePath))
            YtmdNote.Visibility = Visibility.Visible;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) return;
        try
        {
            if (Directory.Exists(_state.Config.InstallDir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _state.Config.InstallDir,
                    UseShellExecute = true,
                });
            }
        }
        catch { }
    }

    public Task<bool> OnNextAsync()
    {
        if (LaunchCheck.IsChecked == true && _state != null)
        {
            var exe = Path.Combine(_state.Config.InstallDir, "Jarvis-NET.exe");
            if (File.Exists(exe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = true,
                        WorkingDirectory = _state.Config.InstallDir,
                    });
                }
                catch { }
            }
        }
        return Task.FromResult(true);
    }
}
