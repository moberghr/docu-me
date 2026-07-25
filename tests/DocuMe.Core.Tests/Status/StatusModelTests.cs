using DocuMe.Core.Config;
using DocuMe.Core.Markdown;
using DocuMe.Core.Publishing;
using DocuMe.Core.State;
using DocuMe.Core.Status;
using Shouldly;

namespace DocuMe.Core.Tests.Status;

/// <summary>
/// <c>docume status</c>'s local half (PLAN.md §6.6): the per-page table, the coverage counts, the
/// derived <c>doctor</c>-lite checks and the <c>--json</c> contract — all with no network and no write.
/// </summary>
/// <remarks>
/// The "published" state is built the way a real run builds it
/// (<see cref="StateUpdates.RecordPublish"/> over the plan) rather than hand-written, so a page that
/// reads as in sync here is one an actual publish would skip. A hand-written hash would pass while the
/// real pair drifted apart.
/// </remarks>
public sealed class StatusModelTests : IDisposable
{
    private const string SpaceKey = "SBX";

    private readonly string _dir = Directory.CreateTempSubdirectory("docume-status-tests").FullName;

    public StatusModelTests()
    {
        Write("README.md", "# Home\n\nSee the [Setup Guide](guides/setup.md) and the ![logo](images/logo.png).");
        Write("guides/setup.md", "---\ntitle: Setup Guide\n---\n\n# Setup\n\nPlain prose, no attachments.");
        WriteBytes("images/logo.png", [1, 2, 3, 4]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_repo_that_has_never_published_reads_as_unpublished()
    {
        var report = Build(new DocumeState(), stateExists: false);

        report.Pages.Select(page => page.Path).ShouldBe(["README.md", "guides/setup.md"]);
        report.Pages.ShouldAllBe(page => page.Sync == StatusSync.Unpublished);
        report.UnpublishedCount.ShouldBe(2);
        report.PublishedCount.ShouldBe(0);
        report.InSyncCount.ShouldBe(0);
        report.HasDrift.ShouldBeTrue();

        // Nothing has been published, so there is no version and no link to show — and neither is 0.
        report.Pages.ShouldAllBe(page => page.PublishedVersion == null);
        report.Pages.ShouldAllBe(page => page.Url == null);

        // A missing state file is not a defect: it is what a pre-first-publish repo looks like, and the
        // report says so rather than leaving a reader to infer it from a table of unpublished rows.
        var state = Check(report, StatusModel.StateCheck);
        state.Outcome.ShouldBe(StatusCheckOutcome.Ok);
        state.Detail.ShouldContain("nothing has been published");
    }

    [Fact]
    public void A_published_wiki_with_nothing_changed_is_in_sync()
    {
        var report = Build(Published());

        report.Pages.ShouldAllBe(page => page.Sync == StatusSync.InSync);
        report.InSyncCount.ShouldBe(2);
        report.PublishedCount.ShouldBe(2);
        report.HasDrift.ShouldBeFalse();
        report.Pages.ShouldAllBe(page => page.PublishedVersion == 1);

        // §6.5's "link" column: the page id is what resolves a page, so the URL needs no title slug.
        Page(report, "README.md").Url
            .ShouldBe($"https://example.atlassian.net/wiki/spaces/{SpaceKey}/pages/page-README.md");

        Check(report, StatusModel.StateCheck).Outcome.ShouldBe(StatusCheckOutcome.Ok);
        report.WorstCheck.ShouldBe(StatusCheckOutcome.Ok);
    }

    [Fact]
    public void An_edited_page_reads_as_drifted_and_leaves_the_rest_alone()
    {
        var state = Published();
        Write("guides/setup.md", "---\ntitle: Setup Guide\n---\n\n# Setup\n\nRewritten.");

        var report = Build(state);

        Page(report, "guides/setup.md").Sync.ShouldBe(StatusSync.Drifted);
        Page(report, "README.md").Sync.ShouldBe(StatusSync.InSync);
        report.DriftedCount.ShouldBe(1);
        report.InSyncCount.ShouldBe(1);
        report.HasDrift.ShouldBeTrue();
    }

    [Fact]
    public void A_changed_image_is_reported_apart_from_a_changed_body()
    {
        var state = Published();
        WriteBytes("images/logo.png", [9, 9, 9, 9, 9]);

        var report = Build(state);

        // The body is byte-identical; only the attachment moved, which is not a page rewrite (§6.2 step 5).
        Page(report, "README.md").Sync.ShouldBe(StatusSync.AttachmentsChanged);
        report.AttachmentsChangedCount.ShouldBe(1);
        report.DriftedCount.ShouldBe(0);
        report.HasDrift.ShouldBeTrue();
    }

    [Fact]
    public void A_state_entry_whose_file_is_gone_is_an_orphan_and_a_warning()
    {
        var state = Published();
        File.Delete(Path.Combine(_dir, "guides", "setup.md"));

        var report = Build(state);

        report.Orphans.ShouldBe(["guides/setup.md"]);
        report.HasDrift.ShouldBeTrue();

        var check = Check(report, StatusModel.StateCheck);
        check.Outcome.ShouldBe(StatusCheckOutcome.Warning);
        check.Detail.ShouldContain("--prune");
    }

    [Fact]
    public void Orphans_alone_are_drift_even_when_every_page_is_in_sync()
    {
        // A state entry for a page that never existed in this tree: every present page is in sync, so
        // without the orphan the report would read as calm while Confluence still holds a dead page.
        var state = StateUpdates.RecordPublish(
            Published(),
            "removed.md",
            new PublishedPage("page-removed", "Removed", null, "sha256:whatever", 3, new Dictionary<string, string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal)));

        var report = Build(state);

        report.Pages.ShouldAllBe(page => page.Sync == StatusSync.InSync);
        report.Orphans.ShouldBe(["removed.md"]);
        report.HasDrift.ShouldBeTrue();
    }

    [Fact]
    public void Approval_counts_come_from_state()
    {
        var state = Approve(Published(), "README.md", "mirko", "2026-07-20T09:00:00Z", version: 1);

        var report = Build(state);

        report.ApprovedCount.ShouldBe(1);
        report.NeedsReviewCount.ShouldBe(0);
        report.UnrecordedApprovalCount.ShouldBe(1);
        report.ApprovedPercent.ShouldBe(50);

        var page = Page(report, "README.md");
        page.Approval.ShouldBe(ApprovalStatus.Approved);
        page.ApprovedBy.ShouldBe("mirko");
        page.ApprovedAt.ShouldBe("2026-07-20T09:00:00Z");
        page.ApprovedVersion.ShouldBe(1);

        // One page carries a record, so the "nothing has synced labels yet" note has stopped applying.
        report.NotYetAvailable.ShouldNotContain(gap => gap.Contains("sync --labels", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_approval_column_says_why_rather_than_reading_as_a_verdict()
    {
        var report = Build(Published());

        report.ApprovedCount.ShouldBe(0);
        report.UnrecordedApprovalCount.ShouldBe(2);

        // The honesty the whole command rests on: nothing has read the `approved` label yet, so a
        // 0%-approved wiki must not be reported as if a reviewer had refused every page.
        report.NotYetAvailable.ShouldContain(gap => gap.Contains("sync --labels", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unpublished_repo_does_not_bother_explaining_its_empty_approval_column()
    {
        var report = Build(new DocumeState(), stateExists: false);

        report.NotYetAvailable.ShouldNotContain(gap => gap.Contains("sync --labels", StringComparison.Ordinal));
    }

    [Fact]
    public void Open_feedback_is_left_out_rather_than_reported_as_zero()
    {
        var report = Build(Published());

        report.NotYetAvailable.ShouldContain(gap => gap.Contains("open feedback", StringComparison.Ordinal));
    }

    [Fact]
    public void A_protected_space_is_a_warning_a_reader_can_act_on()
    {
        var report = StatusModel.Build(Paths(stateExists: true), Config(SpaceKey), Tree(), Published());

        var check = Check(report, StatusModel.SpaceLockCheck);
        check.Outcome.ShouldBe(StatusCheckOutcome.Warning);
        check.Detail.ShouldContain("protectedSpaces");
        report.WorstCheck.ShouldBe(StatusCheckOutcome.Warning);
    }

    [Fact]
    public void A_page_the_converter_refuses_is_a_problem_and_not_drift()
    {
        Write("legacy.md", "# Legacy\n\n```plantuml\n@startuml\nA -> B\n@enduml\n```");

        var report = Build(Published());

        report.Failures.Select(failure => failure.Path).ShouldBe(["legacy.md"]);
        report.Pages.Count.ShouldBe(2);

        var check = Check(report, StatusModel.ConverterCheck);
        check.Outcome.ShouldBe(StatusCheckOutcome.Problem);
        report.WorstCheck.ShouldBe(StatusCheckOutcome.Problem);

        // A page that has never reached Confluence cannot be out of step with it: the refusal is a
        // problem in the check table, and --fail-on-drift is not the flag that catches it.
        report.HasDrift.ShouldBeFalse();
    }

    [Fact]
    public void Environment_checks_are_appended_after_the_derived_ones()
    {
        var probe = new StatusCheck("node", StatusCheckOutcome.Ok, "node v22.0.0");

        var report = StatusModel.Build(
            Paths(stateExists: true), Config(), Tree(), Published(), [probe]);

        report.Checks.Select(check => check.Name).ShouldBe(
            [
                StatusModel.TreeCheck,
                StatusModel.ConverterCheck,
                StatusModel.StateCheck,
                StatusModel.SpaceLockCheck,
                "node",
            ]);
    }

    [Fact]
    public void The_json_spells_every_state_in_words()
    {
        var report = Build(Published());
        var json = report.ToJson();

        // A consumed contract (§10): a skill pastes this into a PR body, so the states are words a human
        // reviewer reads, never the enum's ordinal.
        json.ShouldContain("\"sync\": \"in-sync\"");
        json.ShouldContain("\"outcome\": \"ok\"");
        json.ShouldContain("\"pageCount\": 2");
        json.ShouldContain("\"hasDrift\": false");
        json.ShouldNotContain("\"sync\": 1");
    }

    [Fact]
    public void The_json_never_carries_a_credential()
    {
        var report = StatusModel.Build(
            Paths(stateExists: true),
            Config(),
            Tree(),
            Published(),
            [StatusProbes.Credentials(_ => "super-secret-token")]);

        // Rule §1.1 / CLAUDE.md §0.3 at the one place a report could leak: the probe reads the variables
        // to see whether they are set, and their values must not survive into the answer.
        report.ToJson().ShouldNotContain("super-secret-token");
    }

    [Fact]
    public void A_drifted_json_report_names_the_page_that_drifted()
    {
        var state = Published();
        Write("guides/setup.md", "---\ntitle: Setup Guide\n---\n\n# Setup\n\nRewritten.");

        var json = Build(state).ToJson();

        json.ShouldContain("\"sync\": \"drifted\"");
        json.ShouldContain("\"hasDrift\": true");
    }

    private static StatusCheck Check(StatusReport report, string name) =>
        report.Checks.Single(check => string.Equals(check.Name, name, StringComparison.Ordinal));

    private static StatusPage Page(StatusReport report, string path) =>
        report.Pages.Single(page => string.Equals(page.Path, path, StringComparison.Ordinal));

    private static DocumeConfig Config(params string[] protectedSpaces) => new()
    {
        Confluence = new ConfluenceConfig
        {
            BaseUrl = "https://example.atlassian.net/wiki",
            SpaceKey = SpaceKey,
            ProtectedSpaces = protectedSpaces,
        },
    };

    /// <summary>
    /// Approves through the transition <c>sync --labels</c> uses (PLAN.md §6.3,
    /// <see cref="StateUpdates.RecordApproval"/>) rather than hand-shaping an
    /// <see cref="ApprovalState"/>. The report's honesty mechanism — approval columns and the "nothing
    /// has synced labels yet" note appearing only when a page carries a record — is only worth anything
    /// if it responds to what the real command writes.
    /// </summary>
    private static DocumeState Approve(
        DocumeState state,
        string path,
        string by,
        string at,
        int version)
        => StateUpdates.RecordApproval(state, path, by, at, version);

    private StatusReport Build(DocumeState state, bool stateExists = true) =>
        StatusModel.Build(Paths(stateExists), Config(), Tree(), state);

    private WikiTree Tree() => WikiTree.Load(_dir);

    private StatusPaths Paths(bool stateExists) => new()
    {
        ConfigPath = Path.Combine(_dir, "docume.json"),
        WikiRoot = _dir,
        StatePath = Path.Combine(_dir, "_meta", "state.json"),
        StateFileExists = stateExists,
    };

    /// <summary>
    /// The state a successful publish of the current tree would have written, built through
    /// <see cref="StateUpdates.RecordPublish"/> so the hashes are the real pair.
    /// </summary>
    private DocumeState Published()
    {
        var report = PublishPipeline.Plan(Config(), Tree(), new DocumeState());
        var state = new DocumeState { BaselineSha = "abc1234" };

        foreach (var page in report.Pages)
        {
            var attachments = page.Attachments.ToDictionary(
                attachment => attachment.Name,
                attachment => attachment.ContentHash!,
                StringComparer.Ordinal);

            var parentId = page.ParentPath is { } parentPath ? $"page-{parentPath}" : null;

            state = StateUpdates.RecordPublish(
                state,
                page.Path,
                new PublishedPage(
                    $"page-{page.Path}",
                    page.Title,
                    parentId,
                    page.Plan.ContentHash,
                    1,
                    attachments,
                    new Dictionary<string, string>(StringComparer.Ordinal)));
        }

        return StateUpdates.RecordLastPublishedSha(state, "abc1234");
    }

    private void Write(string relativePath, string content) =>
        File.WriteAllText(Materialize(relativePath), content + "\n");

    private void WriteBytes(string relativePath, byte[] content) =>
        File.WriteAllBytes(Materialize(relativePath), content);

    private string Materialize(string relativePath)
    {
        var full = Path.Combine(_dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        return full;
    }
}
