using ClawTui.Agent;
using ClawTui.Session;
using ClawTui.Tui;

// clawtui [--dir <path>] [--model <name>] [--resume]
//
// Env:
//   OPENAI_API_KEY   required (or any OpenAI-compatible key)
//   OPENAI_MODEL     default model (default: gpt-5-mini)
//   OPENAI_BASE_URL  optional OpenAI-compatible endpoint

string? GetArg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var dir = GetArg("--dir") ?? Directory.GetCurrentDirectory();
var modelOverride = GetArg("--model");
var resume = args.Contains("--resume");

string? resumeId = null;
if (resume)
{
    resumeId = TranscriptStore.MostRecent();
    if (resumeId is null)
    {
        Console.Error.WriteLine("No previous session found under ~/.clawtui/sessions.");
        return 1;
    }
    Console.WriteLine($"Resuming session {resumeId} …");
}

var transcript = new TranscriptStore(resumeId);

AgentFactory.Config config;
try
{
    config = AgentFactory.FromEnvironment(dir, modelOverride);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var agent = AgentFactory.Create(config);
await ChatApp.RunAsync(agent, transcript.SessionId, transcript,
    config.Model, config.WorkingDirectory, replay: resume);
return 0;
