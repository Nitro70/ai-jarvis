using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Jarvis.Installer.NET.Pages;

public partial class InstallLocationPage : Page, IWizardPage
{
    private InstallerState? _state;
    private MainWindow? _host;

    public InstallLocationPage() { InitializeComponent(); }
    public string StepTitle => "Install location";
    public string NextLabel => "Next";
    public bool CanGoBack => true;
    public bool CanGoNext => Validate(out _);
    public bool HideCancel => false;

    public void Bind(InstallerState state, MainWindow host)
    {
        _state = state;
        _host = host;
        PathBox.Text = state.Config.InstallDir;
        UpdateHint();
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_state == null) return;
        _state.Config.InstallDir = PathBox.Text;
        UpdateHint();
        _host?.RefreshNav();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick install folder",
            InitialDirectory = Directory.Exists(_state!.Config.InstallDir)
                ? _state.Config.InstallDir
                : Path.GetDirectoryName(_state.Config.InstallDir) ?? "",
        };
        if (dlg.ShowDialog(_host) == true)
        {
            var picked = dlg.FolderName;
            // If they picked an existing parent (e.g. "Programs"), append "Jarvis-NET".
            if (!picked.EndsWith("Jarvis-NET", StringComparison.OrdinalIgnoreCase) &&
                !picked.EndsWith("Jarvis", StringComparison.OrdinalIgnoreCase))
                picked = Path.Combine(picked, "Jarvis-NET");
            PathBox.Text = picked;
        }
    }

    private bool Validate(out string? error)
    {
        error = null;
        var p = (_state?.Config.InstallDir ?? "").Trim();
        if (string.IsNullOrWhiteSpace(p)) { error = "Pick a folder."; return false; }
        try
        {
            var full = Path.GetFullPath(p);
            if (!Path.IsPathFullyQualified(full)) { error = "Must be an absolute path."; return false; }
        }
        catch (Exception e) { error = e.Message; return false; }
        return true;
    }

    private void UpdateHint()
    {
        if (!Validate(out var err))
        {
            HintText.Text = err ?? "";
            HintText.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }
        var p = _state!.Config.InstallDir;
        if (Directory.Exists(p) && Directory.GetFileSystemEntries(p).Length > 0)
        {
            HintText.Text = "Folder exists and is not empty. Files will be added/overwritten — that's fine for a reinstall.";
            HintText.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
        }
        else
        {
            HintText.Text = "Folder will be created.";
            HintText.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    public Task<bool> OnNextAsync()
    {
        if (!Validate(out var err))
        {
            MessageBox.Show(_host, err, "Jarvis (.NET) Setup",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }
}
