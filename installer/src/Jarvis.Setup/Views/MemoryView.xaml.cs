using System.Windows.Controls;
using Jarvis.Setup.Models;

namespace Jarvis.Setup.Views;

public partial class MemoryView : UserControl
{
    public MemoryView() { InitializeComponent(); }

    public void Bind(InstallConfig cfg)
    {
        DataContext = cfg;
        if (string.IsNullOrEmpty(cfg.Memory.Content))
            cfg.Memory.Content = DefaultTemplate;
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
