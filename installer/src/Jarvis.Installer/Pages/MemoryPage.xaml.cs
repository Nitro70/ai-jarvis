using System.Threading.Tasks;
using System.Windows.Controls;
using Jarvis.Setup.Models;

namespace Jarvis.Installer.Pages;

public partial class MemoryPage : Page, IWizardPage
{
    public MemoryPage() { InitializeComponent(); }
    public string StepTitle => "Personal memory";
    public string NextLabel => "Next";
    public bool CanGoBack => true;
    public bool CanGoNext => true;
    public bool HideCancel => false;
    public void Bind(InstallConfig cfg, MainWindow host) => Host.Bind(cfg);
    public Task<bool> OnNextAsync() => Task.FromResult(true);
}
