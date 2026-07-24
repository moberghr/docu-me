using System.CommandLine;

namespace DocuMe.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand(
            "DocuMe — deterministic markdown-to-Confluence documentation publisher.");

        // No subcommands land until M0's later slices; show help on a bare invocation
        // so `docume` is self-describing rather than silent.
        var invocationArgs = args.Length == 0 ? ["--help"] : args;

        return rootCommand.Parse(invocationArgs).Invoke();
    }
}
