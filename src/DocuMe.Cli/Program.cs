using System.CommandLine;
using DocuMe.Cli.Commands;

namespace DocuMe.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand(
            "DocuMe — deterministic markdown-to-Confluence documentation publisher.")
        {
            InitCommand.Build(),
            ConvertCommand.Build(),
            PublishCommand.Build(),
            SyncCommand.Build(),
            DriftCommand.Build(),
            DashboardCommand.Build(),
            StatusCommand.Build(),
        };

        // Show help on a bare invocation so `docume` is self-describing rather than
        // silent; --help stays valid alongside subcommands.
        var invocationArgs = args.Length == 0 ? ["--help"] : args;

        // InvokeAsync, not Invoke: `convert --render-mermaid` runs an async action (a Node process
        // per diagram). It drives synchronous actions like `init` just the same.
        return await rootCommand.Parse(invocationArgs).InvokeAsync().ConfigureAwait(false);
    }
}
