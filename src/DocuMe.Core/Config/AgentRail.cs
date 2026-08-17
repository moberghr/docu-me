using System.Text.Json.Serialization;

namespace DocuMe.Core.Config;

/// <summary>
/// Which coding agent a consumer's model-running workflows are written for (PLAN.md §10, §11).
/// </summary>
/// <remarks>
/// <para>
/// Four of the six scaffolded workflows run only <c>docume</c> and <c>git</c> and are the same on
/// every rail. Two of them — <c>docs-refresh</c> and <c>docs-feedback</c> — invoke a model, and the
/// invocation is not portable: the CLI name, how the skill is loaded, which token authenticates and
/// how tools are granted all differ. The rail picks which spelling of those two a repo receives.
/// </para>
/// <para>
/// Deliberately an enum over a free string. The value selects an embedded template by name, so an
/// unconstrained string would turn a typo into a missing workflow — and a missing
/// <c>docs-refresh.yml</c> is silent: nothing fails, the nightly job simply never exists.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AgentRail>))]
public enum AgentRail
{
    /// <summary>
    /// Claude Code (<c>claude -p</c>), authenticated by <c>ANTHROPIC_API_KEY</c> and loading the
    /// DocuMe plugin for the session with <c>--plugin-dir</c>. The default: it is what every repo
    /// scaffolded before the Copilot rail existed received, so an existing consumer re-running
    /// <c>init</c> keeps what it has.
    /// </summary>
    [JsonStringEnumMemberName("claude")]
    Claude = 0,

    /// <summary>
    /// GitHub Copilot CLI (<c>copilot -p</c>), authenticated by <c>COPILOT_GITHUB_TOKEN</c> and
    /// finding the skill through a copy under a directory it scans. Chosen by teams who hold Copilot
    /// seats and would rather not own a second model credential.
    /// </summary>
    [JsonStringEnumMemberName("copilot")]
    Copilot = 1,
}
