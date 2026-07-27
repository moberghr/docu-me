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

    /// <summary>
    /// The invariant PLAN.md §6.2 step 8 rests on, asserted against the write itself: a save that cannot
    /// finish must leave the live file exactly as it was, because that file is the only record of every
    /// page id, approval and feedback cursor the tool has ever earned.
    /// </summary>
    /// <remarks>
    /// The failure is injected by putting a directory where <c>StateStore.Save</c> writes its sibling
    /// temp file — a write that cannot start, standing in for the disk filling up or the process being
    /// killed. The <c>.tmp</c> suffix is named literally here and in <c>StateStore.TemporarySuffix</c>;
    /// changing it there turns this test red rather than leaving it vacuous, which is the point of
    /// naming it.
    /// </remarks>
    [Fact]
    public void Save_WhenTheWriteCannotFinish_LeavesTheLiveFileByteIdentical()
    {
        var path = Path("state.json");
        StateStore.Save(path, new DocumeState { BaselineSha = "committed-sha" });
        var before = File.ReadAllBytes(path);

        Directory.CreateDirectory(path + ".tmp");

        // UnauthorizedAccessException on both platforms today; the shared base keeps a platform that
        // maps EISDIR to an IOException from reading as "no failure at all".
        Should.Throw<SystemException>(
            () => StateStore.Save(path, new DocumeState { BaselineSha = "run-that-died" }));

        File.ReadAllBytes(path).ShouldBe(before);
        StateStore.Load(path).BaselineSha.ShouldBe("committed-sha");
    }

    /// <summary>A successful save leaves the state file and nothing else — no temp file to commit.</summary>
    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        var path = Path("state.json");

        StateStore.Save(path, new DocumeState());

        Directory.GetFileSystemEntries(_dir).ShouldHaveSingleItem().ShouldBe(path);
    }

    /// <summary>
    /// The temp name is deterministic, so a run that died mid-write leaves one stale file rather than a
    /// growing pile — and the next save has to overwrite it instead of failing on it.
    /// </summary>
    [Fact]
    public void Save_OverwritesATemporaryFileLeftByAKilledRun()
    {
        var path = Path("state.json");
        File.WriteAllText(path + ".tmp", "{ half a state file");

        StateStore.Save(path, new DocumeState { BaselineSha = "fresh" });

        StateStore.Load(path).BaselineSha.ShouldBe("fresh");
        Directory.GetFileSystemEntries(_dir).ShouldHaveSingleItem().ShouldBe(path);
    }

    /// <summary>
    /// The encoding the state file has always had, pinned now that the bytes go through a
    /// <see cref="FileStream"/> rather than <c>File.WriteAllText</c>: UTF-8 with no byte-order mark and
    /// no trailing newline. A BOM or a stray newline would show up as a whitespace-only diff on a
    /// committed, machine-owned file in every consumer repo.
    /// </summary>
    [Fact]
    public void Save_WritesUtf8WithNoByteOrderMarkAndNoTrailingNewline()
    {
        var path = Path("state.json");

        StateStore.Save(path, new DocumeState { BaselineSha = "98c6df844" });

        var bytes = File.ReadAllBytes(path);
        bytes[0].ShouldBe((byte)'{');
        bytes[^1].ShouldBe((byte)'}');
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
