using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Setup.Models;

namespace Jarvis.Setup.Views;

public partial class MemoryView : UserControl
{
    public MemoryView() { InitializeComponent(); }

    public void Bind(InstallConfig cfg)
    {
        // Reinstall safety: if a memory.md already exists in the chosen
        // install dir AND the wizard's Memory.Content is still empty (i.e.
        // the user hasn't typed anything yet), pre-fill from disk so a
        // re-run of the installer doesn't silently overwrite their
        // accumulated memory with the default template. Their typed edits
        // (if any) take precedence on Save; if they don't touch the
        // textbox, the existing memory survives untouched.
        bool loadedFromDisk = false;
        if (string.IsNullOrWhiteSpace(cfg.Memory.Content) &&
            !string.IsNullOrWhiteSpace(cfg.InstallDir))
        {
            var existing = Path.Combine(cfg.InstallDir, "memory.md");
            if (File.Exists(existing))
            {
                try
                {
                    cfg.Memory.Content = File.ReadAllText(existing);
                    loadedFromDisk = true;
                }
                catch { /* unreadable file — fall through to template */ }
            }
        }

        if (string.IsNullOrEmpty(cfg.Memory.Content))
            cfg.Memory.Content = DefaultTemplate;

        // Important: set DataContext AFTER mutating Memory.Content. WPF
        // bindings without INPC only pick up the property's CURRENT value
        // when the binding is established.
        DataContext = cfg;

        // Show a yellow callout when we loaded existing content, so the user
        // knows their old memory is preserved and any edits replace it.
        ExistingNotice.Visibility = loadedFromDisk ? Visibility.Visible : Visibility.Collapsed;
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
