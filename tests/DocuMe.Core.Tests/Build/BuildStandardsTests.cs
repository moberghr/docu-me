using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;

namespace DocuMe.Core.Tests.Build;

/// <summary>
/// The Moberg house build standards (<c>.claude/references/dotnet/moberg-house-standards.md</c>)
/// as assertions over <c>Directory.Build.props</c>, <c>Directory.Packages.props</c>, the project
/// files and <c>.editorconfig</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every fact below is true today and was, until this class, held by nothing but the convention
/// itself. That is the failure mode worth naming: the properties here do not produce warnings when
/// they go missing, they produce <em>silence</em>. Drop <c>GenerateDocumentationFile</c> and the
/// documentation analyzers stop running while the build reports zero warnings — which is exactly
/// what happened once already, and is why SA0001 is pinned loud below. Add a <c>NoWarn</c> for
/// convenience that happens to name CS8032 and an entire analyzer pack can fail to load with the
/// build still green and quiet.
/// </para>
/// <para>
/// The honest limit: this class reads <em>configuration</em>, so it cannot prove the analyzer packs
/// still execute. Only <c>dotnet build /p:ReportAnalyzer=true -v:d</c> shows that, and it is far too
/// slow for the default run. What is asserted here is that nothing has been configured to stop them.
/// </para>
/// <para>
/// One house rule is deliberately <em>not</em> asserted: Central Package Management's "no inline
/// <c>PackageReference Version=</c>". NuGet already fails the build on it (error NU1008, measured at
/// <c>.mtk/paths-115/probe-cpm-enforcement.py</c>), so a test would restate an existing hard gate.
/// Every assertion below was mutation-checked the same way — <c>.mtk/paths-115/mutate-build-standards.py</c>.
/// </para>
/// </remarks>
public sealed partial class BuildStandardsTests
{
    /// <summary>
    /// Diagnostics that report <em>a check did not run</em> rather than a code smell, so silencing one
    /// hides the loss of every rule behind it. They are not ordinary analyzer rules and none of them
    /// belongs in a NoWarn list; the compiler and NuGet own them, which is why <c>.editorconfig</c>'s
    /// 68 <c>severity = none</c> entries cannot cover this class (iteration 114's sweep).
    /// </summary>
    private static readonly string[] MetaDiagnostics =
    [
        "AD0001", // an analyzer threw, and its rules stopped running
        "CS8032", // an analyzer instance could not be created
        "CS8034", // an analyzer assembly could not be loaded at all
        "CS8785", // a source generator failed
        "CS9057", // analyzer built against a newer Roslyn, may not run
        "NU1900", // the vulnerability audit itself failed
    ];

    /// <summary>The four packs the house standard names, all applied solution-wide.</summary>
    private static readonly string[] AnalyzerPacks =
    [
        "StyleCop.Analyzers",
        "Roslynator.Analyzers",
        "SonarAnalyzer.CSharp",
        "Meziantou.Analyzer",
    ];

    /// <summary>Severities at which a diagnostic can no longer fail a build.</summary>
    private static readonly string[] Silencing = ["none", "silent", "suggestion"];

    /// <summary>Every MSBuild file whose properties apply to the shipped assemblies.</summary>
    private static readonly string[] BuildFiles =
    [
        "Directory.Build.props",
        "Directory.Packages.props",
        Path.Combine("src", "DocuMe.Core", "DocuMe.Core.csproj"),
        Path.Combine("src", "DocuMe.Cli", "DocuMe.Cli.csproj"),
        Path.Combine("tests", "DocuMe.Core.Tests", "DocuMe.Core.Tests.csproj"),
    ];

    [Fact]
    public void Warnings_are_errors_solution_wide()
    {
        const string message = "The house standard builds warnings as errors; without it every "
            + "analyzer below degrades to advice nobody reads.";

        Property("TreatWarningsAsErrors").ShouldBe("true", message);
    }

    [Fact]
    public void A_documentation_file_is_generated_so_the_documentation_analyzers_can_run()
    {
        // Not a docs preference. Without a documentation file the compiler parses doc comments with
        // DocumentationMode.None, so SA1604/SA1642/RCS1139 and the compiler's own cref validation
        // (CS1574/CS0419) never fire at all.
        Property("GenerateDocumentationFile").ShouldBe(
            "true",
            "Dropping this silently disables every documentation analyzer.");
    }

    [Fact]
    public void SA0001_stays_loud_because_it_is_the_tripwire_for_that()
    {
        // The one rule in the whole .editorconfig that reports a *disabled check* rather than a code
        // smell: StyleCop raises it when documentation analysis is off. Kept at warning (an error,
        // under TreatWarningsAsErrors) so the property above cannot go missing quietly.
        var severity = EditorConfigSeverities().GetValueOrDefault("SA0001");

        severity.ShouldNotBeNull(".editorconfig no longer sets SA0001, so its default applies.");

        var message = $"SA0001 is set to '{severity}'. It is the only signal that "
            + "GenerateDocumentationFile was dropped, so silencing it hides the thing it guards.";

        Silencing.ShouldNotContain(severity, message);
    }

    [Fact]
    public void No_meta_diagnostic_is_silenced_anywhere()
    {
        var suppressed = SuppressedInMsBuild()
            .Concat(SuppressedInEditorConfig())
            .ToList();

        suppressed.ShouldBeEmpty(
            "These report that a check stopped running, not that code is wrong. Suppressing one "
            + "makes a failed analyzer pack, a failed generator or a failed audit look like a "
            + "clean build. Fix the cause instead.");
    }

    [Fact]
    public void No_blanket_rule_switches_the_analyzers_off()
    {
        // Category-scoped entries are a judgement call and there is one in the tree
        // (StyleCop.CSharp.OrderingRules). An unscoped `dotnet_analyzer_diagnostic.severity` is not:
        // it is every analyzer rule at once, and it would leave the four packs installed, running,
        // and reporting nothing.
        var blanket = ConfigLines()
            .Select(line => BlanketSeverity().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups["severity"].Value)
            .Where(severity => Silencing.Contains(severity, StringComparer.OrdinalIgnoreCase))
            .ToList();

        blanket.ShouldBeEmpty(
            "An unscoped dotnet_analyzer_diagnostic.severity turns off every rule in every pack. "
            + "Scope the relaxation to the category or rule that actually needs it.");
    }

    [Fact]
    public void Every_house_analyzer_pack_is_referenced_with_PrivateAssets_all()
    {
        var referenced = PackageReferences(Path.Combine(RepoRoot, "Directory.Build.props"))
            .ToDictionary(
                reference => reference.Attribute("Include")?.Value ?? string.Empty,
                reference => reference.Attribute("PrivateAssets")?.Value,
                StringComparer.Ordinal);

        var wrong = new List<string>();

        foreach (var pack in AnalyzerPacks)
        {
            if (!referenced.TryGetValue(pack, out var privateAssets))
            {
                wrong.Add($"{pack} (not referenced)");
                continue;
            }

            if (string.Equals(privateAssets, "all", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            wrong.Add($"{pack} (PrivateAssets='{privateAssets ?? "unset"}')");
        }

        wrong.ShouldBeEmpty(
            "Directory.Build.props applies the house analyzer set to every project, and "
            + "PrivateAssets=all keeps it from flowing to whoever consumes the packages.");
    }

    /// <summary>
    /// A property's value from <c>Directory.Build.props</c>, which is where every solution-wide
    /// property lives.
    /// </summary>
    private static string? Property(string name)
    {
        var props = XDocument.Load(Path.Combine(RepoRoot, "Directory.Build.props"));
        var values = props.Descendants(name).Select(element => element.Value.Trim()).ToList();

        values.Count.ShouldBeLessThanOrEqualTo(
            1, $"Directory.Build.props sets <{name}> more than once; the last one silently wins.");

        return values.SingleOrDefault();
    }

    private static IEnumerable<XElement> PackageReferences(string path) =>
        XDocument.Load(path).Descendants("PackageReference");

    /// <summary>
    /// Meta-diagnostics named by a <c>NoWarn</c> or <c>WarningsNotAsErrors</c> in any build file.
    /// Both are scanned: the second does not hide the warning but does stop it failing the build,
    /// which for this class of diagnostic is the same outcome in a CI log nobody reads.
    /// </summary>
    private static IEnumerable<string> SuppressedInMsBuild()
    {
        foreach (var file in BuildFiles)
        {
            var document = XDocument.Load(Path.Combine(RepoRoot, file));

            var listed = document
                .Descendants()
                .Where(element => element.Name.LocalName is "NoWarn" or "WarningsNotAsErrors")
                .SelectMany(element => element.Value.Split([';', ','], StringSplitOptions.TrimEntries))
                .Where(id => MetaDiagnostics.Contains(id, StringComparer.OrdinalIgnoreCase));

            foreach (var id in listed)
            {
                yield return $"{file}: {id}";
            }
        }
    }

    private static IEnumerable<string> SuppressedInEditorConfig() =>
        EditorConfigSeverities()
            .Where(entry => MetaDiagnostics.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
            .Where(entry => Silencing.Contains(entry.Value, StringComparer.OrdinalIgnoreCase))
            .Select(entry => $".editorconfig: {entry.Key} = {entry.Value}");

    /// <summary>
    /// Every <c>dotnet_diagnostic.&lt;id&gt;.severity</c> in <c>.editorconfig</c>, flattened across
    /// its sections. Flattening is deliberate: a rule silenced in <em>any</em> scope is a rule that
    /// does not fire somewhere, and for the diagnostics this class cares about there is no scope
    /// where that is acceptable.
    /// </summary>
    private static Dictionary<string, string> EditorConfigSeverities()
    {
        var severities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in ConfigLines())
        {
            var match = RuleSeverity().Match(line);

            if (match.Success)
            {
                severities[match.Groups["id"].Value] = match.Groups["severity"].Value;
            }
        }

        return severities;
    }

    private static string[] ConfigLines() => File.ReadAllLines(Path.Combine(RepoRoot, ".editorconfig"));

    [GeneratedRegex(
        @"^\s*dotnet_diagnostic\.(?<id>[A-Za-z]+[0-9]+)\.severity\s*=\s*(?<severity>\w+)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex RuleSeverity();

    [GeneratedRegex(
        @"^\s*dotnet_analyzer_diagnostic\.severity\s*=\s*(?<severity>\w+)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlanketSeverity();

    private static string RepoRoot { get; } = Locate();

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
            $"No DocuMe.slnx above {AppContext.BaseDirectory}, so the build files cannot be found.");
    }
}
