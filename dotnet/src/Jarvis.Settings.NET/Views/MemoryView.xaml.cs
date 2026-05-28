using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Setup.NET.Models;

namespace Jarvis.Settings.NET.Views;

/// <summary>
/// Memory.md editor. Unlike the Python edition (which stores memory content
/// inside the in-memory InstallConfig as Memory.Content), the .NET edition's
/// config only stores Persona.MemoryFile = filename. So we read the file
/// content from disk on Bind, and write it back to disk on Flush.
/// </summary>
public partial class MemoryView : UserControl
{
    private InstallConfig? _cfg;

    public MemoryView() { InitializeComponent(); }

    public void Bind(InstallConfig cfg)
    {
        _cfg = cfg;

        // "Enabled" maps to: Persona.MemoryFile is set to a non-empty string.
        bool enabled = !string.IsNullOrWhiteSpace(cfg.Persona.MemoryFile);
        MemoryEnabled.IsChecked = enabled;

        // Try to load existing memory.md from the install dir.
        bool loadedFromDisk = false;
        string content = "";
        var fileName = string.IsNullOrWhiteSpace(cfg.Persona.MemoryFile)
            ? "memory.md"
            : cfg.Persona.MemoryFile;
        if (!string.IsNullOrWhiteSpace(cfg.InstallDir))
        {
            var path = Path.Combine(cfg.InstallDir, fileName);
            if (File.Exists(path))
            {
                try
                {
                    content = File.ReadAllText(path);
                    loadedFromDisk = true;
                }
                catch { /* unreadable — fall through to template */ }
            }
        }

        if (string.IsNullOrEmpty(content))
            content = DefaultTemplate;

        MemoryBox.Text = content;

        ExistingNotice.Visibility = loadedFromDisk ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Persist the textbox content to disk if memory is enabled. Called by
    /// MainWindow before ConfigService.Save.
    /// </summary>
    public void Flush()
    {
        if (_cfg == null) return;

        bool enabled = MemoryEnabled.IsChecked == true;
        if (enabled)
        {
            // Default the filename if user enabled it but it was empty in the
            // config (e.g. fresh install picking it up for the first time).
            if (string.IsNullOrWhiteSpace(_cfg.Persona.MemoryFile))
                _cfg.Persona.MemoryFile = "memory.md";

            try
            {
                if (!string.IsNullOrWhiteSpace(_cfg.InstallDir) &&
                    Directory.Exists(_cfg.InstallDir))
                {
                    var path = Path.Combine(_cfg.InstallDir, _cfg.Persona.MemoryFile);
                    File.WriteAllText(path, MemoryBox.Text ?? "");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    $"Could not write memory file:\n{ex.Message}",
                    "Jarvis Settings",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            // Disabled — clear the filename in config (but DON'T delete the
            // file from disk; user's notes are precious, leave the file in
            // place so re-enabling is non-destructive).
            _cfg.Persona.MemoryFile = null;
        }
    }

    private void MemoryEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        // No-op handler — the IsEnabled binding on MemoryBox handles the
        // visual side, and Flush() picks up the final state on save.
    }

    private const string DefaultTemplate =
@"# About me

(Your name, what you do — anything you'd want Jarvis to remember.)

# Preferences

- I prefer concise answers.
- I work in (your timezone here).

# Routines

(Recurring things — morning standup at 9, gym on Tuesdays, etc.)

# Learned

(Jarvis appends here when you ask it to remember something.)
";
}
