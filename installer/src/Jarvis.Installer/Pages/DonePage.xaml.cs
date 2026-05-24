using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Controls;
using Jarvis.Setup.Models;

namespace Jarvis.Installer.Pages;

public partial class DonePage : Page, IWizardPage
{
    private InstallConfig? _cfg;

    public DonePage() { InitializeComponent(); }
    public string StepTitle => "Done";
    public string NextLabel => "Finish";
    public bool CanGoBack => false;
    public bool CanGoNext => true;
    public bool HideCancel => true;

    public void Bind(InstallConfig cfg, MainWindow host)
    {
        _cfg = cfg;
        InstallPathText.Text = $"Installed to:\n{cfg.InstallDir}";

        // Show the YT Music API-Server-plugin reminder only if we actually
        // installed the YT Music app.
        if (!string.IsNullOrWhiteSpace(cfg.Tools.MusicYtmdExePath))
            YtmdNote.Visibility = System.Windows.Visibility.Visible;
    }

    public Task<bool> OnNextAsync()
    {
        if (LaunchCheck.IsChecked == true && _cfg != null)
        {
            var runCmd = Path.Combine(_cfg.InstallDir, "run-jarvis.cmd");
            if (File.Exists(runCmd))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/k \"\"{runCmd}\"\"",
                        UseShellExecute = true,
                        WorkingDirectory = _cfg.InstallDir,
                    });
                }
                catch { }
            }
        }
        return Task.FromResult(true);
    }
}
