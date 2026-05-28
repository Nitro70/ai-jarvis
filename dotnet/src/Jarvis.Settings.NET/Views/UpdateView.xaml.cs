using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jarvis.Settings.NET.Services;
using Jarvis.Setup.NET.Models;
using Jarvis.Setup.NET.Services;

namespace Jarvis.Settings.NET.Views;

public partial class UpdateView : UserControl
{
    private InstallConfig? _cfg;
    private InstallPointer? _pointer;
    private UpdateChecker.ReleaseInfo? _latest;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public UpdateView() { InitializeComponent(); }

    public void Bind(InstallConfig cfg, InstallPointer pointer)
    {
        _cfg = cfg;
        _pointer = pointer;
        InstalledVerLabel.Text = DisplayInstalledVersion(cfg);

        // Auto-check on launch so the user doesn't have to click "Check"
        // just to see whether they're current. Fire-and-forget.
        _ = CheckAsync(showNotesEvenIfUpToDate: false);
    }

    private static string DisplayInstalledVersion(InstallConfig cfg)
    {
        var asm = Assembly.GetExecutingAssembly().GetName().Version;
        var stored = cfg.Version;

        bool brokenSentinel = stored == "1.0.0" || stored == "1.0.0.0";

        if (!string.IsNullOrWhiteSpace(stored) && !brokenSentinel)
            return stored!;

        if (asm == null) return "(unknown)";
        if (asm.Major == 1 && asm.Minor == 0 && asm.Build == 0)
            return "(unknown — please reinstall to fix)";
        return asm.ToString(3);
    }

    private async void CheckBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg == null || _busy) return;
        await CheckAsync(showNotesEvenIfUpToDate: true);
    }

    private async System.Threading.Tasks.Task CheckAsync(bool showNotesEvenIfUpToDate)
    {
        if (_cfg == null) return;
        SetBusy(true, "Checking GitHub...");
        try
        {
            _latest = await UpdateChecker.FetchLatestAsync(CancellationToken.None);
            LatestVerLabel.Text = _latest.Tag;
            NotesText.Text = string.IsNullOrWhiteSpace(_latest.Body)
                ? "(no release notes)"
                : _latest.Body;

            if (UpdateChecker.IsNewer(_latest.Tag, _cfg.Version))
            {
                StatusLine.Text = $"Update available: {_latest.Tag}";
                StatusLine.Foreground = Brushes.DarkGoldenrod;
                UpdateBtn.IsEnabled = true;
            }
            else
            {
                StatusLine.Text = "You're on the latest version.";
                StatusLine.Foreground = Brushes.SeaGreen;
                UpdateBtn.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Couldn't reach GitHub: " + ex.Message;
            StatusLine.Foreground = Brushes.Firebrick;
            if (!showNotesEvenIfUpToDate) NotesText.Text = "";
        }
        finally { SetBusy(false); }
    }

    private async void UpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg == null || _pointer == null || _latest == null || _busy) return;
        var confirm = MessageBox.Show(
            Window.GetWindow(this),
            $"Download and install {_latest.Tag}?\n\n" +
            $"Your config.yaml and memory.md will not be touched.\n" +
            $"You'll need to restart Jarvis Settings after the update.",
            "Update Jarvis", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        _cts = new CancellationTokenSource();
        SetBusy(true, "Updating...");
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;

        var log = new Progress<string>(line => StatusLine.Text = line);
        var pct = new Progress<double>(p => Progress.Value = p);

        try
        {
            var result = await Updater.ApplyAsync(_cfg, _pointer, _latest, log, pct, _cts.Token);
            StatusLine.Text = result.Message;
            StatusLine.Foreground = Brushes.SeaGreen;
            InstalledVerLabel.Text = _cfg.Version ?? InstalledVerLabel.Text;
            UpdateBtn.IsEnabled = false;
            if (result.RestartRequired)
            {
                MessageBox.Show(
                    Window.GetWindow(this),
                    result.Message + "\n\nClose this window and re-launch " +
                    "'Jarvis Settings (.NET)' from the Start Menu to use the new version.",
                    "Update complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "Update cancelled.";
            StatusLine.Foreground = Brushes.Gray;
        }
        catch (Exception ex)
        {
            StatusLine.Text = "Update failed: " + ex.Message;
            StatusLine.Foreground = Brushes.Firebrick;
            MessageBox.Show(Window.GetWindow(this), ex.ToString(),
                            "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            Progress.Visibility = Visibility.Hidden;
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        CheckBtn.IsEnabled = !busy;
        UpdateBtn.IsEnabled = !busy && _latest != null &&
                              _cfg != null && UpdateChecker.IsNewer(_latest.Tag, _cfg.Version);
        if (status != null)
        {
            StatusLine.Text = status;
            StatusLine.Foreground = Brushes.Gray;
        }
    }
}
