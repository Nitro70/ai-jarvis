using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jarvis.Installer.NET.Services;
using Jarvis.Setup.NET.Models;

namespace Jarvis.Installer.NET.Pages;

public partial class LlmPage : Page, IWizardPage
{
    private InstallerState? _state;
    private MainWindow? _host;
    private bool _bound;  // suppress event handlers during initial Bind()
    private CancellationTokenSource? _testCts;

    private static readonly string[] ClaudeAgentModels = { "claude-sonnet-4-6", "claude-opus-4", "claude-haiku-4-5" };
    private static readonly string[] ClaudeApiModels   = { "claude-sonnet-4-6", "claude-opus-4", "claude-haiku-4-5" };
    private static readonly string[] OpenAiCompatModels = {
        "gpt-4o-mini", "gpt-4o",
        "llama-3.3-70b-versatile", "llama-3.1-8b-instant",
        "grok-4", "grok-4-fast",
        "llama3.1", "qwen2.5",
    };

    public LlmPage() { InitializeComponent(); }

    public string StepTitle => "AI backend";
    public string NextLabel => "Next";
    public bool CanGoBack => true;
    public bool CanGoNext => true;
    public bool HideCancel => false;

    public void Bind(InstallerState state, MainWindow host)
    {
        _state = state;
        _host = host;
        _bound = false;
        try
        {
            var llm = state.Config.Llm;

            // Pick the right ComboBox item for the backend value.
            foreach (var raw in BackendCombo.Items)
            {
                if (raw is ComboBoxItem cbi && (string?)cbi.Tag == llm.Backend)
                {
                    BackendCombo.SelectedItem = cbi;
                    break;
                }
            }
            if (BackendCombo.SelectedItem == null && BackendCombo.Items.Count > 0)
                BackendCombo.SelectedIndex = 0;

            BaseUrlPreset.Text = llm.BaseUrl ?? "";
            ApiKeyPwd.Password = llm.ApiKey ?? "";
            ApiKeyVisible.Text = llm.ApiKey ?? "";
            DisableToolsCheck.IsChecked = llm.DisableTools;
            MaxTokensBox.Text = llm.MaxTokens.ToString(CultureInfo.InvariantCulture);
            UpdateBackendSpecificUI();
            ModelCombo.Text = llm.Model;
        }
        finally
        {
            _bound = true;
        }
    }

    private string SelectedBackend =>
        (BackendCombo.SelectedItem is ComboBoxItem cbi) ? (string?)cbi.Tag ?? "openai_compat" : "openai_compat";

    private void BackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_state == null) return;
        _state.Config.Llm.Backend = SelectedBackend;
        UpdateBackendSpecificUI();
    }

    private void UpdateBackendSpecificUI()
    {
        if (_state == null) return;
        var be = SelectedBackend;
        var llm = _state.Config.Llm;

        BaseUrlPanel.Visibility = be == "openai_compat" ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyPanel.Visibility  = be == "claude_agent"  ? Visibility.Collapsed : Visibility.Visible;
        DisableToolsPanel.Visibility = be == "openai_compat" ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyOptionalLabel.Visibility =
            be == "openai_compat" ? Visibility.Visible : Visibility.Collapsed;

        // All three backends are shipped as of v0.6.0 — no more "lands in
        // v0.5.0" warning. Notice stays hidden.
        UnsupportedNotice.Visibility = Visibility.Collapsed;

        // Model dropdown items
        ModelCombo.Items.Clear();
        string[] models = be switch
        {
            "claude_agent"  => ClaudeAgentModels,
            "claude_api"    => ClaudeApiModels,
            _               => OpenAiCompatModels,
        };
        foreach (var m in models) ModelCombo.Items.Add(m);

        // If the current model isn't in the new list, pick a sensible default
        // when the user clearly switched backend families.
        if (string.IsNullOrWhiteSpace(llm.Model) ||
            Array.IndexOf(models, llm.Model) < 0)
        {
            if (be == "openai_compat" && (llm.Model ?? "").StartsWith("claude"))
                llm.Model = models[0];
            else if ((be == "claude_agent" || be == "claude_api") &&
                     !(llm.Model ?? "").StartsWith("claude"))
                llm.Model = models[0];
            else if (string.IsNullOrWhiteSpace(llm.Model))
                llm.Model = models[0];
        }
        ModelCombo.Text = llm.Model;

        HelpText.Text = be switch
        {
            "claude_agent" =>
                "Uses your Claude Pro/Max subscription via the Claude Code CLI. " +
                "No API key needed, but Claude Code must be installed and signed in. " +
                "Get it at https://claude.com/code",
            "claude_api" =>
                "Uses the Anthropic API. Get a key at https://console.anthropic.com/settings/keys " +
                "(keys start with 'sk-ant-').",
            "openai_compat" =>
                "Works with any OpenAI-compatible API:\n" +
                "  • OpenAI      — base URL https://api.openai.com/v1        (keys start sk-)\n" +
                "  • Groq        — base URL https://api.groq.com/openai/v1   (keys start gsk_)\n" +
                "  • xAI / Grok  — base URL https://api.x.ai/v1              (keys start xai-)\n" +
                "  • Ollama      — base URL http://localhost:11434/v1        (no key)\n" +
                "  • LM Studio   — base URL http://localhost:1234/v1         (no key)",
            _ => "",
        };
    }

    private void BaseUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.Config.Llm.BaseUrl = BaseUrlPreset.Text;
    }

    private void ApiKeyPwd_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.Config.Llm.ApiKey = ApiKeyPwd.Password;
        if (ApiKeyVisible.Visibility != Visibility.Visible)
            ApiKeyVisible.Text = ApiKeyPwd.Password;
    }

    private void ApiKeyVisible_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.Config.Llm.ApiKey = ApiKeyVisible.Text;
    }

    private void ShowKeyToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ShowKeyToggle.IsChecked == true)
        {
            ApiKeyVisible.Text       = ApiKeyPwd.Password;
            ApiKeyVisible.Visibility = Visibility.Visible;
            ApiKeyPwd.Visibility     = Visibility.Collapsed;
            ShowKeyToggle.Content    = "Hide";
        }
        else
        {
            ApiKeyPwd.Password       = ApiKeyVisible.Text;
            ApiKeyVisible.Visibility = Visibility.Collapsed;
            ApiKeyPwd.Visibility     = Visibility.Visible;
            ShowKeyToggle.Content    = "Show";
        }
    }

    private void DisableToolsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.Config.Llm.DisableTools = DisableToolsCheck.IsChecked == true;
    }

    private void Model_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_bound || _state == null) return;
        _state.Config.Llm.Model = ModelCombo.Text;
    }

    private void MaxTokens_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_bound || _state == null) return;
        if (int.TryParse(MaxTokensBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
            _state.Config.Llm.MaxTokens = n;
    }

    private async void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null) return;
        // Push visible key text into the model in case it was edited in Show mode.
        if (ApiKeyVisible.Visibility == Visibility.Visible)
            _state.Config.Llm.ApiKey = ApiKeyVisible.Text;

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();
        var localCts = _testCts;

        TestBtn.IsEnabled = false;
        TestStatus.Text = "Testing...";
        TestStatus.Foreground = Brushes.Gray;
        try
        {
            var result = await ApiTester.TestAsync(_state.Config.Llm, localCts.Token);
            // Don't touch the UI if THIS test was cancelled out from under us —
            // either a newer test took over or the page got navigated away.
            if (!localCts.IsCancellationRequested)
            {
                TestStatus.Text = result.Message;
                TestStatus.Foreground = result.Ok ? Brushes.Green : Brushes.Firebrick;
            }
        }
        catch (OperationCanceledException)
        {
            // Test was cancelled — caller (page nav or another Test click)
            // already owns the next UI state. Don't write anything.
        }
        catch (Exception ex)
        {
            // Defensive: never let an unhandled exception escape an async void
            // handler — that crashes the entire WPF process (which is what
            // caused the user's installer crash when they clicked Next while
            // a slow Ollama test was still running).
            if (!localCts.IsCancellationRequested)
            {
                try
                {
                    TestStatus.Text = $"Test failed: {ex.Message}";
                    TestStatus.Foreground = Brushes.Firebrick;
                }
                catch { /* page may be gone */ }
            }
        }
        finally
        {
            // Only re-enable if THIS test is still the latest one. Otherwise
            // a newer test (or a navigated-away page state) owns the button.
            if (ReferenceEquals(_testCts, localCts))
            {
                try { TestBtn.IsEnabled = true; } catch { /* page gone */ }
            }
        }
    }

    public Task<bool> OnNextAsync()
    {
        // Cancel any in-flight Test Connection BEFORE we allow navigation.
        // Without this, the test keeps running on the dispatcher and its
        // continuation crashes the app when it touches the navigated-away
        // page's UI elements. This was the v0.6.0 installer crash bug.
        try { _testCts?.Cancel(); } catch { }

        if (_state == null) return Task.FromResult(true);
        var llm = _state.Config.Llm;

        if (llm.Backend == "claude_api" && string.IsNullOrWhiteSpace(llm.ApiKey))
            return Confirm("No API key entered. Jarvis won't start until you add one. Continue anyway?");

        if (llm.Backend == "openai_compat")
        {
            if (string.IsNullOrWhiteSpace(llm.BaseUrl))
                return Confirm("No base URL entered. Continue anyway?");
            if (string.IsNullOrWhiteSpace(llm.ApiKey) &&
                !(llm.BaseUrl?.Contains("localhost", StringComparison.OrdinalIgnoreCase) ?? false) &&
                !(llm.BaseUrl?.Contains("127.0.0.1") ?? false))
                return Confirm("No API key entered for a remote OpenAI-compatible API. Continue anyway?");
        }
        return Task.FromResult(true);
    }

    private Task<bool> Confirm(string msg)
    {
        var r = MessageBox.Show(_host, msg, "Jarvis (.NET) Setup",
                                MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return Task.FromResult(r == MessageBoxResult.Yes);
    }
}
