using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Jarvis.Core;
using Jarvis.Core.Conversation;
using Jarvis.Setup.NET.Models;

namespace Jarvis.App;

public partial class MainWindow : Window
{
    private readonly ConversationOrchestrator _orchestrator;
    private readonly VoiceController _voice;
    private readonly InstallConfig _cfg;

    /// <summary>Lines shown in the chat log. WPF-bound, mutated only on the
    /// dispatcher thread (the event pump marshals via Dispatcher.InvokeAsync).</summary>
    public ObservableCollection<ChatLine> Lines { get; } = new();

    private ChatLine? _streamingLine;  // the in-progress Jarvis line being appended to

    public MainWindow(ConversationOrchestrator orchestrator, VoiceController voice, InstallConfig cfg)
    {
        _orchestrator = orchestrator;
        _voice = voice;
        _cfg = cfg;
        InitializeComponent();
        ChatItems.ItemsSource = Lines;
        BackendLabel.Text = $"{_cfg.Llm.Backend} · {_cfg.Llm.Model}";

        // Seed a welcome line so the window isn't empty.
        Lines.Add(new ChatLine
        {
            Kind = LineKind.Info,
            Speaker = "Jarvis",
            Text = "Ready. Type a message below, or click 🎤 to enable voice mode.",
        });

        // Start the event pump as a background loop on the dispatcher's loop.
        _ = PumpEventsAsync();

        // Auto-start voice loop if config says so (mode=voice or always_on).
        // Loaded event fires AFTER the constructor's DI graph is settled.
        Loaded += async (_, __) =>
        {
            if (_cfg.Mode == "voice" || _cfg.Voice.AlwaysOn)
            {
                MicBtn.IsChecked = true;
                await _voice.StartAsync();
            }
        };
    }

    // ===================================================================
    //  Event pump — drains ConversationOrchestrator.Events and maps to UI
    // ===================================================================
    private async Task PumpEventsAsync()
    {
        await foreach (var ev in _orchestrator.Events)
        {
            // ConfigureAwait(true) so we resume on the dispatcher.
            await Dispatcher.InvokeAsync(() => HandleEvent(ev));
        }
    }

    private void HandleEvent(JarvisEvent ev)
    {
        switch (ev)
        {
            case StateChanged sc:
                ApplyState(sc.State);
                break;

            case UserMessage um:
                Lines.Add(new ChatLine
                {
                    Kind = LineKind.User,
                    Speaker = "You",
                    Text = um.Text,
                });
                ScrollToBottom();
                break;

            case JarvisToken jt:
                // First token of a new reply — open a new line. Subsequent
                // tokens append to that same line so the user sees streaming.
                if (_streamingLine == null)
                {
                    _streamingLine = new ChatLine
                    {
                        Kind = LineKind.Jarvis,
                        Speaker = "Jarvis",
                        Text = "",
                    };
                    Lines.Add(_streamingLine);
                }
                _streamingLine.Text += jt.Token;
                ScrollToBottom();
                break;

            case JarvisReplyComplete:
                _streamingLine = null;
                ScrollToBottom();
                break;

            case VoiceInfo vi:
                Lines.Add(new ChatLine
                {
                    Kind = vi.Severity == InfoSeverity.Error ? LineKind.Error : LineKind.Info,
                    Speaker = vi.Severity switch
                    {
                        InfoSeverity.Error => "Error",
                        InfoSeverity.Warning => "Warning",
                        _ => "(info)",
                    },
                    Text = vi.Message,
                });
                ScrollToBottom();
                break;

            case ToolInvoked ti:
                Lines.Add(new ChatLine
                {
                    Kind = LineKind.Info,
                    Speaker = "Tool",
                    Text = $"→ {ti.ToolName}({ti.ArgumentsJson})",
                });
                ScrollToBottom();
                break;

            case ToolResult tr:
                Lines.Add(new ChatLine
                {
                    Kind = LineKind.Info,
                    Speaker = "Tool",
                    Text = $"← {tr.ToolName}: {Truncate(tr.Result, 200)}",
                });
                ScrollToBottom();
                break;

            case BackendError be:
                Lines.Add(new ChatLine
                {
                    Kind = LineKind.Error,
                    Speaker = "Error",
                    Text = be.UserMessage,
                });
                ScrollToBottom();
                break;
        }
    }

    private void ApplyState(JarvisState state)
    {
        var (text, color) = state switch
        {
            JarvisState.Idle                  => ("Ready.",                  "#33CC66"),
            JarvisState.ListeningForWakeWord  => ("Listening for wake word", "#FFB300"),
            JarvisState.ListeningForCommand   => ("Listening...",            "#FFB300"),
            JarvisState.Transcribing          => ("Transcribing...",         "#1F6FEB"),
            JarvisState.AwaitingReply         => ("Thinking...",             "#1F6FEB"),
            JarvisState.Speaking              => ("Speaking...",             "#9C27B0"),
            _                                  => (state.ToString(),         "#888"),
        };
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(color));
    }

    private void ScrollToBottom() => ChatScroll.ScrollToEnd();

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    // ===================================================================
    //  Input handlers
    // ===================================================================
    private async void SendBtn_Click(object sender, RoutedEventArgs e) => await SendCurrent();
    private async void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
        {
            e.Handled = true;
            await SendCurrent();
        }
    }

    private async Task SendCurrent()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        InputBox.Text = "";
        await _orchestrator.SendAsync(text, CancellationToken.None);
    }

    private async void MicBtn_Click(object sender, RoutedEventArgs e)
    {
        // ToggleButton flips IsChecked BEFORE this handler runs, so the new
        // state IS what we should sync to.
        try
        {
            MicBtn.IsEnabled = false;
            if (MicBtn.IsChecked == true)
            {
                await _voice.StartAsync();
            }
            else
            {
                await _voice.StopAsync();
            }
        }
        catch (Exception ex)
        {
            Lines.Add(new ChatLine
            {
                Kind = LineKind.Error,
                Speaker = "Voice",
                Text = $"Voice toggle failed: {ex.Message}",
            });
            MicBtn.IsChecked = _voice.IsRunning;
        }
        finally
        {
            MicBtn.IsEnabled = true;
        }
    }

    // ===================================================================
    //  Tray + window lifecycle (close-to-tray, quit only via tray menu)
    // ===================================================================
    private void TrayIcon_Click(object sender, RoutedEventArgs e) => ToggleWindow();
    private void ShowMenu_Click(object sender, RoutedEventArgs e)
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    private void QuitMenu_Click(object sender, RoutedEventArgs e)
    {
        TrayIcon.Dispose();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Close-to-tray: hide instead of closing. Quit goes through the
        // tray menu's explicit Quit item.
        e.Cancel = true;
        Hide();
    }

    private void ToggleWindow()
    {
        if (IsVisible) Hide();
        else { Show(); WindowState = WindowState.Normal; Activate(); }
    }
}

// =======================================================================
//  ChatLine — one entry in the chat log. INotifyPropertyChanged for the
//  streaming Text property so JarvisToken appends update the view in-place.
// =======================================================================
public enum LineKind { User, Jarvis, Info, Error }

public class ChatLine : INotifyPropertyChanged
{
    public LineKind Kind { get; set; } = LineKind.Info;
    public string Speaker { get; set; } = "";

    private string _text = "";
    public string Text
    {
        get => _text;
        set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
    }

    public Brush BackgroundBrush => Kind switch
    {
        LineKind.User   => new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xEB)),
        LineKind.Jarvis => new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xF2)),
        LineKind.Error  => new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE7)),
        _               => Brushes.Transparent,
    };

    public Brush ForegroundBrush => Kind switch
    {
        LineKind.User   => Brushes.White,
        LineKind.Error  => new SolidColorBrush(Color.FromRgb(0xB0, 0x00, 0x20)),
        LineKind.Info   => new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x88)),
        _               => Brushes.Black,
    };

    public HorizontalAlignment HorizontalAlignment => Kind switch
    {
        LineKind.User => HorizontalAlignment.Right,
        _             => HorizontalAlignment.Left,
    };

    public FontStyle FontStyle => Kind == LineKind.Info ? FontStyles.Italic : FontStyles.Normal;

    public event PropertyChangedEventHandler? PropertyChanged;
}
