using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Installer.Pages;
using Jarvis.Setup.Models;
using Jarvis.Setup.Services;

namespace Jarvis.Installer;

public partial class MainWindow : Window
{
    /// <summary>Shared install config — every page reads/writes this.</summary>
    public InstallConfig Config { get; } = new InstallConfig
    {
        InstallDir = InstallLocator.DefaultInstallDir,
    };

    private readonly List<IWizardPage> _pages = new();
    private int _index;

    public MainWindow()
    {
        InitializeComponent();

        // If a previous install exists, pre-fill from it (settings-style behavior
        // even though we're the installer — useful for "repair / reinstall").
        var existing = InstallLocator.LoadExisting();
        if (existing != null)
            Config.InstallDir = existing.InstallDir;

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
        page.Bind(Config, this);
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
            MessageBox.Show(this, ex.Message, "Jarvis Setup",
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
            "Jarvis Setup", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) Close();
    }
}

/// <summary>Contract every wizard page implements.</summary>
public interface IWizardPage
{
    string StepTitle { get; }
    string NextLabel { get; }
    bool CanGoBack { get; }
    bool CanGoNext { get; }
    bool HideCancel { get; }

    void Bind(InstallConfig cfg, MainWindow host);

    /// <summary>Returns true to advance, false to stay.</summary>
    System.Threading.Tasks.Task<bool> OnNextAsync();
}
