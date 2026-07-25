using System.Text;

namespace DocuMe.Core.Confluence;

/// <summary>
/// Confluence Cloud basic-auth credentials: an Atlassian account email plus an API token
/// (PLAN.md §4). Read from environment variables only.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not a record and deliberately without a token property. A record's generated
/// <c>ToString</c> prints every member, so the token would land in any log line, exception
/// message or debugger view that formats the object — exactly what CLAUDE.md §0.3 and
/// .claude/rules/security.md §1.1 forbid. The token leaves this type only as the base64 blob an
/// <c>Authorization</c> header needs.
/// </para>
/// </remarks>
public sealed class ConfluenceCredentials
{
    /// <summary>Environment variable holding the Atlassian account email.</summary>
    public const string EmailVariable = "DOCUME_CONFLUENCE_EMAIL";

    /// <summary>Environment variable holding the Atlassian API token.</summary>
    public const string TokenVariable = "DOCUME_CONFLUENCE_TOKEN";

    private readonly string _apiToken;

    public ConfluenceCredentials(string email, string apiToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiToken);

        Email = email;
        _apiToken = apiToken;
    }

    /// <summary>
    /// The account email. Not a secret (it is the identity half of the pair) and kept readable so
    /// a "wrong account" failure is diagnosable.
    /// </summary>
    public string Email { get; }

    /// <summary>
    /// The <c>email:token</c> pair base64-encoded, ready as the parameter of an HTTP Basic
    /// <c>Authorization</c> header.
    /// </summary>
    public string BasicAuthParameter => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{_apiToken}"));

    /// <summary>
    /// Reads both variables from the environment.
    /// </summary>
    /// <param name="readVariable">
    /// How to read one variable, for tests. Defaults to the process environment; a test passing
    /// its own lookup keeps credentials out of the test process entirely.
    /// </param>
    /// <exception cref="ConfluenceCredentialsException">Either variable is missing or empty.</exception>
    public static ConfluenceCredentials FromEnvironment(Func<string, string?>? readVariable = null)
    {
        var read = readVariable ?? Environment.GetEnvironmentVariable;
        var email = read(EmailVariable);
        var apiToken = read(TokenVariable);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(email))
        {
            missing.Add(EmailVariable);
        }

        if (string.IsNullOrWhiteSpace(apiToken))
        {
            missing.Add(TokenVariable);
        }

        if (missing.Count > 0)
        {
            throw new ConfluenceCredentialsException(missing);
        }

        return new ConfluenceCredentials(email!, apiToken!);
    }

    /// <summary>Redacts the token, so formatting these credentials can never leak it.</summary>
    public override string ToString() => $"ConfluenceCredentials {{ Email = {Email}, ApiToken = (redacted) }}";
}
