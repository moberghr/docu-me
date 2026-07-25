using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DocuMe.Core.Tests.Cli;

/// <summary>
/// Starts the built <c>docume</c> the way a user starts it: a process, with arguments, read back
/// through its exit code and its two streams. Every test class in this folder goes through here, so
/// the CLI is located and launched in exactly one place.
/// </summary>
/// <remarks>
/// The CLI is referenced for build order only (see the csproj), never as an assembly: a test that
/// could call <c>InitCommand.Build()</c> in-process would stop covering the entry point that composes
/// them.
/// </remarks>
internal static class DocumeCli
{
    /// <summary>
    /// Placeholders, not credentials (rule §1.1). Set on every run rather than inherited or removed: a
    /// suite whose output depends on whether the developer exported a token is a suite that passes
    /// locally only, and a command that skips its Confluence work when the variables are missing would
    /// otherwise pass for the wrong reason.
    /// </summary>
    internal const string Email = "bot@example.com";

    internal const string ApiToken = "not-a-real-token";

    // Declaration order is initialization order, and the three below all read RepoRoot.
    internal static string RepoRoot { get; } = Locate();

    internal static string Assembly { get; } = LocateCli();

    internal static string SolutionVersion { get; } = ReadSolutionVersion();

    internal static string ToolCommandName { get; } = ReadToolCommandName();

    internal static CliRun Invoke(string workingDirectory, params string[] args) =>
        Invoke(workingDirectory, environment: null, args);

    /// <summary>
    /// Runs the CLI, optionally with extra environment variables layered over the placeholders.
    /// </summary>
    /// <param name="workingDirectory">The directory the command runs in, as a user's shell would be.</param>
    /// <param name="environment">Variables to set or override, or <c>null</c>.</param>
    /// <param name="args">The command line, already split.</param>
    internal static CliRun Invoke(
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        params string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.ArgumentList.Add(Assembly);

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["DOCUME_CONFLUENCE_EMAIL"] = Email;
        info.Environment["DOCUME_CONFLUENCE_TOKEN"] = ApiToken;

        foreach (var (key, value) in environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("dotnet did not start.");

        // Read before waiting: a command that fills a pipe buffer would deadlock against WaitForExit.
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new CliRun(process.ExitCode, output, error, string.Join(' ', args));
    }

    /// <summary>
    /// The CLI built beside the test assembly: both live under
    /// <c>bin/&lt;configuration&gt;/&lt;tfm&gt;</c>, and the csproj's build-order reference is what
    /// guarantees it is there.
    /// </summary>
    private static string LocateCli()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = here.Parent?.Name
            ?? throw new InvalidOperationException($"No configuration directory above {here.FullName}.");

        var dll = Path.Combine(RepoRoot, "src", "DocuMe.Cli", "bin", configuration, here.Name, "DocuMe.Cli.dll");

        if (!File.Exists(dll))
        {
            throw new InvalidOperationException(
                $"No CLI to run at {dll}. DocuMe.Core.Tests.csproj references DocuMe.Cli for build order, "
                + "so a plain `dotnet build` should have produced it.");
        }

        return dll;
    }

    private static string ReadToolCommandName()
    {
        var csproj = XDocument.Load(Path.Combine(RepoRoot, "src", "DocuMe.Cli", "DocuMe.Cli.csproj"));
        var name = csproj.Descendants("ToolCommandName").SingleOrDefault()
            ?? throw new InvalidOperationException("DocuMe.Cli.csproj declares no <ToolCommandName>.");

        return name.Value.Trim();
    }

    private static string ReadSolutionVersion()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot, "Directory.Build.props"));
        var version = props.Descendants("Version").SingleOrDefault()
            ?? throw new InvalidOperationException("Directory.Build.props declares no <Version> (§12).");

        return version.Value.Trim();
    }

    private static string Locate()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DocuMe.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so the CLI cannot be found.");
    }
}

/// <summary>One run of the CLI: what it exited with, and what it said on each stream.</summary>
internal sealed partial record CliRun(int Code, string Output, string Error, string Arguments)
{
    /// <summary>Everything a failure needs; the interesting half is often on stderr.</summary>
    internal string Diagnostics => $"""
        `docume {Arguments}` exited {Code}.
        --- stdout ---
        {Output}
        --- stderr ---
        {Error}
        """;

    /// <summary>
    /// Spectre.Console wraps at 80 columns once stdout is redirected, so a sentence assertion has to
    /// read across the wrap it inserted.
    /// </summary>
    internal string Flowed => WhitespaceRun().Replace(Output, " ");

    /// <summary>Both streams flowed together, for a message that could land on either.</summary>
    internal string FlowedAll => WhitespaceRun().Replace($"{Output}\n{Error}", " ");

    [GeneratedRegex(@"\s+", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespaceRun();
}
