using System.Threading.Tasks;
using System.Windows.Controls;

namespace Jarvis.Installer.NET.Pages;

public partial class WelcomePage : Page, IWizardPage
{
    public WelcomePage() { InitializeComponent(); }
    public string StepTitle => "Welcome";
    public string NextLabel => "Next";
    public bool CanGoBack => true;
    public bool CanGoNext => true;
    public bool HideCancel => false;
    public void Bind(InstallerState state, MainWindow host) { }
    public Task<bool> OnNextAsync() => Task.FromResult(true);
}
