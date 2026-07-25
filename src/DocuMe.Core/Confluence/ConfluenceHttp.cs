using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace DocuMe.Core.Confluence;

/// <summary>
/// Builds the <see cref="HttpClient"/> <see cref="ConfluenceClient"/> talks through: base address,
/// basic auth, and the retry pipeline PLAN.md §4 calls for
/// (<c>Microsoft.Extensions.Http.Resilience</c>).
/// </summary>
/// <remarks>
/// Kept as a handler pipeline rather than retry code inside the client for one reason that matters
/// to the tests: the retry/hard-stop rules then live where a real request goes through them, so a
/// WireMock server exercises the actual behavior instead of a hand-rolled loop.
/// </remarks>
internal static class ConfluenceHttp
{
    private const char SegmentSeparator = '/';

    internal static HttpClient CreateClient(ConfluenceClientOptions options, ConfluenceCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        Validate(options);

        var client = new HttpClient(CreateHandler(options), disposeHandler: true)
        {
            BaseAddress = WithTrailingSlash(options.BaseUrl),
            Timeout = options.Timeout,
        };

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", credentials.BasicAuthParameter);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return client;
    }

    /// <summary>
    /// A base URL is written without a trailing slash by every human who writes one
    /// (<c>https://site.atlassian.net/wiki</c>), but <see cref="Uri"/> composition treats the last
    /// segment of a slash-less base as a file and replaces it, which would silently move every
    /// request from <c>/wiki/api/v2/…</c> to <c>/api/v2/…</c>.
    /// </summary>
    private static Uri WithTrailingSlash(Uri baseUrl)
    {
        var text = baseUrl.AbsoluteUri;
        return text.EndsWith(SegmentSeparator)
            ? baseUrl
            : new Uri(text + SegmentSeparator, UriKind.Absolute);
    }

    /// <summary>
    /// The retry pipeline, or no pipeline at all when the caller asked for no retries.
    /// </summary>
    /// <remarks>
    /// Polly's own <c>MaxRetryAttempts</c> carries a <c>[Range(1, int.MaxValue)]</c>, so a configured
    /// strategy cannot express zero: building one throws a
    /// <see cref="System.ComponentModel.DataAnnotations.ValidationException"/> and the client never
    /// exists to make the single attempt that was asked for. Leaving the strategy out says the same
    /// thing in the shape Polly accepts — one attempt, no second one.
    /// </remarks>
    private static ResilienceHandler CreateHandler(ConfluenceClientOptions options)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();

        if (options.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                Delay = options.RetryDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,

                // Confluence Cloud answers a rate limit with Retry-After; honoring it beats
                // guessing, and PLAN.md §13 S5 expects a bulk publish to lean on it.
                ShouldRetryAfterHeader = true,
                ShouldHandle = arguments => ValueTask.FromResult(IsRetryable(arguments.Outcome)),
            });
        }

        return new ResilienceHandler(builder.Build())
        {
            InnerHandler = new SocketsHttpHandler(),
        };
    }

    /// <summary>
    /// Which outcomes are worth another attempt.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than delegated to the library's own transient predicate on purpose. The
    /// one rule that must never drift is that 401 and 403 are not retryable
    /// (.claude/rules/security.md §1.2), and a hand-written predicate cannot be widened by a
    /// package upgrade.
    /// </remarks>
    private static bool IsRetryable(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is not null)
        {
            // A connection that never completed says nothing about the credentials, so it retries.
            return outcome.Exception is HttpRequestException;
        }

        var statusCode = outcome.Result?.StatusCode;

        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return false;
        }

        return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || statusCode >= HttpStatusCode.InternalServerError;
    }

    private static void Validate(ConfluenceClientOptions options)
    {
        if (!options.BaseUrl.IsAbsoluteUri)
        {
            throw new ArgumentException(
                $"confluence.baseUrl must be an absolute URL, got '{options.BaseUrl}'.",
                nameof(options));
        }

        if (options.BaseUrl.Scheme is not "https" and not "http")
        {
            throw new ArgumentException(
                $"confluence.baseUrl must be http or https, got '{options.BaseUrl.Scheme}'.",
                nameof(options));
        }

        if (options.MaxRetryAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxRetryAttempts,
                "MaxRetryAttempts cannot be negative.");
        }

        if (options.RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.RetryDelay,
                "RetryDelay cannot be negative.");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Timeout,
                "Timeout must be positive.");
        }
    }
}
