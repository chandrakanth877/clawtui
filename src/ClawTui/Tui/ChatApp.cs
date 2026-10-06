using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using ClawTui.Session;

namespace ClawTui.Tui;

/// <summary>
/// Terminal.Gui front-end for the harness coding agent:
/// streaming conversation view, input box, approval dialogs, slash commands.
/// The agent loop runs on a background thread; all UI updates are marshalled
/// through Application.Invoke.
/// </summary>
public sealed class ChatApp
{
    private readonly HarnessAgent _agent;
    private readonly AgentSession _session;
    private readonly AgentModeProvider? _modeProvider;
    private readonly TranscriptStore _transcript;
    private readonly string _model;
    private readonly string _workingDir;
    private readonly bool _replay;

    private Window _window = null!;
    private TextView _conversation = null!;
    private InputField _input = null!;

    private readonly StringBuilder _log = new();
    private readonly object _logLock = new();
    private long _lastRenderTicks;

    private Task? _runTask;
    private CancellationTokenSource? _runCts;
    private string _mode = "execute";

    private ChatApp(HarnessAgent agent, AgentSession session, TranscriptStore transcript,
        string model, string workingDir, bool replay)
    {
        _agent = agent;
        _session = session;
        _transcript = transcript;
        _model = model;
        _workingDir = workingDir;
        _replay = replay;
        _modeProvider = agent.GetService(typeof(AgentModeProvider), null) as AgentModeProvider;
    }

    public static async Task RunAsync(HarnessAgent agent, string sessionId,
        TranscriptStore transcript, string model, string workingDir, bool replay)
    {
        // Note: HarnessAgent sessions are identified internally; --resume replays
        // the transcript into the view and continues in a fresh harness session.
        var session = await agent.CreateSessionAsync();
        var app = new ChatApp(agent, session, transcript, model, workingDir, replay);
        if (app._modeProvider is not null)
        {
            try
            {
                app._mode = await app._modeProvider.GetModeAsync(session, CancellationToken.None);
            }
            catch
            {
                // keep default
            }
        }

        Application.Init();
        try
        {
            app.BuildUi();
            if (replay)
            {
                app.ReplayTranscript();
            }
            app.PrintBanner();
            Application.Run(app._window);
        }
        finally
        {
            Application.Shutdown();
        }
    }

    // ---------- UI construction ----------

    /// <summary>TextField that raises <see cref="Submitted"/> on Enter.</summary>
    private sealed class InputField : TextField
    {
        public event Action? Submitted;

        protected override bool OnKeyDown(Key key)
        {
            if (key == Key.Enter)
            {
                Submitted?.Invoke();
                return true;
            }
            return base.OnKeyDown(key);
        }
    }

    /// <summary>Window that raises <see cref="CancelRequested"/> on Esc.</summary>
    private sealed class MainWindow : Window
    {
        public event Action? CancelRequested;

        protected override bool OnKeyDown(Key key)
        {
            if (key == Key.Esc)
            {
                CancelRequested?.Invoke();
                return true;
            }
            return base.OnKeyDown(key);
        }
    }

    private void BuildUi()
    {
        var main = new MainWindow { Title = Title() };
        main.CancelRequested += () =>
        {
            if (_runTask is not null)
            {
                _runCts?.Cancel();
                AppendSystem("cancelling…");
            }
        };
        _window = main;

        _conversation = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 2,
            ReadOnly = true,
            WordWrap = true,
        };

        var prompt = new Label
        {
            X = 0,
            Y = Pos.Bottom(_conversation),
            Width = 2,
            Height = 1,
            Text = "❯",
        };

        _input = new InputField
        {
            X = 2,
            Y = Pos.Bottom(_conversation),
            Width = Dim.Fill() - 2,
            Height = 1,
        };
        _input.Submitted += Submit;

        var statusBar = new StatusBar(new[]
        {
            new Shortcut(Key.F1, "Help", ShowHelp, "Show help"),
            new Shortcut(Key.F9, "Quit", () => Application.RequestStop(_window), "Quit"),
        })
        {
            X = 0,
            Y = Pos.Bottom(_input),
            Width = Dim.Fill(),
            Height = 1,
        };

        _window.Add(_conversation, prompt, _input, statusBar);
        _input.SetFocus();
    }

    private string Title() => $"clawtui — {_workingDir} [{_model}] · {_mode} mode";

    // ---------- rendering (UI thread only via Invoke) ----------

    private void AppendRaw(string text)
    {
        lock (_logLock)
        {
            _log.Append(text);
        }
        RenderThrottled();
    }

    private void AppendLine(string text = "")
    {
        AppendRaw(text + "\n");
        RenderNow(); // structural lines render immediately
    }

    private void AppendSystem(string text) => AppendLine($"· {text}");

    private void RenderThrottled()
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastRenderTicks) < TimeSpan.TicksPerMillisecond * 120)
        {
            return;
        }
        RenderNow();
    }

    private void RenderNow()
    {
        Interlocked.Exchange(ref _lastRenderTicks, DateTime.UtcNow.Ticks);
        string text;
        lock (_logLock)
        {
            text = _log.ToString();
        }
        Application.Invoke(() =>
        {
            _conversation.Text = text;
            _conversation.MoveEnd();
        });
    }

    private void ClearView()
    {
        lock (_logLock)
        {
            _log.Clear();
        }
        RenderNow();
    }

    private void PrintBanner()
    {
        AppendLine($"ClawTui — harness coding agent  ·  session {_transcript.SessionId}");
        AppendLine($"Model: {_model}   Dir: {_workingDir}");
        AppendLine("Type /help for commands. Enter sends · Esc cancels a run · F9 quits.");
        AppendLine(new string('─', 60));
    }

    private void ReplayTranscript()
    {
        foreach (var (role, text) in _transcript.Load())
        {
            switch (role)
            {
                case "user": AppendLine($"❯ {text}"); break;
                case "assistant": AppendLine(text); AppendLine(); break;
            }
        }
        if (_log.Length > 0)
        {
            AppendLine(new string('─', 60));
        }
    }

    // ---------- input & commands ----------

    private void Submit()
    {
        if (_runTask is not null)
        {
            return; // a run is in flight; Esc cancels it
        }
        var text = _input.Text?.Trim() ?? "";
        if (text.Length == 0)
        {
            return;
        }
        _input.Text = "";
        if (text.StartsWith('/'))
        {
            HandleCommand(text);
            return;
        }
        StartRun(text);
    }

    private void StartRun(string text)
    {
        var cts = new CancellationTokenSource();
        _runCts = cts;
        _runTask = RunTurnAsync(text, cts.Token);
        _runTask.ContinueWith(_ =>
        {
            _runTask = null;
            _runCts = null;
        }, TaskScheduler.Default);
    }

    private void HandleCommand(string text)
    {
        var parts = text[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        AppendLine($"❯ {text}");

        switch (cmd)
        {
            case "help":
                ShowHelpText();
                break;
            case "clear":
                ClearView();
                break;
            case "exit":
            case "quit":
                Application.RequestStop(_window);
                break;
            case "mode" when arg is "plan" or "execute":
                _ = SetModeAsync(arg);
                break;
            case "mode":
                AppendSystem($"Current mode: {_mode}. Use /mode plan|execute to switch.");
                break;
            case "init":
                StartRun("Analyze this repository and create an AGENTS.md in the root describing " +
                         "how to build, test, and contribute to it. Keep it concise.");
                break;
            default:
                AppendSystem($"Unknown command: /{cmd}. Try /help.");
                break;
        }
    }

    private async Task SetModeAsync(string mode)
    {
        if (_modeProvider is null)
        {
            AppendSystem("Mode provider unavailable.");
            return;
        }
        try
        {
            await _modeProvider.SetModeAsync(_session, mode, CancellationToken.None);
            _mode = mode;
            Application.Invoke(() => _window.Title = Title());
            AppendSystem($"Mode set to {mode}.");
        }
        catch (Exception ex)
        {
            AppendSystem($"Could not set mode: {ex.Message}");
        }
    }

    private void ShowHelp() => Application.Invoke(ShowHelpText);

    private void ShowHelpText()
    {
        AppendLine("Commands:");
        AppendLine("  /help            show this help");
        AppendLine("  /mode [plan|execute]  switch agent mode");
        AppendLine("  /init            generate AGENTS.md for this repo");
        AppendLine("  /clear           clear the conversation view");
        AppendLine("  /exit            quit");
        AppendLine("Keys: Enter send · Esc cancel run · F9 quit");
        AppendLine("Tool approvals: read-only tools auto-run; writes and shell commands prompt.");
    }

    // ---------- agent turn ----------

    private async Task RunTurnAsync(string userText, CancellationToken ct)
    {
        try
        {
            await RunTurnInnerAsync(userText, ct);
        }
        catch (OperationCanceledException)
        {
            AppendSystem("cancelled.");
        }
        catch (Exception ex)
        {
            AppendSystem($"Error: {ex.Message}");
        }
    }

    private async Task RunTurnInnerAsync(string userText, CancellationToken ct)
    {
        _transcript.Append("user", userText);
        AppendLine($"❯ {userText}");

        var pending = new List<ChatMessage> { new(ChatRole.User, userText) };
        var callNames = new Dictionary<string, string>();
        var assistantText = new StringBuilder();

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var approvals = new List<ToolApprovalRequestContent>();

            await foreach (var update in _agent.RunStreamingAsync(
                               pending, _session, cancellationToken: ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                {
                    assistantText.Append(update.Text);
                    AppendRaw(update.Text);
                }
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case ToolApprovalRequestContent req:
                            approvals.Add(req);
                            break;
                        case FunctionCallContent fcc:
                            if (fcc.CallId is not null)
                            {
                                callNames[fcc.CallId] = fcc.Name ?? "tool";
                            }
                            AppendLine();
                            AppendLine($"⚙ {fcc.Name}{FormatArgs(fcc.Arguments)}");
                            break;
                        case FunctionResultContent frc:
                            var name = frc.CallId is not null && callNames.TryGetValue(frc.CallId, out var n) ? n : "tool";
                            AppendLine($"  → {name}: {Summarize(frc.Result)}");
                            break;
                    }
                }
            }

            pending.Clear();
            RenderNow();

            foreach (var req in approvals)
            {
                ct.ThrowIfCancellationRequested();
                var decision = await PromptApprovalAsync(req);
                var fcc = req.ToolCall as FunctionCallContent;
                AIContent response = decision switch
                {
                    "always" => req.CreateAlwaysApproveToolResponse("User chose always-allow in TUI"),
                    "deny" => req.CreateResponse(false, "User denied in TUI"),
                    _ => req.CreateResponse(true, "User approved in TUI"),
                };
                pending.Add(new ChatMessage(ChatRole.User, [response]));
                _transcript.Append("approval", $"{decision}: {fcc?.Name}");
                AppendLine(decision == "deny"
                    ? $"  ✗ denied {fcc?.Name}"
                    : $"  ✓ approved {fcc?.Name} ({decision})");
            }
        }

        if (assistantText.Length > 0)
        {
            _transcript.Append("assistant", assistantText.ToString());
        }
        AppendLine();
    }

    private async Task<string> PromptApprovalAsync(ToolApprovalRequestContent req)
    {
        var fcc = req.ToolCall as FunctionCallContent;
        var name = fcc?.Name ?? "tool";
        string args = "{}";
        if (fcc?.Arguments is { } a)
        {
            try
            {
                args = JsonSerializer.Serialize(a,
                    new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                // keep "{}"
            }
        }

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Application.Invoke(() =>
        {
            try
            {
                int choice = MessageBox.Query(Application.Instance, "Tool approval",
                    $"The agent wants to run:\n\n{name}\n{Truncate(args, 1500)}\n\nAllow?",
                    "Allow", "Always allow", "Deny") ?? 2; // dismissed = deny
                tcs.SetResult(choice);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        return await tcs.Task switch
        {
            1 => "always",
            2 => "deny",
            _ => "once",
        };
    }

    // ---------- formatting helpers ----------

    private static string FormatArgs(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
        {
            return "";
        }
        // Show the most informative single argument inline (path/command/pattern).
        foreach (var key in new[] { "path", "command", "pattern" })
        {
            if (args.TryGetValue(key, out var v) && v is not null)
            {
                return $" ({Truncate(v.ToString() ?? "", 80)})";
            }
        }
        return "";
    }

    private static string Summarize(object? result)
    {
        var s = result?.ToString() ?? "";
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return Truncate(s, 160);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
