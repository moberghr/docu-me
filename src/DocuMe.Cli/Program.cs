using System.CommandLine;
using DocuMe.Cli.Commands;

namespace DocuMe.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand(
            "DocuMe — deterministic markdown-to-Confluence documentation publisher.")
        {
            InitCommand.Build(),
            ConvertCommand.Build(),
        };

        // Show help on a bare invocation so `docume` is self-describing rather than
        // silent; --help stays valid alongside subcommands.
        var invocationArgs = args.Length == 0 ? ["--help"] : args;

        return rootCommand.Parse(invocationArgs).Invoke();
    }
}
