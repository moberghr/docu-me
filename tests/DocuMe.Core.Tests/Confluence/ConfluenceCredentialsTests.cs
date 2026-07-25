using System.Text;
using DocuMe.Core.Confluence;
using Shouldly;

namespace DocuMe.Core.Tests.Confluence;

/// <summary>
/// Credentials come from the environment and nowhere else (PLAN.md §4, CLAUDE.md §0.3). These tests
/// read through an injected lookup rather than the process environment, so the suite never depends
/// on — or sets — a real token.
/// </summary>
public sealed class ConfluenceCredentialsTests
{
    private const string Email = "bot@example.com";
    private const string ApiToken = "very-secret-token";

    [Fact]
    public void Reads_both_variables_from_the_environment()
    {
        var credentials = ConfluenceCredentials.FromEnvironment(ReadVariable);

        credentials.Email.ShouldBe(Email);
        Decode(credentials.BasicAuthParameter).ShouldBe($"{Email}:{ApiToken}");
    }

    [Fact]
    public void A_missing_token_names_the_token_variable_and_leaves_the_email_one_out_of_it()
    {
        var exception = Should.Throw<ConfluenceCredentialsException>(
            () => ConfluenceCredentials.FromEnvironment(
                name => string.Equals(name, ConfluenceCredentials.EmailVariable, StringComparison.Ordinal) ? Email : null));

        exception.MissingVariables.ShouldBe([ConfluenceCredentials.TokenVariable]);
        exception.Message.ShouldContain(ConfluenceCredentials.TokenVariable);
    }

    [Fact]
    public void An_empty_variable_counts_as_missing()
    {
        var exception = Should.Throw<ConfluenceCredentialsException>(
            () => ConfluenceCredentials.FromEnvironment(_ => "   "));

        exception.MissingVariables.ShouldBe(
            [ConfluenceCredentials.EmailVariable, ConfluenceCredentials.TokenVariable]);
    }

    /// <summary>
    /// The reason <see cref="ConfluenceCredentials"/> is not a record: a generated
    /// <c>ToString</c> would print the token into the first log line that formats the object.
    /// </summary>
    [Fact]
    public void Formatting_the_credentials_never_prints_the_token()
    {
        var credentials = ConfluenceCredentials.FromEnvironment(ReadVariable);

        var text = credentials.ToString();

        text.ShouldNotContain(ApiToken);
        text.ShouldContain(Email);
    }

    private static string? ReadVariable(string name) => name switch
    {
        ConfluenceCredentials.EmailVariable => Email,
        ConfluenceCredentials.TokenVariable => ApiToken,
        _ => null,
    };

    private static string Decode(string basicAuthParameter)
        => Encoding.UTF8.GetString(Convert.FromBase64String(basicAuthParameter));
}
