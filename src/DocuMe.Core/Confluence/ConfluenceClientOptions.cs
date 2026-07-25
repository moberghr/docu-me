namespace DocuMe.Core.Confluence;

/// <summary>
/// Non-secret settings for <see cref="ConfluenceClient"/>. The base URL comes from a consumer
/// repo's <c>docume.json</c> → <c>confluence.baseUrl</c> (PLAN.md §5.1); the retry knobs exist so
/// a test can collapse the backoff to milliseconds and still exercise the real handler pipeline.
/// </summary>
public sealed record ConfluenceClientOptions
{
    /// <summary>
    /// Wiki base URL, e.g. <c>https://kvika.atlassian.net/wiki</c>. A missing trailing slash is
    /// added by the client: without one, <see cref="Uri"/> composition would drop the
    /// <c>/wiki</c> segment and every request would 404 at the site root.
    /// </summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>
    /// Retries after the first attempt, for transient failures only (429, 408, 5xx, network).
    /// Never applied to 401/403 — see <see cref="ConfluenceAuthenticationException"/>. Zero is a
    /// valid floor and means one attempt with no second one, which is what a probe wants.
    /// </summary>
    public int MaxRetryAttempts { get; init; } = 3;

    /// <summary>
    /// Base delay for the exponential backoff. A <c>Retry-After</c> header on the response wins
    /// over this, which is how Confluence Cloud tells a bulk publish how long to wait.
    /// </summary>
    /// <remarks>
    /// Three attempts at a 2s base is a starting point, not a measured one: PLAN.md §13 S5 (rate
    /// limits on an ~80-page bulk publish) is the spike that should tune it against real numbers.
    /// </remarks>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Budget for one logical call including all its retries, after which the request is
    /// abandoned. Covers the whole pipeline, so it must exceed the summed backoff.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(1);
}
