using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocuMe.Core.Markdown;
using DocuMe.Core.State;
using DocuMe.Core.Tests.Markdown;
using Shouldly;
using WireMock;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocuMe.Core.Tests.Cli;

/// <summary>
/// The <c>```mermaid</c> half of <c>docume publish</c> (PLAN.md §6.2 step 3, §7) run as a process: the
/// last CLI path that had never been executed as a command. It is the only path in the tool that starts
/// a second process of its own and the only one that uploads bytes rather than JSON, and neither of
/// those is reachable in-process.
/// </summary>
/// <remarks>
/// <para>
/// The questions here are the command's, not the renderer's — <see cref="MermaidRendererTests"/> owns
/// the script's contract and <see cref="Confluence.ConfluenceClientTests"/> owns the multipart wire
/// shape. What only a run can answer: whether <c>mermaid.renderer</c> resolves against
/// <c>docume.json</c> or against the shell's working directory, whether <c>--dry-run</c> starts Node at
/// all, whether an unchanged diagram is re-rendered and re-uploaded, and what a run exits with when Node
/// is absent or the script refuses the diagram.
/// </para>
/// <para>
/// Most tests use a stub renderer that records its own invocation in a marker file. That is not a
/// shortcut around <c>beautiful-mermaid</c>: a marker answers "did the CLI start Node" directly, where
/// an SVG only implies it. One test runs the real <c>templates/tools/render-mermaid.mjs</c> end to end
/// and skips when <c>npm ci</c> has not been run, which is the same bargain the rest of the suite makes
/// (<see cref="BundledRenderScript"/>).
/// </para>
/// </remarks>
public sealed partial class CliMermaidTests : IDisposable
{
    private const string SpaceKey = "SBX";
    private const string SpaceId = "98304";

    /// <summary>The page this suite adds, and the fence body inside it.</summary>
    private const string DiagramPath = "architecture/data-flow.md";

    private const string DiagramTitle = "Data Flow";

    /// <summary>
    /// The fence body exactly as the converter hands it to the renderer: no trailing newline, because
    /// that is the string <see cref="MermaidAttachmentName.ForSource"/> is a function of.
    /// </summary>
    private const string DiagramSource = "graph TD\n  A[Loan request] --> B{Approved?}";

    /// <summary>
    /// The width the stub SVG carries. Fractional on purpose: <c>ac:width</c> is a whole pixel count
    /// rounded up, so 240.4 → 241 distinguishes a measured width from a copied string.
    /// </summary>
    private const string StubSvgWidth = "240.4";

    private const string StubSvgWidthPixels = "241";

    private readonly WireMockServer _server = WireMockServer.Start();

    private readonly string _root = Directory.CreateTempSubdirectory("docume-cli-mermaid").FullName;

    /// <summary>Page title → the id the fake Confluence invented for it.</summary>
    private readonly Dictionary<string, string> _created = new(StringComparer.Ordinal);

    private int _nextPageId = 810000;

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// §6.2 step 3 end to end with the shipped script: the fence is rendered, the SVG is uploaded as an
    /// attachment, and the page body points at the file that was uploaded. The three names have to be
    /// one name — a body referencing an attachment nobody uploaded is a published page with a broken
    /// image, which is the failure §7 refuses to degrade into.
    /// </summary>
    [Fact]
    public void Publish_uploads_the_rendered_diagram_and_points_the_body_at_it()
    {
        var script = BundledRenderScript.TryFind();
        Assert.SkipUnless(script is not null, BundledRenderScript.DependencyMissingReason);

        var work = Scaffolded(nameof(Publish_uploads_the_rendered_diagram_and_points_the_body_at_it));

        // Absolute, at its place in this repo: Node resolves beautiful-mermaid from the script's own
        // directory upwards, so a copy in a temp directory would find nothing to import.
        SetRenderer(work, script!);
        WriteDiagramPage(work);

        StubSpace();
        StubCreate();
        StubAttachmentUpload();

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);
        run.Flowed.ShouldContain("Attachments uploaded: 1", customMessage: run.Diagnostics);

        var upload = Uploads().ShouldHaveSingleItem();

        // To the diagram's own page, not to whichever page the run happened to create first: an
        // attachment on the wrong page is a published body pointing at a file that is not there.
        upload.Path.ShouldBe(
            $"/wiki/rest/api/content/{PageId(DiagramTitle)}/child/attachment",
            run.Diagnostics);

        var multipart = Multipart(upload);

        // The v1 part names, from the request the CLI actually built rather than the one Core builds in
        // isolation: .NET writes `name=file` as a bare token and repeats the filename in filename*=.
        multipart.ShouldContain("name=file", customMessage: run.Diagnostics);
        multipart.ShouldContain("Content-Type: image/svg+xml", customMessage: run.Diagnostics);
        multipart.ShouldContain("<svg", customMessage: run.Diagnostics);
        multipart.ShouldContain("</svg>", customMessage: run.Diagnostics);

        var uploaded = UploadedFilename(multipart);

        // Oracle-free: whatever the run chose to call the file, the body it published has to reference
        // that exact name and state has to remember it.
        var body = CreatedBody(DiagramTitle);
        var because = $"The page body does not reference the attachment the run uploaded ('{uploaded}')."
            + Environment.NewLine + run.Diagnostics;

        body.ShouldContain($"<ri:attachment ri:filename=\"{uploaded}\"/>", customMessage: because);

        State(work).Pages[DiagramPath].Attachments.Keys.ShouldBe([uploaded], run.Diagnostics);

        // And the name is the documented function of the fence body (§8): stable across publishes, which
        // is what stops every run re-uploading every diagram and churning approvals.
        uploaded.ShouldBe(MermaidAttachmentName.ForSource(DiagramSource), run.Diagnostics);
    }

    /// <summary>
    /// §7's <c>ac:width</c> is measured from the rendered SVG, so it can only appear on the body a real
    /// run uploads — the converter never renders and <c>--dry-run</c> never measures.
    /// </summary>
    [Fact]
    public void The_published_body_carries_the_width_measured_from_the_rendered_svg()
    {
        var work = Scaffolded(nameof(The_published_body_carries_the_width_measured_from_the_rendered_svg));

        StubRenderer(work, Renders());
        WriteDiagramPage(work);

        StubSpace();
        StubCreate();
        StubAttachmentUpload();

        var run = Invoke(work, "publish");

        run.Code.ShouldBe(0, run.Diagnostics);

        var body = CreatedBody(DiagramTitle);
        var because = $"The published body carries no ac:width from the SVG's width=\"{StubSvgWidth}\"."
            + Environment.NewLine + run.Diagnostics;

        body.ShouldContain($"<ac:image ac:width=\"{StubSvgWidthPixels}\">", customMessage: because);

        // Recorded as well as published: an ordinary edit later on re-publishes the remembered width
        // rather than re-rendering an unchanged diagram (DiagramImageWidth).
        var widths = State(work).Pages[DiagramPath].DiagramWidths;
        widths.Values.ShouldBe([StubSvgWidthPixels], run.Diagnostics);
    }

    /// <summary>
    /// <c>--dry-run</c> promises to write nothing, and PLAN.md §6.2 puts rendering in the write half —
    /// so a dry run must not start Node either. It is the check every scaffolded workflow runs on a pull
    /// request, on a runner that may have no Node at all.
    /// </summary>
    [Fact]
    public void A_dry_run_never_starts_node()
    {
        var work = Scaffolded(nameof(A_dry_run_never_starts_node));

        var marker = StubRenderer(work, Renders());
        WriteDiagramPage(work);

        StubSpace();
        StubCreate();
        StubAttachmentUpload();

        var dry = Invoke(work, "publish", "--dry-run");

        dry.Code.ShouldBe(0, dry.Diagnostics);

        var because = "`publish --dry-run` started the mermaid renderer." + Environment.NewLine
            + dry.Diagnostics;

        File.Exists(marker).ShouldBeFalse(because);

        // The same fixture, one flag apart, does start it — without this the assertion above would pass
        // just as well against a renderer nothing could ever invoke.
        var real = Invoke(work, "publish");

        real.Code.ShouldBe(0, real.Diagnostics);
        File.Exists(marker).ShouldBeTrue(real.Diagnostics);

        // And the fence body reached the script verbatim, which is what makes the attachment name a
        // function of the source rather than of the file it came from.
        File.ReadAllText(marker).ShouldBe(DiagramSource, real.Diagnostics);
    }

    /// <summary>
    /// A diagram whose source has not changed is neither re-rendered nor re-uploaded (§6.2 step 5, §8):
    /// re-uploading would spend a Node process and an upload per diagram on every publish, and the whole
    /// point of hashing attachments is that it does not.
    /// </summary>
    [Fact]
    public void An_unchanged_diagram_is_neither_re_rendered_nor_re_uploaded_unless_forced()
    {
        var work = Scaffolded(nameof(An_unchanged_diagram_is_neither_re_rendered_nor_re_uploaded_unless_forced));

        var marker = StubRenderer(work, Renders());
        WriteDiagramPage(work);

        StubSpace();
        StubCreate();
        StubRepublish();
        StubAttachmentUpload();

        Invoke(work, "publish").Code.ShouldBe(0, "The fixture's own first publish failed.");

        Uploads().Count.ShouldBe(1, "The first publish did not upload the diagram.");
        File.Delete(marker);

        var again = Invoke(work, "publish");

        again.Code.ShouldBe(0, again.Diagnostics);

        var because = "A second publish re-rendered a diagram whose source had not changed."
            + Environment.NewLine + again.Diagnostics;

        File.Exists(marker).ShouldBeFalse(because);
        Uploads().Count.ShouldBe(1, $"A second publish re-uploaded it.{Environment.NewLine}{again.Diagnostics}");

        // --force is the escape hatch §6.2 documents, and running it here is what keeps the two
        // assertions above from passing on a fixture that could never have uploaded twice.
        var forced = Invoke(work, "publish", "--force");

        forced.Code.ShouldBe(0, forced.Diagnostics);
        File.Exists(marker).ShouldBeTrue($"--force did not re-render.{Environment.NewLine}{forced.Diagnostics}");
        Uploads().Count.ShouldBe(2, $"--force did not re-upload.{Environment.NewLine}{forced.Diagnostics}");
    }

    /// <summary>
    /// <c>mermaid.renderer</c> is resolved against the directory holding <c>docume.json</c>, exactly like
    /// <c>wiki.root</c> — not against the shell's working directory. The scaffolded
    /// <c>docs-publish.yml</c> runs from the repository root so the two agree there, but a monorepo job
    /// with a <c>--config</c> pointing elsewhere would render a different script, or none.
    /// </summary>
    [Fact]
    public void The_renderer_path_resolves_against_docume_json_rather_than_the_shell_directory()
    {
        var work = Scaffolded(nameof(The_renderer_path_resolves_against_docume_json_rather_than_the_shell_directory));

        var marker = StubRenderer(work, Renders());
        WriteDiagramPage(work);

        // A second script at the same relative path under the directory the command is run from. If the
        // path were resolved against the shell, this is the one that would run — and it says so out loud
        // instead of leaving "not found" to be interpreted.
        var elsewhere = Path.Combine(work, "docs");
        var decoy = StubRenderer(elsewhere, Refuses(), marker: "decoy-ran.txt");

        StubSpace();
        StubCreate();
        StubAttachmentUpload();

        var run = Invoke(elsewhere, "publish", "--config", Path.Combine(work, "docume.json"));

        run.Code.ShouldBe(0, run.Diagnostics);

        var because = "`--config` did not anchor mermaid.renderer: the script beside the shell's working "
            + $"directory ran instead.{Environment.NewLine}{run.Diagnostics}";

        File.Exists(decoy).ShouldBeFalse(because);
        File.Exists(marker).ShouldBeTrue(run.Diagnostics);
        Uploads().Count.ShouldBe(1, run.Diagnostics);
    }

    /// <summary>
    /// PLAN.md §4 names the message a machine without Node has to get. A runner missing Node fails every
    /// diagram identically, so the run stops rather than reporting one finding per page — and it stops
    /// before writing that page, because rendering precedes the body write
    /// (<see cref="Publishing.PublishExecutor"/>).
    /// </summary>
    [Fact]
    public void A_missing_node_stops_the_run_before_a_single_page_is_written()
    {
        var work = Scaffolded(nameof(A_missing_node_stops_the_run_before_a_single_page_is_written));

        StubRenderer(work, Renders());
        WriteDiagramPage(work);

        StubSpace();
        StubCreate();
        StubAttachmentUpload();

        // An empty directory as the whole PATH: the CLI is launched by absolute path, so this takes away
        // Node (and git, which the publish only ever asks nicely for) without taking away the run.
        var withoutNode = Path.Combine(work, "no-tools");
        Directory.CreateDirectory(withoutNode);

        var run = Invoke(work, new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = withoutNode }, "publish");

        run.Code.ShouldNotBe(0, run.Diagnostics);

        // The three things the reader needs: what is missing, that this stopped the run rather than
        // failing one page, and where it stopped. Asserted on the words §4 promises, not on "Node"
        // appearing somewhere — every publish report mentions Node in one help string or another.
        run.Flowed.ShouldContain("Could not start Node", customMessage: run.Diagnostics);
        run.Flowed.ShouldContain("the mermaid renderer cannot run", customMessage: run.Diagnostics);
        run.Flowed.ShouldContain("STOPPED", customMessage: run.Diagnostics);
        run.Flowed.ShouldContain(DiagramPath, customMessage: run.Diagnostics);

        var titles = CreatedTitles();
        var because = $"The diagram's page was written by a run that could never render it: "
            + $"[{string.Join(", ", titles)}].{Environment.NewLine}{run.Diagnostics}";

        titles.ShouldNotContain(DiagramTitle, because);
        Uploads().ShouldBeEmpty(run.Diagnostics);
        State(work).Pages.ShouldNotContainKey(DiagramPath, run.Diagnostics);
    }

    /// <summary>
    /// A diagram the script refuses fails its page and nothing else (§7: an unrenderable construct fails
    /// its page rather than degrading it). The script's own one-line diagnostic has to reach the
    /// operator — it is the only place the reason exists, and "the diagram did not render" without it
    /// leaves nothing to fix.
    /// </summary>
    [Fact]
    public void A_diagram_the_script_refuses_fails_its_page_and_carries_the_script_s_reason()
    {
        var work = Scaffolded(nameof(A_diagram_the_script_refuses_fails_its_page_and_carries_the_script_s_reason));

        StubRenderer(work, Refuses());
        WriteDiagramPage(work);

        StubSpace();
        StubCreate();
        StubAttachmentUpload();

        var run = Invoke(work, "publish");

        run.Code.ShouldNotBe(0, run.Diagnostics);

        run.Flowed.ShouldContain(DiagramPath, customMessage: run.Diagnostics);
        run.Flowed.ShouldContain(RefusalReason, customMessage: run.Diagnostics);

        // Its page is failed, and only its page: the scaffolded README publishes in the same run.
        run.Flowed.ShouldContain("1 page(s) published", customMessage: run.Diagnostics);
        run.Flowed.ShouldContain("1 failed", customMessage: run.Diagnostics);

        var titles = CreatedTitles();
        var because = $"The refused page was written anyway: [{string.Join(", ", titles)}]."
            + Environment.NewLine + run.Diagnostics;

        titles.ShouldNotContain(DiagramTitle, because);
        Uploads().ShouldBeEmpty(run.Diagnostics);
        State(work).Pages.ShouldNotContainKey(DiagramPath, run.Diagnostics);
    }

    private static DocumeState State(string work) => StateStore.Load(StatePath(work));

    private static string StatePath(string work) =>
        Path.Combine(work, "docs", "wiki", "_meta", "state.json");

    private static JsonElement Payload(IRequestMessage request)
    {
        request.Body.ShouldNotBeNull();
        using var document = JsonDocument.Parse(request.Body!);

        return document.RootElement.Clone();
    }

    /// <summary>The multipart request body verbatim; the SVG part is text, so UTF-8 reads it whole.</summary>
    private static string Multipart(IRequestMessage request)
    {
        request.BodyAsBytes.ShouldNotBeNull();

        return Encoding.UTF8.GetString(request.BodyAsBytes!);
    }

    /// <summary>The attachment name the file part carries, read back out of the request that sent it.</summary>
    private static string UploadedFilename(string multipart)
    {
        var match = FilenamePart().Match(multipart);
        match.Success.ShouldBeTrue($"No `filename=` in the upload:{Environment.NewLine}{multipart}");

        return match.Groups["name"].Value;
    }

    private static CliRun Invoke(string workingDirectory, params string[] args) =>
        DocumeCli.Invoke(workingDirectory, args);

    private static CliRun Invoke(
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        params string[] args) =>
        DocumeCli.Invoke(workingDirectory, environment, args);

    /// <summary>The one page this suite publishes, carrying a single <c>```mermaid</c> fence.</summary>
    private static void WriteDiagramPage(string work)
    {
        var full = Path.Combine(
            work, "docs", "wiki", DiagramPath.Replace('/', Path.DirectorySeparatorChar));

        var markdown = $"""
            ---
            title: {DiagramTitle}
            ---

            # {DiagramTitle}

            How a request moves through the system.

            ```mermaid
            {DiagramSource}
            ```

            """;

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, markdown);
    }

    private static void SetRenderer(string work, string path) =>
        Reconfigure(work, config =>
        {
            config["mermaid"] ??= new JsonObject();
            config["mermaid"]!["renderer"] = JsonValue.Create(path);
        });

    private static void Reconfigure(string work, Action<JsonNode> edit)
    {
        var path = Path.Combine(work, "docume.json");
        var config = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"{path} parsed as null.");

        edit(config);

        File.WriteAllText(path, config.ToJsonString());
    }

    /// <summary>
    /// Writes a stand-in renderer over the scaffolded <c>tools/render-mermaid.mjs</c> and answers the
    /// marker path it touches when it runs.
    /// </summary>
    /// <param name="directory">The directory holding <c>tools/</c>, which is the repo root for a real run.</param>
    /// <param name="ending">What the script does once it has recorded the source (<see cref="Renders"/>).</param>
    /// <param name="marker">
    /// The marker filename, relative to <paramref name="directory"/>. The source is written into it, so a
    /// test can also assert what the CLI fed the script.
    /// </param>
    /// <remarks>
    /// The path is left at the scaffolded default so the config under test is the one a consumer gets.
    /// Reading stdin the shipped script's way is not optional: <c>readFileSync(0)</c> throws EAGAIN on the
    /// non-blocking pipe .NET hands a child, which would put a race in the fixture
    /// (<see cref="BundledRenderScript.ReadSourceFromStdin"/>).
    /// </remarks>
    private static string StubRenderer(string directory, string ending, string marker = "renderer-ran.txt")
    {
        var markerPath = Path.Combine(directory, marker);
        var script = Path.Combine(directory, "tools", "render-mermaid.mjs");

        var source = $$"""
            import { writeFileSync } from 'node:fs';
            {{BundledRenderScript.ReadSourceFromStdin}}
            writeFileSync({{JsonSerializer.Serialize(markerPath)}}, source);
            {{ending}}
            """;

        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, source);

        return markerPath;
    }

    /// <summary>A script that answers with an SVG, the way a rendered diagram arrives.</summary>
    private static string Renders() =>
        $"""process.stdout.write('<svg xmlns="http://www.w3.org/2000/svg" width="{StubSvgWidth}" height="120"><g/></svg>');""";

    /// <summary>A script that refuses the diagram: exit 2, one line on stderr, nothing on stdout.</summary>
    private static string Refuses() =>
        $"""
        process.stderr.write('render-mermaid: {RefusalReason}\n');
        process.exitCode = 2;
        """;

    /// <summary>The stub script's own words, which have to survive the trip to the report.</summary>
    private static string RefusalReason => "the diagram did not render: Parse error on line 2";

    [GeneratedRegex("filename=(?<name>[^\";\\r\\n]+)", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FilenamePart();

    /// <summary>Every request the fake Confluence was sent, in order.</summary>
    private List<IRequestMessage> Seen() =>
        _server.LogEntries
            .Select(entry => entry.RequestMessage)
            .OfType<IRequestMessage>()
            .ToList();

    private List<IRequestMessage> Requests(string method, string path) =>
        Seen()
            .Where(request => string.Equals(request.Method, method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Path, path, StringComparison.Ordinal))
            .ToList();

    /// <summary>Every attachment upload, whichever page id it was addressed to.</summary>
    private List<IRequestMessage> Uploads() =>
        Seen()
            .Where(request => string.Equals(request.Method, "PUT", StringComparison.OrdinalIgnoreCase)
                && request.Path.EndsWith("/child/attachment", StringComparison.Ordinal))
            .ToList();

    /// <summary>The id the fake Confluence handed back for the page it created under <paramref name="title"/>.</summary>
    private string PageId(string title)
    {
        lock (_created)
        {
            _created.ShouldContainKey(title, $"No page was created under the title '{title}'.");

            return _created[title];
        }
    }

    /// <summary>The title of every page a run created, in the order the creates went out.</summary>
    private List<string?> CreatedTitles() =>
        Requests("POST", "/wiki/api/v2/pages")
            .Select(request => Payload(request).GetProperty("title").GetString())
            .ToList();

    /// <summary>The storage body the run published under <paramref name="title"/>.</summary>
    private string CreatedBody(string title)
    {
        var created = Requests("POST", "/wiki/api/v2/pages")
            .Select(Payload)
            .Where(payload => string.Equals(payload.GetProperty("title").GetString(), title, StringComparison.Ordinal))
            .ToList();

        created.Count.ShouldBe(1, $"'{title}' was not created exactly once.");

        return created[0].GetProperty("body").GetProperty("storage").GetProperty("value").GetString()!;
    }

    /// <summary>
    /// A consumer repo as `docume init` leaves it, pointed at this test's own server — same fixture as
    /// <see cref="CliConfluenceTests"/>, and scaffolded by the CLI so it cannot drift from what a real
    /// consumer gets.
    /// </summary>
    private string Scaffolded(string name)
    {
        var work = Path.Combine(_root, name);
        Directory.CreateDirectory(work);

        var run = Invoke(work, "init", "--space", SpaceKey, "--base-url", $"{_server.Url}/wiki");

        run.Code.ShouldBe(0, $"The fixture's own `docume init` failed.{Environment.NewLine}{run.Diagnostics}");

        return work;
    }

    private void StubSpace() =>
        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/spaces").UsingGet())
            .RespondWith(Json($$"""
                {
                  "results": [{ "id": "{{SpaceId}}", "key": "{{SpaceKey}}", "name": "DocuMe Sandbox" }],
                  "_links": {}
                }
                """));

    /// <summary>Answers a create with an id invented at request time, so nothing is asserted against a literal.</summary>
    private void StubCreate() =>
        _server
            .Given(Request.Create().WithPath("/wiki/api/v2/pages").UsingPost())
            .RespondWith(Json(request =>
            {
                var id = Interlocked.Increment(ref _nextPageId).ToString(CultureInfo.InvariantCulture);
                var title = Payload(request).GetProperty("title").GetString()!;

                lock (_created)
                {
                    _created[title] = id;
                }

                return $$"""
                    {
                      "id": "{{id}}",
                      "status": "current",
                      "title": {{JsonSerializer.Serialize(title)}},
                      "spaceId": "{{SpaceId}}",
                      "version": { "number": 1 }
                    }
                    """;
            }));

    /// <summary>
    /// What a republish needs beyond a create: the page as Confluence holds it now (for the version an
    /// update sends), the inline comments a body rewrite could strand (§6.2 step 6), and the update
    /// itself. The comments stub takes priority because it sits under the same <c>pages/{id}/</c> prefix
    /// as the page read.
    /// </summary>
    private void StubRepublish()
    {
        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/wiki/api/v2/pages/*/inline-comments"))
                .UsingGet())
            .AtPriority(-1)
            .RespondWith(Json("""{ "results": [], "_links": {} }"""));

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/api/v2/pages/*")).UsingGet())
            .RespondWith(Json(request => Page(request, version: 1)));

        _server
            .Given(Request.Create().WithPath(new WildcardMatcher("/wiki/api/v2/pages/*")).UsingPut())
            .RespondWith(Json(request => Page(request, version: 2)));
    }

    /// <summary>One page, echoing back the id the request named, at the version asked for.</summary>
    private static string Page(IRequestMessage request, int version) => $$"""
        {
          "id": "{{request.Path.Split('/')[^1]}}",
          "status": "current",
          "title": {{JsonSerializer.Serialize(DiagramTitle)}},
          "spaceId": "{{SpaceId}}",
          "version": { "number": {{version.ToString(CultureInfo.InvariantCulture)}} }
        }
        """;

    /// <summary>
    /// The v1 multipart endpoint. <c>PUT</c> only: <c>POST</c> is create-only and would 400 on a name
    /// that already exists, so leaving it unstubbed means picking the wrong verb fails here too.
    /// </summary>
    private void StubAttachmentUpload() =>
        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher("/wiki/rest/api/content/*/child/attachment"))
                .UsingPut())
            .RespondWith(Json("""
                {
                  "results": [
                    {
                      "id": "att77830",
                      "type": "attachment",
                      "status": "current",
                      "title": "mermaid.svg",
                      "version": { "number": 1 }
                    }
                  ],
                  "start": 0, "limit": 200, "size": 1,
                  "_links": {}
                }
                """));

    private static IResponseBuilder Json(string body) => Response.Create()
        .WithStatusCode(HttpStatusCode.OK)
        .WithHeader("Content-Type", "application/json")
        .WithBody(body);

    private static IResponseBuilder Json(Func<IRequestMessage, string> body) => Response.Create()
        .WithStatusCode(HttpStatusCode.OK)
        .WithHeader("Content-Type", "application/json")
        .WithBody(body);
}
