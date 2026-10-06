# ClawTui — a Claude Code-style TUI coding agent on .NET + Microsoft Agent Framework

A terminal UI coding agent: you type requests, a harness-based agent
(Microsoft Agent Framework `HarnessAgent`) plans and executes with real tools,
and every mutating action asks for your approval inline.

```
┌ clawtui — /path/to/repo [gpt-5-mini] · execute mode ─────────────┐
│ ❯ add retry logic to the http client                              │
│ I'll look at the HTTP client code first.                          │
│                                                                   │
│ ⚙ glob_files (**/*http*.cs)                                       │
│   → glob_files: src/HttpClient.cs                                 │
│ ⚙ read_file (src/HttpClient.cs)                                   │
│   → read_file: 1: using System; …                                 │
│ ⚙ edit_file (src/HttpClient.cs)                                   │
│   Tool approval?  [Allow] [Always allow] [Deny]                    │
│   ✓ approved edit_file (once)                                     │
│ Done — added exponential backoff with jitter. Tests pass.         │
│                                                                   │
│ ❯▌                                                                │
├───────────────────────────────────────────────────────────────────┤
│ F1 Help · F9 Quit · Esc cancels the run                            │
└───────────────────────────────────────────────────────────────────┘
```

## Prerequisites

- .NET 10 SDK
- An OpenAI API key (or any OpenAI-compatible endpoint)

## Run

```bash
export OPENAI_API_KEY="sk-..."
# optional:
export OPENAI_MODEL="gpt-5-mini"          # default
export OPENAI_BASE_URL="https://..."      # OpenAI-compatible endpoint

cd src/ClawTui
dotnet run -- --dir /path/to/your/repo
dotnet run -- --dir /path/to/your/repo --model gpt-5.4 --resume
```

## Commands & keys

| Input | Action |
|---|---|
| `/help` | show help |
| `/mode plan\|execute` | switch agent mode (plan = no mutations) |
| `/init` | generate `AGENTS.md` for the repo |
| `/clear` | clear the conversation view |
| `/exit` | quit |
| `Enter` | send · `Esc` cancels a run · `F9` quits |

## How it works

- **Agent** (`Agent/AgentFactory.cs`) — builds a `HarnessAgent` via
  `chatClient.AsHarnessAgent(...)` with plan/execute modes, todo tracking,
  and tool approval. The TUI drives `RunStreamingAsync` directly (the same
  loop as MAF's `HarnessAgentRunner`) so approvals render as native dialogs.
- **Tools** (`Agent/CodingTools.cs`) — `read_file`, `glob_files`, `grep`
  (auto-approved); `write_file`, `edit_file`, `run_bash` (always prompt).
  All paths are confined to the working directory; the shell has a hard
  deny-list (`rm -rf /`, fork bombs, …) plus timeouts.
- **Permissions** (`Agent/PermissionPolicy.cs`) — read-only tools
  auto-approve via `ToolApprovalAgentOptions.AutoApprovalRules`; "Always
  allow" in the dialog records a standing rule for the session.
- **TUI** (`Tui/ChatApp.cs`) — Terminal.Gui: streaming conversation view,
  input box, approval dialogs, slash commands, status bar. The agent loop
  runs on a background thread; UI updates marshal through
  `Application.Invoke`.
- **Sessions** (`Session/TranscriptStore.cs`) — transcripts saved as JSONL
  under `~/.clawtui/sessions/`; `--resume` replays the last one.

## Roadmap

- Diff viewer in the approval dialog (currently shows raw args JSON)
- Token/cost counter in the status bar
- `@file` mentions to pin context, `/compact` manual compaction
- Swap the hand-rolled shell for `Microsoft.Agents.AI.Tools.Shell`
  (preview) or `HyperlightCodeActProvider` for sandboxed CodeAct execution

Built against `Microsoft.Agents.AI.Harness` 1.23.0, `Microsoft.Agents.AI.OpenAI`
1.23.0, Terminal.Gui 2.5.0.
