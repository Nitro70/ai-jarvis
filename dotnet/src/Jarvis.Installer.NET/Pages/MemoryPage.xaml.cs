using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Jarvis.Installer.NET.Pages;

public partial class MemoryPage : Page, IWizardPage
{
    private InstallerState? _state;
    private bool _bound;

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

    public MemoryPage() { InitializeComponent(); }
    public string StepTitle => "Personal memory";
    public string NextLabel => "Next";
    public bool CanGoBack => true;
    public bool CanGoNext => true;
    public bool HideCancel => false;

    public void Bind(InstallerState state, MainWindow host)
    {
        _state = state;
        _bound = false;
        try
        {
            // Reinstall safety: if a memory.md already exists in the chosen
            // install dir AND the wizard's MemoryBody is still empty, pre-fill
            // from disk so the installer doesn't overwrite an accumulated
            // memory.md with a fresh template.
            bool loadedFromDisk = false;
            if (string.IsNullOrWhiteSpace(state.MemoryBody) &&
                !string.IsNullOrWhiteSpace(state.Config.InstallDir))
            {
                var existing = Path.Combine(state.Config.InstallDir, "memory.md");
                if (File.Exists(existing))
                {
                    try
                    {
                        state.MemoryBody = File.ReadAllText(existing);
                        loadedFromDisk = true;
                    }
                    catch { }
                }
            }

            if (string.IsNullOrEmpty(state.MemoryBody))
                state.MemoryBody = DefaultTemplate;

            MemoryBox.Text = state.MemoryBody;
            MemoryEnabled.IsChecked = state.MemoryEnabled;
            ExistingNotice.Visibility = loadedFromDisk ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _bound = true;
        }
    }

    private void MemoryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.MemoryBody = MemoryBox.Text;
    }

    private void MemoryEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.MemoryEnabled = MemoryEnabled.IsChecked == true;
    }

    public Task<bool> OnNextAsync() => Task.FromResult(true);
}
