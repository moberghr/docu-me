using System.ComponentModel;
using System.Diagnostics;
using DocuMe.Core.Confluence;

namespace DocuMe.Core.Status;

/// <summary>
/// The half of §6.6's <c>doctor</c>-lite that touches the world: are the credential variables set, is
/// Node there, is the render script there, is the space reachable. Each answers with one
/// <see cref="StatusCheck"/> and never throws.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Never throws, on purpose.</strong> A status command is what a human runs precisely when
/// something is broken, so a probe that threw on a missing token or an unplugged network would fail in
/// exactly the situation it exists for. Every failure here becomes a row in the report instead
/// (<see cref="StatusCheckOutcome.Problem"/> for something a publish would hit,
/// <see cref="StatusCheckOutcome.Warning"/> for something it might).
/// </para>
/// <para>
/// <strong>Nothing renders and nothing is written.</strong> <see cref="NodeAsync"/> asks Node its
/// version and stops there; whether <c>beautiful-mermaid</c> is installed is a question only an actual
/// render answers, and rendering belongs to <c>publish</c> and <c>convert</c>.
/// </para>
/// <para>
/// <strong>No credential ever reaches a detail string</strong> — not the token, not the account email,
/// not a truncation of either (CLAUDE.md §0.3, rule §1.1). "Authenticated as …" is deliberately absent:
/// a status report that echoed the account would be printing half a credential to answer a question
/// nobody asked.
/// </para>
/// </remarks>
public static class StatusProbes
{
    /// <summary>Name of the check that answers "are the credential variables set?".</summary>
    public const string CredentialsCheck = "credentials";

    /// <summary>Name of the check that answers §6.6's "node present?".</summary>
    public const string NodeCheck = "node";

    /// <summary>Name of the check that answers "is the mermaid render script where docume.json says?".</summary>
    public const string RendererCheck = "mermaid renderer";

    /// <summary>Name of the check that answers §6.6's "token valid? space reachable?".</summary>
    public const string ConfluenceCheck = "confluence";

    /// <summary>How long the Node version probe gets before it is killed.</summary>
    private static readonly TimeSpan NodeBudget = TimeSpan.FromSeconds(10);

    /// <summary>A check that did not run, and why (<c>--offline</c>, or no credentials).</summary>
    public static StatusCheck NotChecked(string name, string reason) =>
        new(name, StatusCheckOutcome.NotChecked, reason);

    /// <summary>
    /// Whether both credential variables are set (§4). Their <em>values</em> are never read into the
    /// answer — only whether each one is there.
    /// </summary>
    /// <param name="readVariable">
    /// How to read one variable; defaults to the process environment. A test passes its own lookup so
    /// no credential enters the test process.
    /// </param>
    public static StatusCheck Credentials(Func<string, string?>? readVariable = null)
    {
        var read = readVariable ?? Environment.GetEnvironmentVariable;

        var missing = new[] { ConfluenceCredentials.EmailVariable, ConfluenceCredentials.TokenVariable }
            .Where(variable => string.IsNullOrWhiteSpace(read(variable)))
            .ToArray();

        if (missing.Length == 0)
        {
            const string both =
                $"{ConfluenceCredentials.EmailVariable} and {ConfluenceCredentials.TokenVariable} are "
                + "both set. Whether the token still works is the confluence check below.";

            return new StatusCheck(CredentialsCheck, StatusCheckOutcome.Ok, both);
        }

        var detail = $"not set: {string.Join(", ", missing)}. `docume publish` and `docume sync` need "
            + "both; `convert` and this report do not. DocuMe reads them from the environment only — "
            + "never from docume.json, which is committed.";

        return new StatusCheck(CredentialsCheck, StatusCheckOutcome.Warning, detail);
    }

    /// <summary>
    /// Whether the mermaid render script named by <c>docume.json</c> → <c>mermaid.renderer</c> (§5.1) is
    /// where it says.
    /// </summary>
    /// <param name="rendererPath">The resolved absolute path.</param>
    public static StatusCheck Renderer(string rendererPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rendererPath);

        if (File.Exists(rendererPath))
        {
            return new StatusCheck(RendererCheck, StatusCheckOutcome.Ok, $"found at {rendererPath}.");
        }

        var detail = $"mermaid.renderer in docume.json points at {rendererPath}, which is not there. A "
            + "page with a ```mermaid fence cannot be published until it is; a wiki with no diagrams "
            + "never needs it.";

        return new StatusCheck(RendererCheck, StatusCheckOutcome.Warning, detail);
    }

    /// <summary>
    /// Whether Node can be started, and which version answers (§4 requires ≥ 20).
    /// </summary>
    /// <param name="nodeExecutable">The executable to try, resolved through <c>PATH</c> by default.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// A missing Node is a <see cref="StatusCheckOutcome.Warning"/> rather than a problem even though it
    /// breaks every diagram: this probe deliberately does not know whether the wiki has any. Counting
    /// diagrams means converting the tree, and letting the report's own plan feed its checks would make
    /// the probes depend on the thing they are supposed to check independently. The sentence says what
    /// breaks, so a reader with diagrams reads it as fatal and a reader without ignores it.
    /// </remarks>
    public static async Task<StatusCheck> NodeAsync(
        string nodeExecutable = "node",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeExecutable);

        var startInfo = new ProcessStartInfo(nodeExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--version");

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            var detail = $"'{nodeExecutable}' could not be started: {ex.Message} DocuMe renders mermaid "
                + "diagrams by shelling out to Node (≥ 20 required, §4), so a page with a ```mermaid "
                + "fence cannot be published until Node is on PATH.";

            return new StatusCheck(NodeCheck, StatusCheckOutcome.Warning, detail);
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NodeBudget);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);

            var detail = $"'{nodeExecutable} --version' did not answer within {NodeBudget.TotalSeconds} "
                + "seconds and was stopped.";

            return new StatusCheck(NodeCheck, StatusCheckOutcome.Warning, detail);
        }

        var version = (await standardOutput.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0 || version.Length == 0)
        {
            var detail = $"'{nodeExecutable} --version' exited {process.ExitCode} without a version.";

            return new StatusCheck(NodeCheck, StatusCheckOutcome.Warning, detail);
        }

        return new StatusCheck(NodeCheck, StatusCheckOutcome.Ok, $"{nodeExecutable} {version} (§4 wants ≥ 20).");
    }

    /// <summary>
    /// §6.6's "token valid? space reachable?" — both, in one request.
    /// </summary>
    /// <param name="client">A client built from the environment credentials.</param>
    /// <param name="spaceKey">The target space (<c>confluence.spaceKey</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// One call, not two: a 200 from <see cref="ConfluenceClient.FindSpaceByKeyAsync"/> proves the token
    /// was accepted <em>and</em> that the space is visible to the account, so probing them separately
    /// would spend a second request to learn nothing.
    /// </para>
    /// <para>
    /// A 401/403 is reported as an expired-or-revoked token and never retried — the whole of rule §1.2,
    /// enforced one layer down by <see cref="ConfluenceAuthenticationException"/>.
    /// </para>
    /// </remarks>
    public static async Task<StatusCheck> SpaceAsync(
        ConfluenceClient client,
        string spaceKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceKey);

        try
        {
            var space = await client.FindSpaceByKeyAsync(spaceKey, cancellationToken).ConfigureAwait(false);

            if (space is null)
            {
                var missing = $"the token was accepted, but space '{spaceKey}' came back empty: it does "
                    + "not exist, or this account cannot see it. The API answers both the same way, so "
                    + "check confluence.spaceKey first and the account's space permissions second.";

                return new StatusCheck(ConfluenceCheck, StatusCheckOutcome.Problem, missing);
            }

            var found = $"space {space.Key} '{space.Name}' (id {space.Id}) is reachable and the token was "
                + "accepted.";

            return new StatusCheck(ConfluenceCheck, StatusCheckOutcome.Ok, found);
        }
        catch (ConfluenceAuthenticationException ex)
        {
            return new StatusCheck(ConfluenceCheck, StatusCheckOutcome.Problem, ex.Message);
        }
        catch (ConfluenceException ex)
        {
            var detail = $"could not confirm space '{spaceKey}': {ex.Message}";

            return new StatusCheck(ConfluenceCheck, StatusCheckOutcome.Warning, detail);
        }
        catch (HttpRequestException ex)
        {
            var detail = $"could not reach Confluence: {ex.Message} This is a transport failure, not a "
                + "rejected token — check the network, a proxy, and confluence.baseUrl.";

            return new StatusCheck(ConfluenceCheck, StatusCheckOutcome.Warning, detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var detail = $"the request for space '{spaceKey}' timed out. Confluence may be slow or "
                + "unreachable; nothing was written either way.";

            return new StatusCheck(ConfluenceCheck, StatusCheckOutcome.Warning, detail);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone between the timeout firing and the kill.
        }
    }
}
