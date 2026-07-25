using System.Net;

namespace DocuMe.Core.Confluence;

/// <summary>
/// Base for every failure <see cref="ConfluenceClient"/> raises. Callers that want to treat all
/// Confluence trouble alike (a publish run reporting a page as failed) catch this; callers that
/// must stop the whole run catch <see cref="ConfluenceAuthenticationException"/> specifically.
/// </summary>
public abstract class ConfluenceException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Thrown when the credential environment variables are missing or empty (PLAN.md §4).
/// </summary>
/// <remarks>
/// Names the variables and nothing else: there is no config-file fallback to suggest, because
/// <c>docume.json</c> is committed and must stay secret-free (CLAUDE.md §0.3, §1.1).
/// </remarks>
public sealed class ConfluenceCredentialsException(IReadOnlyList<string> missingVariables)
    : ConfluenceException(
        $"Confluence credentials are not set: {string.Join(", ", missingVariables)}. "
        + $"Export {ConfluenceCredentials.EmailVariable} (your Atlassian account email) and "
        + $"{ConfluenceCredentials.TokenVariable} (an API token from "
        + "id.atlassian.com/manage-profile/security/api-tokens). DocuMe reads credentials from "
        + "the environment only — never from docume.json, which is committed.")
{
    public IReadOnlyList<string> MissingVariables { get; } = missingVariables;
}

/// <summary>
/// Thrown on 401 or 403. A hard stop by design: the request is never retried, because a bad or
/// expired token retried across an ~80-page bulk publish is how an account gets locked out
/// (PLAN.md §6, .claude/rules/security.md §1.2).
/// </summary>
public sealed class ConfluenceAuthenticationException(HttpStatusCode statusCode, string requestPath)
    : ConfluenceException(
        $"Confluence rejected the request ({(int)statusCode} {statusCode}) for '{requestPath}'. "
        + $"This is an authentication or permission failure, not a transient one, so DocuMe "
        + $"stopped instead of retrying. Check that {ConfluenceCredentials.TokenVariable} has not "
        + $"expired or been revoked, that {ConfluenceCredentials.EmailVariable} is the account "
        + "that owns it, and that the account can see the target space.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string RequestPath { get; } = requestPath;
}

/// <summary>
/// Thrown when Confluence answered with a non-success status that is not an auth failure —
/// including a 429 or 5xx that survived every retry.
/// </summary>
public sealed class ConfluenceApiException(HttpStatusCode statusCode, string requestPath, string responseExcerpt)
    : ConfluenceException(
        $"Confluence returned {(int)statusCode} {statusCode} for '{requestPath}': {responseExcerpt}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string RequestPath { get; } = requestPath;
}

/// <summary>
/// Thrown when a successful response body is not what the API contract promises: unparseable
/// JSON, a missing <c>results</c> array, or an entity missing a field DocuMe depends on.
/// </summary>
/// <remarks>
/// Deliberately loud rather than degrading to an empty result. A publish pipeline that read
/// "no page found" out of a malformed response would create a duplicate page next to the real
/// one, and the fix costs a human page-by-page cleanup.
/// </remarks>
public sealed class ConfluenceProtocolException(string requestPath, string detail, Exception? innerException = null)
    : ConfluenceException(
        $"Confluence returned an unexpected response body for '{requestPath}': {detail}",
        innerException)
{
    public string RequestPath { get; } = requestPath;
}
