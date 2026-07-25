using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using DocuMe.Core.Config;
using DocuMe.Core.Confluence;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using DocuMe.Core.Sync;
using Spectre.Console;

namespace DocuMe.Cli.Commands;

/// <summary>
/// <c>docume sync</c> — PLAN.md §6.3. Reads the <c>approved</c> and <c>stale</c> labels out of the
/// configured space with two CQL searches and reconciles them into <c>_meta/state.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It writes nothing to Confluence.</strong> A sync is a read plus a state-file write: the
/// human gesture is the label (§8), and the only label writes in the design are publish's invalidation
/// (§6.2 step 7) and <c>drift --mark</c> (§6.4). It reads no page bodies either — rule §9.1 makes the
/// repo the source of truth, so a label and a version number are all it learns.
/// </para>
/// <para>
/// <strong>Committing is deliberately not its job</strong> (§6.3's closing line). The cron workflow
/// that runs it commits the changed state file to a <c>docs/sync</c> branch and opens a PR, because
/// direct pushes to protected branches do not work in this org.
/// </para>
/// <para>
/// <strong>Half of §6.3 is not built yet.</strong> §6.3's default is both halves, but comment
/// ingestion is M4, so today <c>--labels</c> is the whole command and a bare <c>sync</c> runs it. There
/// is no <c>--comments</c> flag to pass: an accepted flag that quietly did nothing would be worse than
/// an unknown-option error, and the help text says when it arrives.
/// </para>
/// </remarks>
internal static class SyncCommand
{
    /// <summary>Where <c>docume init</c> scaffolds the state file, relative to the wiki root (§5.3).</summary>
    private const string DefaultStateFile = "_meta/state.json";

    /// <summary>
    /// The <c>approvedAt</c> shape: ISO-8601 UTC to the second. No fractional part, because this string
    /// lands in a committed file that humans read in PR diffs.
    /// </summary>
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static Command Build()
    {
        var configOption = new Option<string>("--config")
        {
            Description = "Path to docume.json. Its directory is the repo root wiki.root resolves against.",
            DefaultValueFactory = _ => ConfigLoader.DefaultFileName,
        };
        var stateOption = new Option<string>("--state")
        {
            Description = $"Path to state.json. Defaults to <wiki.root>/{DefaultStateFile}.",
        };
        var labelsOption = new Option<bool>("--labels")
        {
            Description = "Reconcile the approved/stale labels into state. The only half implemented, "
                + "so a bare `sync` does it anyway; comment ingestion (--comments) arrives in M4.",
        };
        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Report what would change in state.json and write nothing.",
        };

        const string description =
            "Read the approved/stale labels out of Confluence and reconcile them into state.json. "
            + "Writes nothing to Confluence; committing the result is the caller's job.";

        var command = new Command("sync", description)
        {
            configOption,
            stateOption,
            labelsOption,
            dryRunOption,
        };

        command.SetAction((parseResult, cancellationToken) => RunAsync(
            parseResult.GetValue(configOption)!,
            parseResult.GetValue(stateOption),
            parseResult.GetValue(dryRunOption),
            cancellationToken));

        return command;
    }

    private static async Task<int> RunAsync(
        string configPath,
        string? statePath,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var fullConfigPath = Path.GetFullPath(configPath);

        DocumeConfig config;
        try
        {
            config = ConfigLoader.Load(fullConfigPath);
        }
        catch (ConfigNotFoundException ex)
        {
            return Fail(ex.Message);
        }
        catch (ConfigValidationException ex)
        {
            return Fail(ex.Message);
        }
        catch (JsonException ex)
        {
            return Fail($"{fullConfigPath} is not valid JSON: {ex.Message}");
        }

        if (config.Confluence.SpaceKey is not { Length: > 0 } spaceKey)
        {
            return Fail(
                "confluence.spaceKey is not set in docume.json, and a label search is scoped to a space "
                + "(PLAN.md §6.3: `space = X AND label = approved`).");
        }

        var repoRoot = Path.GetDirectoryName(fullConfigPath) ?? Directory.GetCurrentDirectory();
        var wikiRoot = Path.GetFullPath(Path.Combine(repoRoot, config.Wiki.Root));
        var resolvedStatePath = statePath is { Length: > 0 }
            ? Path.GetFullPath(statePath)
            : Path.Combine(wikiRoot, DefaultStateFile.Replace('/', Path.DirectorySeparatorChar));

        DocumeState state;
        try
        {
            state = StateStore.Load(resolvedStatePath);
        }
        catch (FileNotFoundException)
        {
            return Fail(
                $"No state file at {resolvedStatePath}. A sync reconciles labels onto pages a publish "
                + "recorded, so there is nothing to reconcile until `docume publish` has run.");
        }
        catch (StateVersionException ex)
        {
            return Fail(ex.Message);
        }
        catch (JsonException ex)
        {
            return Fail($"{resolvedStatePath} is not valid JSON: {ex.Message}");
        }

        ConfluenceCredentials credentials;
        try
        {
            credentials = ConfluenceCredentials.FromEnvironment();
        }
        catch (ConfluenceCredentialsException ex)
        {
            return Fail(ex.Message);
        }

        if (!Uri.TryCreate(config.Confluence.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            return Fail(
                $"confluence.baseUrl '{config.Confluence.BaseUrl}' is not an absolute URL. It should look "
                + "like https://your-site.atlassian.net/wiki (PLAN.md §5.1).");
        }

        // The write lock is surfaced, not enforced: this command writes nothing to Confluence, so a
        // protected space is worth knowing about (the labels being read belong to a space this repo is
        // not cleared to publish to) without being a refusal. `docume status` reports it the same way.
        if (PublishGuard.WriteRefusal(config.Confluence, allowProtectedSpace: false) is { } refusal)
        {
            AnsiConsole.MarkupLine($"[yellow]note[/] — {refusal.EscapeMarkup()}");
            AnsiConsole.MarkupLine(
                "[grey]Reading labels from it anyway: a sync writes nothing to Confluence.[/]");
        }

        using var client = ConfluenceClient.Create(new ConfluenceClientOptions { BaseUrl = baseUrl }, credentials);

        AnsiConsole.MarkupLine(
            $"Reading labels from [blue]{spaceKey.EscapeMarkup()}[/] at "
            + $"[blue]{baseUrl.ToString().EscapeMarkup()}[/]…");

        try
        {
            return await ReconcileAsync(
                    client,
                    config,
                    state,
                    spaceKey,
                    resolvedStatePath,
                    dryRun,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ConfluenceException ex)
        {
            return Fail(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Fail($"Confluence is unreachable at {baseUrl}: {ex.Message}");
        }
    }

    /// <summary>
    /// The two searches, the version fill-in, the plan, and the one state write.
    /// </summary>
    private static async Task<int> ReconcileAsync(
        ConfluenceClient client,
        DocumeConfig config,
        DocumeState state,
        string spaceKey,
        string statePath,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var approved = await client
            .SearchPagesByLabelAsync(spaceKey, config.Labels.Approved, cancellationToken)
            .ConfigureAwait(false);

        var stale = await client
            .SearchPagesByLabelAsync(spaceKey, config.Labels.Stale, cancellationToken)
            .ConfigureAwait(false);

        var titles = Titles(approved, stale);
        var versions = await VersionsAsync(client, approved, state, cancellationToken).ConfigureAwait(false);

        var observation = new LabelObservation(
            approved.Select(page => page.Id).ToArray(),
            stale.Select(page => page.Id).ToArray(),
            versions,
            DateTimeOffset.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));

        var plan = LabelSyncPlanner.Plan(state, observation);

        AnsiConsole.MarkupLine(
            $"[green]{approved.Count}[/] page(s) labelled [blue]{config.Labels.Approved.EscapeMarkup()}[/], "
            + $"[green]{stale.Count}[/] labelled [blue]{config.Labels.Stale.EscapeMarkup()}[/].");

        Render(plan, titles);

        if (!plan.HasChanges)
        {
            AnsiConsole.MarkupLine("[green]IN SYNC[/] — state already matches the labels. Nothing written.");
            return 0;
        }

        if (dryRun)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]--dry-run[/] — {plan.ChangeCount} change(s) planned, "
                + $"{statePath.EscapeMarkup()} left alone.");

            return 0;
        }

        StateStore.Save(statePath, LabelSyncPlanner.Apply(state, plan));

        AnsiConsole.MarkupLine($"State written: [blue]{statePath.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine(
            "[grey]Committing is not this command's job (§6.3) — the sync workflow commits the change to "
            + "a docs/sync branch and opens a PR.[/]");

        return 0;
    }

    /// <summary>
    /// Page id → the version current at observation time (§8), for the pages an approval may be recorded
    /// against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The search is asked for <c>expand=version</c>, which is where nearly every version should come
    /// from: one request per label rather than one per page. A hit that answered no version is read by
    /// id, and only if state manages it — a labelled page DocuMe does not publish is reported and
    /// skipped, so paying a request to learn its version would be paying for nothing.
    /// </para>
    /// <para>
    /// <strong>Not <c>state.publishedVersion</c> as a fallback.</strong> The two differ exactly when a
    /// human edited the page in a browser, which is the case §8's "version current at observation time"
    /// exists for; a page whose version cannot be established is left out of the map, and the reconciler
    /// then declines to restamp rather than recording a version nobody observed.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, int>> VersionsAsync(
        ConfluenceClient client,
        IReadOnlyList<ConfluenceLabelledPage> approved,
        DocumeState state,
        CancellationToken cancellationToken)
    {
        var managed = PageHierarchy.PathsByPageId(state);
        var versions = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var page in approved)
        {
            if (page.Version is { } version)
            {
                versions[page.Id] = version;
                continue;
            }

            if (!managed.ContainsKey(page.Id))
            {
                continue;
            }

            var read = await client.FindPageByIdAsync(page.Id, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (read?.Version is { } current)
            {
                versions[page.Id] = current;
            }
        }

        return versions;
    }

    /// <summary>
    /// Page id → title, from the search results, so an unmanaged page can be named rather than only
    /// numbered. The reconciler carries no titles by design (see <see cref="UnmanagedLabelledPage"/>).
    /// </summary>
    private static Dictionary<string, string> Titles(
        IReadOnlyList<ConfluenceLabelledPage> approved,
        IReadOnlyList<ConfluenceLabelledPage> stale)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var page in approved.Concat(stale))
        {
            titles[page.Id] = page.Title;
        }

        return titles;
    }

    private static void Render(LabelSyncPlan plan, IReadOnlyDictionary<string, string> titles)
    {
        AnsiConsole.WriteLine();

        foreach (var approval in plan.Approvals)
        {
            var version = approval.Version is { } number ? $"v{number}" : "an unknown version";
            var moved = approval.PreviousVersion is { } previous
                ? $" [grey](was approved at v{previous}; the page changed under the label)[/]"
                : string.Empty;

            AnsiConsole.MarkupLine(
                $"  [green]+approved[/] {approval.Path.EscapeMarkup()} at {version} "
                + $"by [grey]{approval.ApprovedBy.EscapeMarkup()}[/]{moved}");
        }

        foreach (var revocation in plan.Revocations)
        {
            var version = revocation.ApprovedVersion is { } number ? $"v{number}" : "an unknown version";

            AnsiConsole.MarkupLine(
                $"  [yellow]-approved[/] {revocation.Path.EscapeMarkup()} "
                + $"[grey](was approved at {version}; the label is gone, so someone revoked it)[/]");
        }

        foreach (var change in plan.StaleChanges)
        {
            var word = change.Stale ? "[yellow]+stale[/]" : "[green]-stale[/]";
            AnsiConsole.MarkupLine($"  {word} {change.Path.EscapeMarkup()}");
        }

        RenderUnmanaged(plan, titles);
    }

    /// <summary>
    /// Labelled pages state does not know. Reported rather than matched to a path by title: a human
    /// labelling their own page in a shared space is ordinary, and guessing which markdown file it
    /// belongs to is how the wrong page gets approved.
    /// </summary>
    private static void RenderUnmanaged(LabelSyncPlan plan, IReadOnlyDictionary<string, string> titles)
    {
        if (plan.Unmanaged.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[yellow]{plan.Unmanaged.Count} labelled page(s) are not in state[/] — skipped, not guessed at:");

        foreach (var page in plan.Unmanaged)
        {
            var labels = page.Approved ? "approved" : "stale";
            if (page.Approved && page.Stale)
            {
                labels = "approved + stale";
            }

            var title = titles.TryGetValue(page.PageId, out var known) ? known : "unknown title";

            AnsiConsole.MarkupLine(
                $"  [grey]•[/] {page.PageId.EscapeMarkup()} — {title.EscapeMarkup()} [grey]({labels})[/]");
        }
    }

    private static int Fail(string message)
    {
        AnsiConsole.MarkupLine($"[red]{message.EscapeMarkup()}[/]");

        return 1;
    }
}
