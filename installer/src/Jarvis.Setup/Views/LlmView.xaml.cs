using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jarvis.Setup.Models;
using Jarvis.Setup.Services;

namespace Jarvis.Setup.Views;

public partial class LlmView : UserControl
{
    private InstallConfig? _cfg;
    private CancellationTokenSource? _testCts;

    private static readonly string[] ClaudeAgentModels = { "claude-sonnet-4-6", "claude-opus-4", "claude-haiku-4-5" };
    private static readonly string[] ClaudeApiModels   = { "claude-sonnet-4-6", "claude-opus-4", "claude-haiku-4-5" };
    private static readonly string[] OpenAiCompatModels = {
        "llama-3.3-70b-versatile", "llama-3.1-8b-instant",
        "gpt-4o-mini", "gpt-4o",
        "grok-4", "grok-4-fast",
        "llama3.1", "qwen2.5",
    };

    public LlmView()
    {
        InitializeComponent();
    }

    public void Bind(InstallConfig cfg)
    {
        _cfg = cfg;
        DataContext = cfg;

        // Select the right ComboBox item for the backend value.
        foreach (var raw in BackendCombo.Items)
        {
            if (raw is ComboBoxItem cbi && (string?)cbi.Tag == cfg.Llm.Backend)
            {
                BackendCombo.SelectedItem = cbi;
                break;
            }
        }
        if (BackendCombo.SelectedItem == null && BackendCombo.Items.Count > 0)
            BackendCombo.SelectedIndex = 0;

        // Initialize the password box (it doesn't bind directly).
        ApiKeyPwd.Password = cfg.Llm.ApiKey ?? "";
        ApiKeyVisible.Text  = cfg.Llm.ApiKey ?? "";

        UpdateBackendSpecificUI();
    }

    private string SelectedBackend =>
        (BackendCombo.SelectedItem is ComboBoxItem cbi) ? (string?)cbi.Tag ?? "claude_agent" : "claude_agent";

    private void BackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_cfg == null) return;
        _cfg.Llm.Backend = SelectedBackend;
        UpdateBackendSpecificUI();
    }

    private void UpdateBackendSpecificUI()
    {
        if (_cfg == null) return;
        var be = SelectedBackend;

        BaseUrlPanel.Visibility = be == "openai_compat" ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyPanel.Visibility  = be == "claude_agent" ? Visibility.Collapsed : Visibility.Visible;

        // Model dropdown items
        ModelCombo.Items.Clear();
        string[] models = be switch
        {
            "claude_agent"  => ClaudeAgentModels,
            "claude_api"    => ClaudeApiModels,
            _               => OpenAiCompatModels,
        };
        foreach (var m in models) ModelCombo.Items.Add(m);

        // If the current model isn't in the new list, leave the user's typed value.
        if (string.IsNullOrWhiteSpace(_cfg.Llm.Model) ||
            !System.Array.Exists(models, m => m == _cfg.Llm.Model))
        {
            // Keep what user has, just don't snap a default — but if they had a
            // Claude model and switched to openai_compat, pick a sensible default.
            if (be == "openai_compat" && (_cfg.Llm.Model ?? "").StartsWith("claude"))
                _cfg.Llm.Model = models[0];
            else if ((be == "claude_agent" || be == "claude_api") &&
                     !(_cfg.Llm.Model ?? "").StartsWith("claude"))
                _cfg.Llm.Model = models[0];
        }
        ModelCombo.Text = _cfg.Llm.Model;

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
                "  • Groq        — base URL https://api.groq.com/openai/v1   (keys start gsk_)\n" +
                "  • OpenAI      — base URL https://api.openai.com/v1        (keys start sk-)\n" +
                "  • xAI / Grok  — base URL https://api.x.ai/v1              (keys start xai-)\n" +
                "  • Ollama      — base URL http://localhost:11434/v1        (no key)\n" +
                "  • LM Studio   — base URL http://localhost:1234/v1         (no key)",
            _ => "",
        };
    }

    private void ApiKeyPwd_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_cfg == null) return;
        _cfg.Llm.ApiKey = ApiKeyPwd.Password;
        if (ApiKeyVisible.Visibility != Visibility.Visible)
            ApiKeyVisible.Text = ApiKeyPwd.Password;
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

    private async void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg == null) return;

        // Push the visible api key text into the model in case it was edited
        // there directly (Show mode).
        if (ApiKeyVisible.Visibility == Visibility.Visible)
            _cfg.Llm.ApiKey = ApiKeyVisible.Text;

        _testCts?.Cancel();
        _testCts = new CancellationTokenSource();

        TestBtn.IsEnabled = false;
        TestStatus.Text = "Testing...";
        TestStatus.Foreground = Brushes.Gray;
        try
        {
            var result = await ApiTester.TestAsync(_cfg.Llm, _testCts.Token);
            TestStatus.Text = result.Message;
            TestStatus.Foreground = result.Ok ? Brushes.Green : Brushes.Firebrick;
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }
}
