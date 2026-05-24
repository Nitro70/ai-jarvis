using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Setup.Models;

namespace Jarvis.Installer.Pages;

public partial class InstallLocationPage : Page, IWizardPage
{
    private InstallConfig? _cfg;
    private MainWindow? _host;

    public InstallLocationPage() { InitializeComponent(); }
    public string StepTitle => "Install location";
    public string NextLabel => "Next";
    public bool CanGoBack => true;
    public bool CanGoNext => Validate(out _);
    public bool HideCancel => false;

    public void Bind(InstallConfig cfg, MainWindow host)
    {
        _cfg = cfg;
        _host = host;
        PathBox.Text = cfg.InstallDir;
        UpdateHint();
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_cfg == null) return;
        _cfg.InstallDir = PathBox.Text;
        UpdateHint();
        _host?.RefreshNav();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        // Plain old folder picker via SHBrowseForFolder/CommonOpenFileDialog —
        // WPF has no native one. We use the WinForms FolderBrowserDialog
        // via reflection to avoid pulling in WinForms reference. Or just type it.
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Pick install folder",
            InitialDirectory = Directory.Exists(_cfg!.InstallDir)
                ? _cfg.InstallDir
                : Path.GetDirectoryName(_cfg.InstallDir) ?? "",
        };
        if (dlg.ShowDialog(_host) == true)
        {
            // If they picked an existing folder (e.g. "Programs"), append "Jarvis".
            var picked = dlg.FolderName;
            if (!picked.EndsWith("Jarvis", StringComparison.OrdinalIgnoreCase))
                picked = Path.Combine(picked, "Jarvis");
            PathBox.Text = picked;
        }
    }

    private bool Validate(out string? error)
    {
        error = null;
        var p = (_cfg?.InstallDir ?? "").Trim();
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
        var p = _cfg!.InstallDir;
        if (Directory.Exists(p) && Directory.GetFileSystemEntries(p).Length > 0)
        {
            HintText.Text = $"Folder exists and is not empty. Files will be added/overwritten — that's fine for a reinstall.";
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
            MessageBox.Show(_host, err, "Jarvis Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }
}
