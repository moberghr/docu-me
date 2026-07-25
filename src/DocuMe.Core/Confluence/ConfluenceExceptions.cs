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
/// <remarks>
/// <paramref name="operation"/> is what makes a write's failure actionable. Confluence answers a
/// duplicate title, a body over its 5 MB limit and a parent it cannot see all as a flat 400 or 404
/// with prose, so the message names what DocuMe was doing — which page, which title, which version —
/// alongside whatever Confluence said. A read passes no operation: its path already carries the
/// space key or page id.
/// </remarks>
public sealed class ConfluenceApiException(
    HttpStatusCode statusCode,
    string requestPath,
    string responseExcerpt,
    string? operation = null)
    : ConfluenceException(Describe(statusCode, requestPath, responseExcerpt, operation))
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string RequestPath { get; } = requestPath;

    /// <summary>What DocuMe was doing, e.g. <c>creating page 'Loans' in space 98304</c>; null for a read.</summary>
    public string? Operation { get; } = operation;

    private static string Describe(
        HttpStatusCode statusCode,
        string requestPath,
        string responseExcerpt,
        string? operation)
    {
        var what = operation is null ? string.Empty : $" while {operation}";
        return $"Confluence returned {(int)statusCode} {statusCode} for '{requestPath}'{what}: {responseExcerpt}";
    }
}

/// <summary>
/// Thrown on 409 Conflict from a write: the page moved on between the read that supplied its version
/// and this attempt to overwrite it, because a human edited it in the browser or a second run raced
/// this one.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately neither retried nor silently re-read, and the difference is worth stating because
/// both alternatives look reasonable. Re-reading the version and pushing again would always
/// "succeed" — the repo is the source of truth and republish overwrites hand edits by design (PLAN.md
/// §9.1) — so the write would go through either way. What that costs is the only signal DocuMe gets
/// that someone was editing: a race between two publish runs would interleave two different repo
/// states silently, and a human's in-browser edit would be discarded without anything to read
/// afterwards. Failing the page reports it once; the re-run then reads the current version and
/// republishes from the repo, which is the intended resolution.
/// </para>
/// <para>
/// Not retried by the transport either: the retry predicate handles 408/429/5xx only
/// (<see cref="ConfluenceClient"/>), so a 409 never gets a second attempt regardless of this type.
/// </para>
/// <para>
/// Atlassian's v2 OpenAPI document lists no 409 for any endpoint — <c>PUT /pages/{id}</c> documents
/// 400, 401 and 404 — but the API answers 409 "Version must be incremented when updating a page" in
/// practice. Hence both paths are handled: a 409 lands here, and a 400 lands in
/// <see cref="ConfluenceApiException"/> quoting Confluence's own words.
/// </para>
/// </remarks>
public sealed class ConfluenceConflictException(string requestPath, string operation, string responseExcerpt)
    : ConfluenceException(
        $"Confluence answered 409 Conflict while {operation} ('{requestPath}'): {responseExcerpt}. The page "
        + "changed between the version DocuMe read and this write, so DocuMe stopped rather than "
        + "overwriting blind. Re-run publish: the next run reads the current version and republishes "
        + "from the repo, which is the source of truth.")
{
    public string RequestPath { get; } = requestPath;

    /// <summary>What DocuMe was doing, e.g. <c>updating page 65601 'Loans' to version 8</c>.</summary>
    public string Operation { get; } = operation;
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
