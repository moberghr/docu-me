using System.Reflection;
using DocuMe.Core.State;
using DocuMe.Core.Sync;
using Shouldly;

namespace DocuMe.Core.Tests.State;

/// <summary>
/// <c>docs/wiki/10-concepts/approval-and-drift.md</c>'s account of the approval record and of what
/// invalidates one, against the transitions <see cref="StateUpdates"/>, <see cref="PublishPlanner"/> and
/// <see cref="LabelSyncPlanner"/> actually make.
/// </summary>
/// <remarks>
/// <para>
/// The behaviour was covered; the page describing it was not, and at iter91 four of its claims named a
/// trigger the code does not draw. It said the approval entry carries "the content hash it was approved
/// at" — <see cref="ApprovalState"/> has no such field, and invalidation is decided by a publish
/// computing a fresh hash, never by comparing against one the approval remembered. Its
/// <c>approvedAt</c> row read "when the sync run first saw the label", which is restamped the moment the
/// page reaches a new version with the label still on. Its <c>history</c> row credited invalidation
/// alone, when <see cref="StateUpdates.RecordApproval"/> also archives a still-valid approval it
/// displaces. And "What invalidates an approval" named only the republish path, leaving the revocation
/// half — <see cref="LabelSyncPlanner"/>'s "label absent, state says approved" — undocumented even
/// though it runs the identical transition.
/// </para>
/// <para>
/// The worst of the four was a bullet reading "an attachment re-upload with identical bytes does not"
/// invalidate. Identical bytes are never re-uploaded at all, so it described an event that does not
/// occur while implying that *changed* bytes would invalidate. They do not: attachment hashes are
/// outside the body hash entirely (<see cref="PublishPlan"/>'s <c>UpdateAttachments</c> path), so a
/// hand-placed image can be swapped under a standing approval. That is the one case where an approved
/// page visibly changes without re-review, and the page hid it behind a claim of the opposite shape.
/// </para>
/// <para>
/// Every boundary here is <em>executed</em> and its verdict asserted beside the wording, the shape
/// <see cref="Markdown.ConversionReferencePageTests"/> established: pinning the prose against a
/// hand-listed expectation would keep passing if the planner's answer moved, leaving a stale page on a
/// green build. Here the transition and the sentence describing it fail together.
/// </para>
/// </remarks>
public sealed class ApprovalAndDriftPageTests
{
    private const string PagePath = "docs/wiki/10-concepts/approval-and-drift.md";

    /// <summary>The header cell that opens the approval-record table, and the row scan's anchor.</summary>
    private const string TableHeader = "| Field |";

    private const string WikiPath = "10-concepts/approval-and-drift.md";
    private const string PublishedHash = "sha256:published";
    private const string PageId = "page-1";
    private const string Image = "diagram.png";

    /// <summary>
    /// One trial per invalidation boundary a reader would guess wrong, with the verdict
    /// <see cref="PublishPlanner.PlanPage"/> gives it and what the page has to say for a reader to place
    /// their own edit. Listed rather than derived: which boundaries surprise someone is a judgement about
    /// readers, the same one <see cref="Markdown.ConversionReferencePageTests"/> makes.
    /// </summary>
    private static readonly (string Trial, bool Invalidates, string[] DocMustName)[] Boundaries =
    [

        // The only one that does invalidate, and the page has to keep saying so.
        ("body-edited", true, ["Editing one sentence"]),

        // Re-uploads everything, spends a version, and leaves approval alone: the hash did not move.
        ("force", false, ["--force"]),

        // A body write carries the new parent, but where a page hangs is not what was approved.
        ("reparented", false, ["Reparenting"]),

        // The case the old bullet inverted: changed bytes, unchanged body, approval stands.
        ("attachment-bytes", false, ["re-uploaded", "left standing"]),
    ];

    /// <summary>
    /// The table is the reader's field list for the approval entry, so a field the record grows without
    /// a row — or a row naming a field the record does not have, which is how the content-hash claim
    /// survived — has to redden.
    /// </summary>
    [Fact]
    public void The_page_lists_every_field_the_approval_entry_carries_and_no_others()
    {
        var documented = TableRows().Select(FieldOf).ToList();

        documented.ShouldBe(ApprovalFields(), ignoreOrder: true);
    }

    [Fact]
    public void Each_invalidation_boundary_the_planner_draws_is_named_where_the_page_describes_it()
    {
        var page = Page();

        foreach (var (trial, invalidates, mustName) in Boundaries)
        {
            var plan = PlanFor(trial);

            var moved = $"The planner's verdict on the '{trial}' boundary moved, so every description of "
                + $"what invalidates an approval, {PagePath} included, is stale by definition.";
            plan.InvalidatesApproval.ShouldBe(invalidates, moved);

            foreach (var token in mustName)
            {
                var missing = $"{PagePath} does not name the '{trial}' boundary ('{token}'), so a reader "
                    + "cannot tell whether their own edit drops the approval — and the one that does not "
                    + "drop it is the case worth knowing.";
                page.ShouldContain(token, Case.Sensitive, missing);
            }
        }
    }

    /// <summary>
    /// A reviewer taking the label off runs the same transition a republish does. The page named only
    /// the republish half, so the entire revocation path was undocumented.
    /// </summary>
    [Fact]
    public void A_reviewer_removing_the_label_revokes_the_approval_and_the_page_says_so()
    {
        var state = StateWithApprovedPage();
        var observation = new LabelObservation([], [], new Dictionary<string, int>(StringComparer.Ordinal), "2026-07-26T12:00:00Z");

        var plan = LabelSyncPlanner.Plan(state, observation);
        var applied = LabelSyncPlanner.Apply(state, plan);
        var approval = applied.Pages[WikiPath].Approval;

        plan.Revocations.Count.ShouldBe(1, "A label absent from an approved page is §6.3's revocation.");
        approval!.Status.ShouldBe(ApprovalStatus.NeedsReview);
        approval.History.Count.ShouldBe(1, "The retired approval is archived, never dropped (§8 audit trail).");

        const string unnamed = $"{PagePath} does not say a reviewer removing the label revokes the "
            + "approval, so a page silently moves to needs-review with nothing on the page explaining it.";
        Page().ShouldContain("revocation", Case.Sensitive, unnamed);
    }

    /// <summary>
    /// The other writer of <c>history</c>: an approval observed again at a newer version displaces the
    /// old one without any invalidation, which the page credited to invalidation alone.
    /// </summary>
    [Fact]
    public void Re_recording_an_approval_at_a_newer_version_archives_the_previous_one_and_restamps()
    {
        var state = StateWithApprovedPage();
        var versions = new Dictionary<string, int>(StringComparer.Ordinal) { [PageId] = 7 };
        var observation = new LabelObservation([PageId], [], versions, "2026-07-26T12:00:00Z");

        var applied = LabelSyncPlanner.Apply(state, LabelSyncPlanner.Plan(state, observation));
        var approval = applied.Pages[WikiPath].Approval;

        approval!.Status.ShouldBe(ApprovalStatus.Approved, "Nothing was invalidated — the label never left.");
        approval.ApprovedVersion.ShouldBe(7);
        approval.ApprovedAt.ShouldBe("2026-07-26T12:00:00Z", "approvedAt tracks the version it applies to.");
        approval.History.Count.ShouldBe(1, "The displaced v5 approval survives only in history.");

        var rows = TableRows();
        var history = rows.Find(row => string.Equals(FieldOf(row), "history", StringComparison.Ordinal));
        history.ShouldNotBeNull($"{PagePath} has no `history` row.");

        const string invalidationOnly = $"{PagePath}'s `history` row credits invalidation alone, so the "
            + "entry a re-approval displaces looks like a lost record rather than an archived one.";
        history.ShouldContain("re-recorded", Case.Sensitive, invalidationOnly);
    }

    [Fact]
    public void The_table_scan_found_the_rows_these_checks_read()
    {
        // The field check and the history-row lookup both pass vacuously on an empty scan.
        var rows = TableRows();
        rows.ShouldNotBeEmpty($"The '{TableHeader}' table was not found in {PagePath}.");
        rows.ShouldAllBe(row => row.Contains('`'));

        Page().Length.ShouldBeGreaterThan(2000, $"{PagePath} is far shorter than the page these tests scan.");

        ApprovalFields().Length.ShouldBe(5);
        Boundaries.Length.ShouldBe(4);
    }

    /// <summary>An approved page at v5 with one attachment, the starting point every trial edits from.</summary>
    private static PageState ApprovedPage() => new()
    {
        PageId = PageId,
        Title = "Approval and Drift",
        ContentHash = PublishedHash,
        PublishedVersion = 5,
        Attachments = new Dictionary<string, string>(StringComparer.Ordinal) { [Image] = "sha256:before" },
        Approval = new ApprovalState
        {
            Status = ApprovalStatus.Approved,
            ApprovedBy = LabelSyncPlanner.UnknownApprover,
            ApprovedAt = "2026-07-25T09:00:00Z",
            ApprovedVersion = 5,
        },
    };

    private static DocumeState StateWithApprovedPage() => new()
    {
        Pages = new Dictionary<string, PageState>(StringComparer.Ordinal) { [WikiPath] = ApprovedPage() },
    };

    private static PagePublishPlan PlanFor(string trial)
    {
        var current = ApprovedPage();
        var unchanged = new Dictionary<string, string>(StringComparer.Ordinal) { [Image] = "sha256:before" };
        var swapped = new Dictionary<string, string>(StringComparer.Ordinal) { [Image] = "sha256:after" };

        return trial switch
        {
            "body-edited" => PublishPlanner.PlanPage(WikiPath, current, "sha256:edited", unchanged),
            "force" => PublishPlanner.PlanPage(WikiPath, current, PublishedHash, unchanged, force: true),
            "reparented" => PublishPlanner.PlanPage(WikiPath, current, PublishedHash, unchanged, parentMoved: true),
            "attachment-bytes" => PublishPlanner.PlanPage(WikiPath, current, PublishedHash, swapped),
            _ => throw new ArgumentOutOfRangeException(nameof(trial), trial, "Unknown boundary trial."),
        };
    }

    /// <summary>Every line of the approval-record table, separator and header excluded.</summary>
    private static List<string> TableRows()
    {
        var rows = new List<string>();
        var inTable = false;

        foreach (var line in File.ReadAllLines(Path.Combine(RepoRoot, Native(PagePath))))
        {
            if (line.StartsWith(TableHeader, StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            if (!inTable || line.StartsWith("|---", StringComparison.Ordinal))
            {
                continue;
            }

            if (!line.StartsWith('|'))
            {
                break;
            }

            rows.Add(line);
        }

        return rows;
    }

    /// <summary>The field a row names: the first backticked token in its first cell.</summary>
    private static string FieldOf(string row)
    {
        var open = row.IndexOf('`', StringComparison.Ordinal);
        var close = row.AsSpan(open + 1).IndexOf('`') + open + 1;

        return row[(open + 1)..close];
    }

    /// <summary><see cref="ApprovalState"/>'s properties as the state file spells them.</summary>
    private static string[] ApprovalFields() =>
        typeof(ApprovalState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => char.ToLowerInvariant(property.Name[0]) + property.Name[1..])
            .ToArray();

    private static string Page() => File.ReadAllText(Path.Combine(RepoRoot, Native(PagePath)));

    private static string Native(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static string RepoRoot { get; } = Locate();

    /// <summary>Walks up to the directory holding <c>DocuMe.slnx</c>: both files ship in the tree.</summary>
    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so {PagePath} cannot be found.");
    }
}
