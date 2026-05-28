using System;
using System.Collections.Generic;
using System.Windows;
using Jarvis.Installer.NET.Pages;
using Jarvis.Setup.NET.Models;
using Jarvis.Setup.NET.Services;

namespace Jarvis.Installer.NET;

public partial class MainWindow : Window
{
    /// <summary>Shared install config + memory body — every page reads/writes this.</summary>
    public InstallerState State { get; } = new();

    private readonly List<IWizardPage> _pages = new();
    private int _index;

    public MainWindow()
    {
        InitializeComponent();

        State.Config.InstallDir = InstallLocator.DefaultInstallDir;

        // Repair / reinstall: pre-fill from an existing pointer if found.
        var existing = InstallLocator.LoadExisting();
        if (existing != null && !string.IsNullOrWhiteSpace(existing.InstallDir))
            State.Config.InstallDir = existing.InstallDir;

        _pages.Add(new WelcomePage());
        _pages.Add(new InstallLocationPage());
        _pages.Add(new LlmPage());
        _pages.Add(new VoicePage());
        _pages.Add(new MemoryPage());
        _pages.Add(new ToolsPage());
        _pages.Add(new InstallProgressPage());
        _pages.Add(new DonePage());

        ShowPage(0);
    }

    private void ShowPage(int idx)
    {
        _index = idx;
        var page = _pages[idx];
        page.Bind(State, this);
        PageHost.Navigate(page);

        StepLabel.Text = $"Step {idx + 1} of {_pages.Count} — {page.StepTitle}";
        BackBtn.IsEnabled = idx > 0 && page.CanGoBack;
        NextBtn.Content   = page.NextLabel;
        NextBtn.IsEnabled = page.CanGoNext;
        CancelBtn.Visibility = page.HideCancel ? Visibility.Hidden : Visibility.Visible;
    }

    /// <summary>Pages call this to re-evaluate Next button state after user input.</summary>
    public void RefreshNav()
    {
        var page = _pages[_index];
        BackBtn.IsEnabled    = _index > 0 && page.CanGoBack;
        NextBtn.IsEnabled    = page.CanGoNext;
        NextBtn.Content      = page.NextLabel;
        CancelBtn.Visibility = page.HideCancel ? Visibility.Hidden : Visibility.Visible;
    }

    private async void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        var page = _pages[_index];
        try
        {
            if (!await page.OnNextAsync()) return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Jarvis (.NET) Setup",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (_index + 1 < _pages.Count)
            ShowPage(_index + 1);
        else
            Close();
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_index > 0) ShowPage(_index - 1);
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this,
            "Cancel installation? No files have been changed yet.",
            "Jarvis (.NET) Setup", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) Close();
    }
}

/// <summary>
/// Shared wizard state. We need a wrapper because the .NET edition's
/// InstallConfig (mirroring config.yaml) intentionally doesn't carry
/// the user's typed memory.md body — that lives on disk, not in
/// config.yaml. The installer wizard needs somewhere to keep it
/// between the Memory page and the InstallProgress page.
/// </summary>
public class InstallerState
{
    public InstallConfig Config { get; } = new();

    /// <summary>Free-text body the user fills in on the Memory page. Goes
    /// into memory.md as-is at install time. Empty = don't write the file.</summary>
    public string MemoryBody { get; set; } = "";

    /// <summary>If true, the Memory page wrote the file. If false, no
    /// memory.md is written and persona.memory_file is cleared.</summary>
    public bool MemoryEnabled { get; set; } = true;

    /// <summary>If true and music_ytmd is enabled, auto-install the YT Music
    /// app via NSIS silent install during the progress page.</summary>
    public bool MusicYtmdAutoInstall { get; set; } = true;
}

/// <summary>Contract every wizard page implements.</summary>
public interface IWizardPage
{
    string StepTitle { get; }
    string NextLabel { get; }
    bool CanGoBack { get; }
    bool CanGoNext { get; }
    bool HideCancel { get; }

    void Bind(InstallerState state, MainWindow host);

    /// <summary>Returns true to advance, false to stay.</summary>
    System.Threading.Tasks.Task<bool> OnNextAsync();
}
