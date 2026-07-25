using System.Net;
using DocuMe.Core.Confluence;
using DocuMe.Core.Status;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace DocuMe.Core.Tests.Status;

/// <summary>
/// §6.6's <c>doctor</c>-lite probes. Every case here is a failure case on purpose: a status command is
/// what a human runs when something is broken, so what matters is that a missing token, an absent Node
/// and an unreachable space each become a row rather than an exception.
/// </summary>
/// <remarks>
/// The space probe runs against a real local HTTP server (rule §4.2) so the 401 path goes through the
/// resilience handler that must NOT retry it (rule §1.2).
/// </remarks>
public sealed class StatusProbesTests
{
    private const string Email = "bot@example.com";
    private const string ApiToken = "very-secret-token";
    private const string SpaceKey = "DOCUMESBX";

    private static readonly ConfluenceCredentials Credentials = new(Email, ApiToken);

    [Fact]
    public void Credentials_are_ok_when_both_variables_are_set()
    {
        var check = StatusProbes.Credentials(_ => "set");

        check.Outcome.ShouldBe(StatusCheckOutcome.Ok);
        check.Name.ShouldBe(StatusProbes.CredentialsCheck);
    }

    [Fact]
    public void A_missing_credential_variable_is_named_and_nothing_else_is()
    {
        var check = StatusProbes.Credentials(variable =>
            string.Equals(variable, ConfluenceCredentials.EmailVariable, StringComparison.Ordinal)
                ? "bot@example.com"
                : null);

        check.Outcome.ShouldBe(StatusCheckOutcome.Warning);
        check.Detail.ShouldContain(ConfluenceCredentials.TokenVariable);

        // The variable that IS set must not have its value echoed: half a credential is still a
        // credential, and §6.6 asks whether the token works, not who owns it (rule §1.1).
        check.Detail.ShouldNotContain("bot@example.com");
    }

    [Fact]
    public void A_set_token_never_reaches_the_answer()
    {
        var check = StatusProbes.Credentials(_ => ApiToken);

        check.Detail.ShouldNotContain(ApiToken);
    }

    [Fact]
    public void A_missing_render_script_is_a_warning_that_names_the_path_it_looked_at()
    {
        var missing = Path.Combine(Path.GetTempPath(), "docume-no-such-renderer.mjs");

        var check = StatusProbes.Renderer(missing);

        check.Outcome.ShouldBe(StatusCheckOutcome.Warning);
        check.Detail.ShouldContain(missing);
        check.Detail.ShouldContain("mermaid.renderer");
    }

    [Fact]
    public void A_render_script_that_is_there_is_ok()
    {
        var directory = Directory.CreateTempSubdirectory("docume-renderer-probe");
        try
        {
            var script = Path.Combine(directory.FullName, "render-mermaid.mjs");
            File.WriteAllText(script, "// not executed by this probe");

            StatusProbes.Renderer(script).Outcome.ShouldBe(StatusCheckOutcome.Ok);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Node_answers_with_its_version()
    {
        var check = await StatusProbes.NodeAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        // §4 requires Node ≥ 20 and the loop environment has it. Asserted loosely on purpose: pinning a
        // version would make this test a statement about the machine rather than about the probe.
        check.Outcome.ShouldBe(StatusCheckOutcome.Ok);
        check.Detail.ShouldContain("v");
    }

    [Fact]
    public async Task An_absent_node_is_a_warning_that_says_what_breaks()
    {
        var check = await StatusProbes.NodeAsync(
            "docume-node-that-is-not-installed", TestContext.Current.CancellationToken);

        check.Outcome.ShouldBe(StatusCheckOutcome.Warning);
        check.Detail.ShouldContain("mermaid");
    }

    [Fact]
    public async Task A_reachable_space_proves_the_token_and_the_space_in_one_request()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(SpacesPath).UsingGet())
            .RespondWith(Json(HttpStatusCode.OK, SpacesBody));

        using var client = CreateClient(server);
        var check = await StatusProbes.SpaceAsync(client, SpaceKey, TestContext.Current.CancellationToken);

        check.Outcome.ShouldBe(StatusCheckOutcome.Ok);
        check.Detail.ShouldContain(SpaceKey);
        check.Detail.ShouldContain("98304");

        // One request, not two: a 200 answers both halves of §6.6's question.
        server.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_space_that_is_not_there_is_a_problem_and_says_both_reasons()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(SpacesPath).UsingGet())
            .RespondWith(Json(HttpStatusCode.OK, EmptyResultsBody));

        using var client = CreateClient(server);
        var check = await StatusProbes.SpaceAsync(client, SpaceKey, TestContext.Current.CancellationToken);

        check.Outcome.ShouldBe(StatusCheckOutcome.Problem);
        check.Detail.ShouldContain("confluence.spaceKey");

        // The endpoint answers "no such space" and "you cannot see it" identically, so the report must
        // not pick one and send a reader down the wrong path.
        check.Detail.ShouldContain("cannot see it");
    }

    [Fact]
    public async Task An_expired_token_is_a_problem_and_is_never_retried()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(SpacesPath).UsingGet())
            .RespondWith(Json(HttpStatusCode.Unauthorized, "{}"));

        using var client = CreateClient(server);
        var check = await StatusProbes.SpaceAsync(client, SpaceKey, TestContext.Current.CancellationToken);

        check.Outcome.ShouldBe(StatusCheckOutcome.Problem);
        check.Detail.ShouldContain(ConfluenceCredentials.TokenVariable);

        // Rule §1.2: an auth failure is not transient, and retrying it across a bulk run is how an
        // account gets locked out. One attempt, no matter what MaxRetryAttempts says.
        server.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_server_error_is_a_warning_rather_than_a_verdict_on_the_token()
    {
        using var server = WireMockServer.Start();
        server
            .Given(Request.Create().WithPath(SpacesPath).UsingGet())
            .RespondWith(Json(HttpStatusCode.InternalServerError, "boom"));

        using var client = CreateClient(server);
        var check = await StatusProbes.SpaceAsync(client, SpaceKey, TestContext.Current.CancellationToken);

        check.Outcome.ShouldBe(StatusCheckOutcome.Warning);
        check.Detail.ShouldContain(SpaceKey);
    }

    [Fact]
    public async Task An_unreachable_host_is_a_warning_that_blames_the_transport()
    {
        var options = new ConfluenceClientOptions
        {
            // A port nothing is listening on: the transport fails before any status code exists.
            BaseUrl = new Uri("http://127.0.0.1:1/wiki"),

            // 0: nothing is listening, so a retry can only spend the clock. Polly's own
            // HttpRetryStrategyOptions rejects 0 at construction, which is why ConfluenceHttp leaves
            // the strategy out of the pipeline entirely at that value rather than configuring it.
            MaxRetryAttempts = 0,
            RetryDelay = TimeSpan.FromMilliseconds(1),
            Timeout = TimeSpan.FromSeconds(5),
        };

        using var client = ConfluenceClient.Create(options, Credentials);
        var check = await StatusProbes.SpaceAsync(client, SpaceKey, TestContext.Current.CancellationToken);

        check.Outcome.ShouldBe(StatusCheckOutcome.Warning);
        check.Detail.ShouldContain("transport failure");
    }

    [Fact]
    public void A_check_that_did_not_run_carries_its_reason()
    {
        var check = StatusProbes.NotChecked(StatusProbes.ConfluenceCheck, "--offline");

        check.Outcome.ShouldBe(StatusCheckOutcome.NotChecked);
        check.Detail.ShouldBe("--offline");
    }

    private static ConfluenceClient CreateClient(WireMockServer server)
    {
        var options = new ConfluenceClientOptions
        {
            BaseUrl = new Uri($"{server.Url}/wiki"),
            MaxRetryAttempts = 2,
            RetryDelay = TimeSpan.FromMilliseconds(1),
            Timeout = TimeSpan.FromSeconds(30),
        };

        return ConfluenceClient.Create(options, Credentials);
    }

    private static IResponseBuilder Json(HttpStatusCode statusCode, string body)
        => Response.Create()
            .WithStatusCode(statusCode)
            .WithHeader("Content-Type", "application/json")
            .WithBody(body);

    private static string SpacesPath => "/wiki/api/v2/spaces";

    private static string SpacesBody =>
        """
        {
          "results": [
            { "id": "98304", "key": "DOCUMESBX", "name": "DocuMe Sandbox", "type": "global", "status": "current" }
          ],
          "_links": { "base": "https://example.atlassian.net/wiki" }
        }
        """;

    private static string EmptyResultsBody =>
        """
        { "results": [], "_links": {} }
        """;
}
