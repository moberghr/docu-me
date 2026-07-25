using System.CommandLine;
using System.CommandLine.Help;
using DocuMe.Cli.Commands;

namespace DocuMe.Cli;

internal static class Program
{
    /// <summary>
    /// What the tool is installed as: <c>&lt;ToolCommandName&gt;</c> in <c>DocuMe.Cli.csproj</c>.
    /// <c>CliExecutionTests</c> pins this literal to the csproj so the two cannot drift.
    /// </summary>
    private const string ToolCommandName = "docume";

    private static async Task<int> Main(string[] args)
    {
        // A plain Command, not RootCommand: RootCommand takes its name from the entry assembly, so every
        // usage line and every parse error read "DocuMe.Cli" — the package id, which is not a command
        // anyone can type. Symbol.Name is read-only, so naming the root costs us the two options
        // RootCommand would have added for us; they are added back below.
        var rootCommand = new Command(
            ToolCommandName,
            "DocuMe — deterministic markdown-to-Confluence documentation publisher.")
        {
            new HelpOption(),
            new VersionOption(),
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
