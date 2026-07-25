using System.Text.Json;
using DocuMe.Core.Feedback;
using Shouldly;

namespace DocuMe.Core.Tests.Feedback;

/// <summary>
/// The inbox on disk (PLAN.md §5.4): where items land, what the file says, and what it refuses to touch.
/// </summary>
public sealed class FeedbackInboxTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docume-inbox-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>§5.4's path, spelled with the platform's separator.</summary>
    [Fact]
    public void Puts_the_inbox_where_plan_md_5_4_says()
    {
        var directory = FeedbackInbox.DirectoryFor(Path.Combine("repo", "docs", "wiki"));

        directory.ShouldBe(Path.Combine("repo", "docs", "wiki", "_meta", "feedback", "inbox"));
    }

    /// <summary>
    /// The file a human reads in the PR diff: camelCase keys, indented, and — because DocuMe's writer
    /// omits nulls — no <c>resolution</c> line until /docs-feedback fills one in, and no
    /// <c>quotedText</c> on a footer item.
    /// </summary>
    [Fact]
    public void Writes_an_item_as_the_json_5_4_documents()
    {
        var plan = Plan(
            Item("conf-comment-987654", FeedbackKind.Inline, quotedText: "Loans are disbursed within 24 hours"),
            Item("conf-comment-987655", FeedbackKind.Footer));

        FeedbackInbox.Write(_dir, plan).Count.ShouldBe(2);

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "page-987654.json")));
        var root = document.RootElement;

        root.GetProperty("id").GetString().ShouldBe("conf-comment-987654");
        root.GetProperty("page").GetString().ShouldBe("10-domains/loans/README.md");
        root.GetProperty("kind").GetString().ShouldBe("inline");
        root.GetProperty("author").GetString().ShouldBe("Jónas");
        root.GetProperty("createdAt").GetString().ShouldBe("2026-08-02T14:11:00.000Z");
        root.GetProperty("quotedText").GetString().ShouldBe("Loans are disbursed within 24 hours");
        root.GetProperty("body").GetString().ShouldBe("<p>A claim to verify.</p>");
        root.GetProperty("status").GetString().ShouldBe("new");
        root.TryGetProperty("resolution", out _).ShouldBeFalse();

        using var footer = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "page-987655.json")));
        footer.RootElement.TryGetProperty("quotedText", out _).ShouldBeFalse();
    }

    /// <summary>
    /// A committed text file ends with a newline: a diff whose every hunk carries "\ No newline at end of
    /// file" is noise in the PR a human reviews.
    /// </summary>
    [Fact]
    public void Ends_each_file_with_a_newline()
    {
        FeedbackInbox.Write(_dir, Plan(Item("conf-comment-1", FeedbackKind.Footer)));

        File.ReadAllText(Path.Combine(_dir, "page-1.json")).ShouldEndWith("\n");
    }

    /// <summary>
    /// A storage-format body and a non-ASCII name are written as themselves. The default JSON encoder
    /// escapes both — <c><p></c>, <c>Jónas</c> — which is valid JSON and unreadable in the
    /// PR diff §5.4 commits these files for.
    /// </summary>
    [Fact]
    public void Writes_the_body_and_the_author_as_a_human_would_read_them()
    {
        FeedbackInbox.Write(_dir, Plan(Item("conf-comment-1", FeedbackKind.Footer)));

        var json = File.ReadAllText(Path.Combine(_dir, "page-1.json"));

        json.ShouldContain("<p>A claim to verify.</p>");
        json.ShouldContain("Jónas");
        json.ShouldNotContain("\\u");
    }

    /// <summary>The first sync on a repo creates the directory rather than failing on it.</summary>
    [Fact]
    public void Creates_the_inbox_directory_on_the_first_item()
    {
        var directory = Path.Combine(_dir, "docs", "wiki", "_meta", "feedback", "inbox");

        FeedbackInbox.Write(directory, Plan(Item("conf-comment-1", FeedbackKind.Footer)));

        File.Exists(Path.Combine(directory, "page-1.json")).ShouldBeTrue();
    }

    /// <summary>An empty plan writes nothing at all — not even the directory.</summary>
    [Fact]
    public void Writes_nothing_for_a_plan_with_no_items()
    {
        var directory = Path.Combine(_dir, "inbox");

        FeedbackInbox.Write(directory, new FeedbackIngestPlan([], [], [])).ShouldBeEmpty();

        Directory.Exists(directory).ShouldBeFalse();
    }

    /// <summary>
    /// What the planner's on-disk guard reads. Only item files count: the archive lives elsewhere (§5.4)
    /// and a stray note in the inbox is not an item.
    /// </summary>
    [Fact]
    public void Lists_the_item_files_already_in_the_inbox()
    {
        File.WriteAllText(Path.Combine(_dir, "page-1.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "README.md"), "not an item");

        var existing = FeedbackInbox.ExistingItemFiles(_dir);

        existing.ShouldBe(new HashSet<string>(["page-1.json"]));
    }

    /// <summary>
    /// An inbox that does not exist yet reads as empty: the first sync on a repo is the ordinary case, and
    /// `docume init` scaffolds no inbox for a wiki with no feedback in it.
    /// </summary>
    [Fact]
    public void Reads_a_missing_inbox_as_empty()
        => FeedbackInbox.ExistingItemFiles(Path.Combine(_dir, "nope")).ShouldBeEmpty();

    /// <summary>
    /// Case-insensitively, because macOS and Windows would treat two casings as one file: an inbox that
    /// behaved differently per platform would overwrite an item on exactly one of them.
    /// </summary>
    [Fact]
    public void Compares_existing_item_names_the_way_the_filesystem_does()
    {
        File.WriteAllText(Path.Combine(_dir, "Page-1.json"), "{}");

        FeedbackInbox.ExistingItemFiles(_dir).ShouldContain("page-1.json");
    }

    /// <summary>
    /// The archive is the inbox's sibling, so the default layout lands exactly on §5.4's path and a run
    /// that relocated the inbox with <c>--output-dir</c> keeps the pair together.
    /// </summary>
    [Fact]
    public void Puts_the_archive_next_to_whichever_inbox_is_in_use()
    {
        var wiki = Path.Combine(Path.GetTempPath(), "repo", "docs", "wiki");
        var archive = FeedbackInbox.ArchiveBeside(FeedbackInbox.DirectoryFor(wiki));

        archive.ShouldBe(Path.Combine(wiki, "_meta", "feedback", "archive"));
        FeedbackInbox.ArchiveBeside(Path.Combine(wiki, "elsewhere", "inbox"))
            .ShouldBe(Path.Combine(wiki, "elsewhere", "archive"));
    }

    /// <summary>
    /// The reply pass reads the inbox and the archive together (§9 step 4 moves an item to the archive in
    /// the same PR that triages it, so by the time a reply is due the item is usually already there).
    /// </summary>
    [Fact]
    public void Reads_items_from_every_directory_it_is_given()
    {
        var archive = Directory.CreateDirectory(Path.Combine(_dir, "archive")).FullName;
        FeedbackInbox.Write(_dir, Plan(Item("conf-comment-1", FeedbackKind.Footer)));
        FeedbackInbox.Write(archive, Plan(Item("conf-comment-2", FeedbackKind.Footer)));

        var items = FeedbackInbox.Read([_dir, archive]);

        items.Select(item => item.Item?.Id).ShouldBe(["conf-comment-1", "conf-comment-2"]);
    }

    /// <summary>
    /// A directory that does not exist reads as empty: a repo with no archive yet is the ordinary case,
    /// and <c>docume init</c> scaffolds neither directory.
    /// </summary>
    [Fact]
    public void Reads_a_missing_directory_as_empty()
        => FeedbackInbox.Read([Path.Combine(_dir, "nope")]).ShouldBeEmpty();

    /// <summary>
    /// One hand-edited item with a typo in it must not stop the other forty from being answered, so an
    /// unparseable file comes back with a null item for the caller to report.
    /// </summary>
    [Fact]
    public void Surfaces_an_unparseable_item_instead_of_throwing()
    {
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");
        FeedbackInbox.Write(_dir, Plan(Item("conf-comment-1", FeedbackKind.Footer)));

        var items = FeedbackInbox.Read([_dir]);

        items.Count.ShouldBe(2);
        items.Single(item => item.FilePath.EndsWith("broken.json", StringComparison.Ordinal))
            .Item.ShouldBeNull();
    }

    /// <summary>
    /// The stamp that stops a second reply (§9 step 5). Everything else on the item survives it — the
    /// triage's own status and resolution above all, which is what the reply was composed from.
    /// </summary>
    [Fact]
    public void Stamps_an_item_as_replied_without_disturbing_its_triage()
    {
        FeedbackInbox.Write(_dir, Plan(Item("conf-comment-1", FeedbackKind.Footer)));
        var path = Path.Combine(_dir, "page-1.json");

        var triaged = FeedbackInbox.Read([_dir])[0].Item! with
        {
            Status = FeedbackStatus.Fixed,
            Resolution = "Corrected on the loans page.",
        };

        FeedbackInbox.MarkReplied(
            new StoredFeedbackItem(path, triaged),
            new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.Zero));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        root.GetProperty("repliedAt").GetString().ShouldBe("2026-08-03T09:00:00.000Z");
        root.GetProperty("status").GetString().ShouldBe("fixed");
        root.GetProperty("resolution").GetString().ShouldBe("Corrected on the loans page.");
        root.GetProperty("body").GetString().ShouldBe("<p>A claim to verify.</p>");
    }

    /// <summary>An item that never parsed has nothing to stamp, and stamping a blank over it would erase it.</summary>
    [Fact]
    public void Refuses_to_stamp_an_item_that_never_parsed()
        => Should.Throw<ArgumentException>(() => FeedbackInbox.MarkReplied(
            new StoredFeedbackItem(Path.Combine(_dir, "broken.json"), Item: null),
            DateTimeOffset.UnixEpoch));

    private static FeedbackIngestPlan Plan(params PlannedFeedbackItem[] items) => new(items, [], []);

    private static PlannedFeedbackItem Item(string id, string kind, string? quotedText = null)
        => new(
            "10-domains/loans/README.md",
            $"page-{id.Replace("conf-comment-", string.Empty, StringComparison.Ordinal)}.json",
            new FeedbackItem
            {
                Id = id,
                Page = "10-domains/loans/README.md",
                Kind = kind,
                Author = "Jónas",
                CreatedAt = "2026-08-02T14:11:00.000Z",
                QuotedText = quotedText,
                Body = "<p>A claim to verify.</p>",
                Status = FeedbackStatus.New,
                Resolution = null,
            });
}
