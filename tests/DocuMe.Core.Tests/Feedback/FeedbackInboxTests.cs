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
