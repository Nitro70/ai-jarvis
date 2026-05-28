using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jarvis.Settings.NET.Services;
using Jarvis.Setup.NET.Models;

namespace Jarvis.Settings.NET.Views;

public partial class LlmView : UserControl
{
    private InstallConfig? _cfg;
    private CancellationTokenSource? _testCts;

    private static readonly string[] OpenAiCompatModels = {
        "llama-3.3-70b-versatile", "llama-3.1-8b-instant",
        "gpt-4o-mini", "gpt-4o",
        "grok-4", "grok-4-fast",
        "llama3.1", "qwen2.5",
    };
    private static readonly string[] ClaudeAgentModels = { "claude-sonnet-4-6", "claude-opus-4", "claude-haiku-4-5" };
    private static readonly string[] ClaudeApiModels   = { "claude-sonnet-4-6", "claude-opus-4", "claude-haiku-4-5" };

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

        // PasswordBox doesn't support binding for security reasons; seed
        // it manually.
        ApiKeyPwd.Password = cfg.Llm.ApiKey ?? "";
        ApiKeyVisible.Text  = cfg.Llm.ApiKey ?? "";

        UpdateBackendSpecificUI();
    }

    /// <summary>
    /// Force any pending UI edits (PasswordBox content while in hidden mode)
    /// back into the config model. Called by MainWindow before Save.
    /// </summary>
    public void Flush()
    {
        if (_cfg == null) return;
        if (ApiKeyVisible.Visibility == Visibility.Visible)
            _cfg.Llm.ApiKey = ApiKeyVisible.Text;
        else
            _cfg.Llm.ApiKey = ApiKeyPwd.Password;
    }

    private string SelectedBackend =>
        (BackendCombo.SelectedItem is ComboBoxItem cbi)
            ? (string?)cbi.Tag ?? "openai_compat"
            : "openai_compat";

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
        DisableToolsPanel.Visibility = be == "openai_compat" ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyOptionalLabel.Visibility =
            be == "openai_compat" ? Visibility.Visible : Visibility.Collapsed;

        // Model dropdown items
        ModelCombo.Items.Clear();
        string[] models = be switch
        {
            "claude_agent"  => ClaudeAgentModels,
            "claude_api"    => ClaudeApiModels,
            _               => OpenAiCompatModels,
        };
        foreach (var m in models) ModelCombo.Items.Add(m);

        if (string.IsNullOrWhiteSpace(_cfg.Llm.Model) ||
            !System.Array.Exists(models, m => m == _cfg.Llm.Model))
        {
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
                "Coming in Jarvis (.NET) v0.5.0. The .NET edition currently only ships " +
                "the openai_compat backend; pick that for now.",
            "claude_api" =>
                "Coming in Jarvis (.NET) v0.5.0. The .NET edition currently only ships " +
                "the openai_compat backend; pick that for now.",
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
        Flush();

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
