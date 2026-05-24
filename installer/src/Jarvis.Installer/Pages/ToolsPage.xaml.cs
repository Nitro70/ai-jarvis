using System.Threading.Tasks;
using System.Windows.Controls;
using Jarvis.Setup.Models;

namespace Jarvis.Installer.Pages;

public partial class ToolsPage : Page, IWizardPage
{
    public ToolsPage() { InitializeComponent(); }
    public string StepTitle => "Tools";
    public string NextLabel => "Install";
    public bool CanGoBack => true;
    public bool CanGoNext => true;
    public bool HideCancel => false;
    public void Bind(InstallConfig cfg, MainWindow host) => Host.Bind(cfg);
    public Task<bool> OnNextAsync() => Task.FromResult(true);
}
