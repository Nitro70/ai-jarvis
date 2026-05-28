using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Jarvis.Settings.NET.Services;
using Jarvis.Setup.NET.Models;
using Jarvis.Setup.NET.Services;

namespace Jarvis.Settings.NET;

public partial class MainWindow : Window
{
    private readonly InstallConfig _cfg;
    private readonly InstallPointer _pointer;
    private readonly ConfigService _configService = new();

    public MainWindow()
    {
        InitializeComponent();

        // Resolve install dir. Prefer the install pointer; fall back to the
        // directory this .exe lives in (since JarvisSettings-NET.exe ships
        // alongside Jarvis-NET.exe and config.yaml in the install dir).
        _pointer = InstallLocator.LoadExisting() ?? new InstallPointer
        {
            InstallDir = AppContext.BaseDirectory,
        };
        if (string.IsNullOrWhiteSpace(_pointer.InstallDir) ||
            !Directory.Exists(_pointer.InstallDir))
        {
            _pointer.InstallDir = AppContext.BaseDirectory;
        }

        // Load config.yaml (or defaults if it doesn't exist yet).
        try
        {
            _cfg = _configService.Load(_pointer.InstallDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Failed to load config.yaml:\n\n{ex.Message}\n\n" +
                "Starting from defaults — your existing file is NOT modified.",
                "Jarvis Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            _cfg = new InstallConfig { InstallDir = _pointer.InstallDir };
        }
        _cfg.InstallDir = _pointer.InstallDir;
        // Mirror the pointer's version onto the config so the Updates tab can
        // display + self-heal it via the same code path as the Python edition.
        _cfg.Version = _pointer.Version;

        // Self-heal corrupted version stamps. Same logic as the Python
        // edition: if install-info.json's Version is missing, the broken
        // "1.0.0" sentinel, or older than the running .exe, trust the .exe.
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
                bool asmIsReal = asm.Major == 0 || asm.Major >= 2;
                if ((unknown || brokenSentinel || asm > stored) && asmIsReal)
                {
                    _cfg.Version = asm.ToString(3);
                    _pointer.Version = _cfg.Version;
                    try { InstallLocator.Save(_pointer); } catch { }
                }
            }
        }
        catch { /* best-effort */ }

        InstallDirLabel.Text = _cfg.InstallDir;

        // Sweep up any leftover JarvisSettings-NET.exe.old from a prior update.
        Updater.CleanupOldSettingsExe(_cfg.InstallDir);

        LlmTab.Bind(_cfg);
        VoiceTab.Bind(_cfg);
        MemoryTab.Bind(_cfg);
        ToolsTab.Bind(_cfg);
        UpdateTab.Bind(_cfg, _pointer);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(_cfg.InstallDir))
                throw new DirectoryNotFoundException(
                    $"Install dir not found: {_cfg.InstallDir}");

            // Flush any per-view edits that aren't captured by simple bindings
            // (memory.md, password box).
            LlmTab.Flush();
            MemoryTab.Flush();

            _configService.Save(_cfg);
            // Keep the install pointer in sync with the install dir; never
            // overwrite the runtime / version fields needlessly.
            _pointer.InstallDir = _cfg.InstallDir;
            _pointer.Version = _cfg.Version ?? _pointer.Version;
            InstallLocator.Save(_pointer);

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
