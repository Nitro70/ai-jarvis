using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Jarvis.Setup.Models;
using Jarvis.Setup.Services;

namespace Jarvis.Settings;

public partial class MainWindow : Window
{
    private readonly InstallConfig _cfg;

    public MainWindow()
    {
        InitializeComponent();

        // Find the existing install via the pointer file, OR fall back to the
        // folder this .exe lives in (since Settings.exe is installed alongside
        // jarvis.py and config.yaml).
        _cfg = InstallLocator.LoadExisting()
               ?? new InstallConfig { InstallDir = AppContext.BaseDirectory };

        if (string.IsNullOrWhiteSpace(_cfg.InstallDir) || !Directory.Exists(_cfg.InstallDir))
            _cfg.InstallDir = AppContext.BaseDirectory;

        // Load existing memory.md content if any.
        var memoryPath = Path.Combine(_cfg.InstallDir, "memory.md");
        if (File.Exists(memoryPath))
        {
            try { _cfg.Memory.Content = File.ReadAllText(memoryPath); }
            catch { }
        }

        InstallDirLabel.Text = _cfg.InstallDir;

        // Sweep up any leftover JarvisSettings.exe.old left by a prior update.
        Updater.CleanupOldSettingsExe(_cfg.InstallDir);

        LlmTab.Bind(_cfg);
        VoiceTab.Bind(_cfg);
        MemoryTab.Bind(_cfg);
        ToolsTab.Bind(_cfg);
        UpdateTab.Bind(_cfg);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_cfg.InstallDir))
                throw new DirectoryNotFoundException($"Install dir not found: {_cfg.InstallDir}");

            ConfigYamlWriter.Write(_cfg);
            InstallLocator.Save(_cfg);
            StatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            StatusText.Text = $"Saved at {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = "Save failed: " + ex.Message;
            MessageBox.Show(this, ex.Message, "Jarvis Settings",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _cfg.InstallDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Jarvis Settings",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
