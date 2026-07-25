namespace DocuMe.Core.Git;

/// <summary>
/// Thrown when git could not answer a question whose answer the caller cannot do without. The message
/// is written for a terminal: what was asked, what to check, and what git said.
/// </summary>
/// <remarks>
/// Not every git failure is one of these. Reading <c>HEAD</c> for the <c>lastPublishedSha</c> stamp is
/// best effort and answers <c>null</c> instead (<see cref="GitRepository.TryReadHeadAsync"/>); a
/// question that narrows a publish run cannot degrade that way, because the fallback would be
/// publishing everything.
/// </remarks>
public sealed class GitException : Exception
{
    public GitException(string message)
        : base(message)
    {
    }

    public GitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
