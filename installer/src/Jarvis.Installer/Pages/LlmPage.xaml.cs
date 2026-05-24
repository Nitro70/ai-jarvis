using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Setup.Models;

namespace Jarvis.Installer.Pages;

public partial class LlmPage : Page, IWizardPage
{
    private InstallConfig? _cfg;
    private MainWindow? _host;
    public LlmPage() { InitializeComponent(); }

    public string StepTitle => "AI backend";
    public string NextLabel => "Next";
    public bool CanGoBack => true;
    public bool CanGoNext => true;
    public bool HideCancel => false;

    public void Bind(InstallConfig cfg, MainWindow host)
    {
        _cfg = cfg;
        _host = host;
        Host.Bind(cfg);
    }

    public Task<bool> OnNextAsync()
    {
        if (_cfg == null) return Task.FromResult(true);
        var llm = _cfg.Llm;

        if (llm.Backend == "claude_api" && string.IsNullOrWhiteSpace(llm.ApiKey))
        {
            return Confirm("No API key entered. Jarvis won't start until you add one. Continue anyway?");
        }
        if (llm.Backend == "openai_compat")
        {
            if (string.IsNullOrWhiteSpace(llm.BaseUrl))
                return Confirm("No base URL entered. Continue anyway?");
            if (string.IsNullOrWhiteSpace(llm.ApiKey) &&
                !llm.BaseUrl.Contains("localhost") &&
                !llm.BaseUrl.Contains("127.0.0.1"))
                return Confirm("No API key entered for a remote OpenAI-compatible API. Continue anyway?");
        }
        return Task.FromResult(true);
    }

    private Task<bool> Confirm(string msg)
    {
        var r = MessageBox.Show(_host, msg, "Jarvis Setup",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return Task.FromResult(r == MessageBoxResult.Yes);
    }
}
