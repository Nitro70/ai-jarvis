using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Jarvis.Setup.Models;
using Jarvis.Setup.Services;

namespace Jarvis.Installer.Pages;

public partial class InstallProgressPage : Page, IWizardPage
{
    private InstallConfig? _cfg;
    private MainWindow? _host;
    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _done;

    public InstallProgressPage() { InitializeComponent(); }

    public string StepTitle => "Installing";
    public string NextLabel => _done ? "Next" : "Next";
    public bool CanGoBack => false;
    public bool CanGoNext => _done;
    public bool HideCancel => _done;

    public void Bind(InstallConfig cfg, MainWindow host)
    {
        _cfg = cfg;
        _host = host;
        if (!_started)
        {
            _started = true;
            _ = StartInstallAsync();
        }
    }

    private async Task StartInstallAsync()
    {
        if (_cfg == null || _host == null) return;
        _cts = new CancellationTokenSource();

        var log = new Progress<string>(line =>
        {
            LogText.AppendText(line + Environment.NewLine);
            LogScroll.ScrollToEnd();
        });
        var pct = new Progress<double>(p => Progress.Value = p);
        var step = new Progress<string>(s => StepText.Text = s + "...");

        try
        {
            await Jarvis.Setup.Services.Installer.InstallAsync(_cfg, log, pct, step, _cts.Token);
            _done = true;
            StepText.Text = "Installation complete.";
            Progress.Value = 100;
        }
        catch (OperationCanceledException)
        {
            StepText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StepText.Text = "Installation failed.";
            LogText.AppendText(Environment.NewLine + "ERROR: " + ex.Message + Environment.NewLine);
            LogScroll.ScrollToEnd();
            MessageBox.Show(_host, ex.Message, "Jarvis Setup",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _host.RefreshNav();
        }
    }

    public Task<bool> OnNextAsync() => Task.FromResult(_done);
}
