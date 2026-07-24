using DocuMe.Core.State;
using Shouldly;

namespace DocuMe.Core.Tests.State;

public sealed class StateStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docume-state-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_dir, name);

    [Fact]
    public void SaveLoad_PopulatedState_RoundTrips()
    {
        var path = Path("state.json");
        var original = new DocumeState
        {
            BaselineSha = "98c6df844",
            LastPublishedSha = "abc123",
            Pages = new Dictionary<string, PageState>
            {
                ["10-domains/loans/README.md"] = new PageState
                {
                    PageId = "123456",
                    Title = "Loans Domain",
                    ParentPageId = "999",
                    ContentHash = "sha256:deadbeef",
                    PublishedVersion = 6,
                    Attachments = new Dictionary<string, string> { ["diagram-1.svg"] = "sha256:cafe" },
                    Approval = new ApprovalState
                    {
                        Status = "approved",
                        ApprovedBy = "jonas",
                        ApprovedVersion = 6,
                        History = [new ApprovalHistoryEntry { By = "jonas", Version = 4 }],
                    },
                    Stale = false,
                    FeedbackCursor = "2026-08-01T10:00:00Z",
                },
            },
        };

        StateStore.Save(path, original);
        var loaded = StateStore.Load(path);

        loaded.Version.ShouldBe(StateStore.CurrentVersion);
        loaded.BaselineSha.ShouldBe("98c6df844");
        loaded.LastPublishedSha.ShouldBe("abc123");
        loaded.Pages.ShouldContainKey("10-domains/loans/README.md");

        var page = loaded.Pages["10-domains/loans/README.md"];
        page.PageId.ShouldBe("123456");
        page.Title.ShouldBe("Loans Domain");
        page.ContentHash.ShouldBe("sha256:deadbeef");
        page.PublishedVersion.ShouldBe(6);
        page.Attachments["diagram-1.svg"].ShouldBe("sha256:cafe");
        page.Approval.ShouldNotBeNull();
        page.Approval!.Status.ShouldBe("approved");
        page.Approval.ApprovedBy.ShouldBe("jonas");
        page.Approval.History.ShouldHaveSingleItem().Version.ShouldBe(4);
        page.FeedbackCursor.ShouldBe("2026-08-01T10:00:00Z");
    }

    [Fact]
    public void SaveLoad_EmptyState_RoundTrips()
    {
        var path = Path("empty.json");

        StateStore.Save(path, new DocumeState());
        var loaded = StateStore.Load(path);

        loaded.Version.ShouldBe(StateStore.CurrentVersion);
        loaded.Pages.ShouldBeEmpty();
        loaded.BaselineSha.ShouldBeNull();
    }

    [Fact]
    public void Save_CreatesMissingParentDirectories()
    {
        var path = Path(System.IO.Path.Combine("docs", "wiki", "_meta", "state.json"));

        StateStore.Save(path, new DocumeState());

        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void Load_NewerVersion_ThrowsStateVersion()
    {
        var path = Path("future.json");
        File.WriteAllText(path, $$"""{ "version": {{StateStore.CurrentVersion + 1}}, "pages": {} }""");

        var ex = Should.Throw<StateVersionException>(() => StateStore.Load(path));

        ex.FileVersion.ShouldBe(StateStore.CurrentVersion + 1);
        ex.SupportedVersion.ShouldBe(StateStore.CurrentVersion);
    }
}
