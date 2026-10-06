using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace ClawTui.Agent;

/// <summary>
/// Claude Code-style permission model:
/// - read_file / glob_files / grep are auto-approved (read-only),
/// - write_file / edit_file / run_bash always prompt the user in the TUI,
/// - the harness's "always allow" choices are remembered per tool (and per args)
///   for the rest of the session.
/// </summary>
public static class PermissionPolicy
{
    private static readonly HashSet<string> ReadOnlyTools =
        new(StringComparer.OrdinalIgnoreCase) { "read_file", "glob_files", "grep" };

    public static ToolApprovalAgentOptions Create() => new()
    {
        AutoApprovalRules =
        [
            ctx => ValueTask.FromResult(
                ctx.FunctionCallContent.Name is not null
                && ReadOnlyTools.Contains(ctx.FunctionCallContent.Name)),
        ],
        // Safety valve: never auto-approve more than this many calls in a row
        // without the model producing visible output.
        MaxAutoApprovalIterations = 200,
    };

    /// <summary>Tools that must prompt even if a standing "always allow" rule exists.</summary>
    public static bool IsMutating(string? toolName) =>
        toolName is "write_file" or "edit_file" or "run_bash";
}
