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

        // Overlay EVERY UI-writable section from the live config.yaml on top
        // of install-info.json. Without this the Settings UI shows stale
        // values for anything the user hand-edited — and Save would silently
        // overwrite those edits. memory.md loading is handled by MemoryView
        // itself (single source of truth) so the "loaded existing memory"
        // banner fires consistently in both wizard and Settings.
        try { ConfigYamlReader.OverlayAll(_cfg, _cfg.InstallDir); }
        catch { /* best-effort — never block the UI from loading */ }

        // Self-heal corrupted version stamps. Pre-0.1.12 installers stamped
        // install-info.json with "1.0.0" forever (Jarvis.Setup.dll's default
        // assembly version) regardless of release. If the running .exe has
        // a real version (0.x.y), trust IT and overwrite the bogus stamp.
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly()
                        .GetName().Version;
            if (asm != null)
            {
                var stored = UpdateChecker.ParseTag(_cfg.Version);
                bool brokenSentinel = _cfg.Version == "1.0.0"
                                      || _cfg.Version == "1.0.0.0";
                bool unknown = stored == null;
                bool asmIsReal = asm.Major == 0 || asm.Major >= 2;  // not the 1.0.0 default
                if ((unknown || brokenSentinel || asm > stored) && asmIsReal)
                {
                    _cfg.Version = asm.ToString(3);
                    InstallLocator.Save(_cfg);
                }
            }
        }
        catch { /* best-effort */ }

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
