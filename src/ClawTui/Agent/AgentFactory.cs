using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace ClawTui.Agent;

/// <summary>
/// Builds the harness-based coding agent: model client + coding tools +
/// permission policy + plan/execute modes.
/// </summary>
public static class AgentFactory
{
    public sealed record Config(
        string WorkingDirectory,
        string Model,
        string ApiKey,
        string? BaseUrl);

    public static Config FromEnvironment(string workingDirectory, string? modelOverride)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException(
                "OPENAI_API_KEY is not set. Export it before starting clawtui.");
        var model = modelOverride
            ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
            ?? "gpt-5-mini";
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        return new Config(Path.GetFullPath(workingDirectory), model, apiKey, baseUrl);
    }

    public static HarnessAgent Create(Config config)
    {
        IChatClient chatClient = CreateChatClient(config);

        var tools = new CodingTools(config.WorkingDirectory);

        var agentTools = new List<AITool>
        {
            AIFunctionFactory.Create(
                new Func<string, int, int, string>(tools.ReadFile), "read_file"),
            AIFunctionFactory.Create(
                new Func<string, string>(tools.GlobFiles), "glob_files"),
            AIFunctionFactory.Create(
                new Func<string, string?, string?, string>(tools.Grep), "grep"),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                new Func<string, string, string>(tools.WriteFile), "write_file")),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                new Func<string, string, string, string>(tools.EditFile), "edit_file")),
            new ApprovalRequiredAIFunction(AIFunctionFactory.Create(
                new Func<string, int, string>(tools.RunBash), "run_bash")),
        };

        var options = new HarnessAgentOptions
        {
            Name = "claw",
            Description = "TUI coding agent",
            ChatOptions = new ChatOptions
            {
                Instructions = SystemPrompt(config.WorkingDirectory),
                Tools = agentTools,
            },
            HarnessInstructions =
                "You are a coding agent running inside a terminal UI. " +
                "Prefer small, verifiable steps: read before editing, run tests after changes. " +
                "Keep responses concise; the user sees tool calls inline.",
            ToolApprovalAgentOptions = PermissionPolicy.Create(),
            AgentModeProviderOptions = new AgentModeProviderOptions
            {
                Modes =
                [
                    new AgentModeProviderOptions.AgentMode("plan",
                        "Investigate and propose a plan without writing files or running mutating commands."),
                    new AgentModeProviderOptions.AgentMode("execute",
                        "Carry out the work: edit files and run commands as needed."),
                ],
                DefaultMode = "execute",
                Instructions = "In 'plan' mode, investigate and propose a plan without writing files or running mutating commands. " +
                               "In 'execute' mode, carry out the work.",
            },
            MaximumIterationsPerRequest = 60,
            // Keep v1 predictable: no web search, no file-memory sidecar.
            DisableWebSearch = true,
            DisableFileMemory = true,
        };

        // AsHarnessAgent lives in Microsoft.Extensions.AI (ChatClientHarnessExtensions).
        return chatClient.AsHarnessAgent(options);
    }

    private static IChatClient CreateChatClient(Config config)
    {
        ChatClient chatClient;
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            var client = new OpenAIClient(
                new System.ClientModel.ApiKeyCredential(config.ApiKey),
                new OpenAIClientOptions { Endpoint = new Uri(config.BaseUrl) });
            chatClient = client.GetChatClient(config.Model);
        }
        else
        {
            chatClient = new ChatClient(config.Model, config.ApiKey);
        }
        return chatClient.AsIChatClient();
    }

    private static string SystemPrompt(string workingDirectory) => $"""
        You are Claw, an expert coding assistant working in the repository at:
        {workingDirectory}

        Rules:
        - All file paths are relative to the working directory above. Never access paths outside it.
        - Read and understand code before changing it. Use glob_files/grep to explore.
        - Make the smallest change that solves the problem. Match existing code style.
        - After editing code, run the relevant build or tests with run_bash and fix failures.
        - Explain what you changed briefly; do not paste large code blocks unless asked.
        - If a request is ambiguous, ask a short clarifying question instead of guessing.
        - Never run destructive commands (rm -rf, mkfs, etc.); the shell tool refuses them.
        """;
}
