using System.Text.Json.Serialization;

namespace DocuMe.Core.Config;

/// <summary>
/// Model for a consumer repo's <c>docume.json</c> (PLAN.md §5.1). Committed,
/// contains no secrets. Loaded and validated by <see cref="ConfigLoader"/>.
/// </summary>
public sealed record DocumeConfig
{
    /// <summary>JSON Schema reference; ignored by the loader.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public ConfluenceConfig Confluence { get; init; } = new();

    public WikiConfig Wiki { get; init; } = new();

    public LabelsConfig Labels { get; init; } = new();

    public DashboardConfig Dashboard { get; init; } = new();

    public DriftConfig Drift { get; init; } = new();

    public LinksConfig Links { get; init; } = new();

    public MermaidConfig Mermaid { get; init; } = new();
}

public sealed record ConfluenceConfig
{
    /// <summary>Wiki base URL, e.g. <c>https://kvika.atlassian.net/wiki</c>. Required.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Space key, e.g. <c>AUR</c>. Required.</summary>
    public string? SpaceKey { get; init; }

    public string? SpaceId { get; init; }

    /// <summary>Parent page under which the wiki tree is published.</summary>
    public string? RootPageId { get; init; }

    /// <summary>
    /// Space keys this repo is not cleared to publish into. A run targeting one of them refuses to
    /// write unless a human unlocks it for that run (<c>--allow-protected-space</c>). Empty by default.
    /// </summary>
    /// <remarks>
    /// The write lock behind CLAUDE.md §0.1 and rule §1.4, expressed where repo-specific knowledge
    /// belongs (rule §9.5) rather than as a space key hardcoded in the tool. Going live is then a
    /// reviewed config commit that removes the entry, not a flag someone types often enough to stop
    /// reading. See <see cref="Publishing.PublishGuard"/>.
    /// </remarks>
    public IReadOnlyList<string> ProtectedSpaces { get; init; } = [];
}

public sealed record WikiConfig
{
    /// <summary>Root of the markdown wiki within the repo. Required.</summary>
    public string Root { get; init; } = "docs/wiki";

    public IReadOnlyList<string> Exclude { get; init; } = ["_meta/**"];

    public IReadOnlyList<ExtraPage> ExtraPages { get; init; } = [];

    /// <summary>File whose body becomes the tree root page.</summary>
    public string HomePage { get; init; } = "README.md";
}

/// <summary>An excluded file republished anyway under a chosen title (PLAN.md §5.1).</summary>
public sealed record ExtraPage
{
    public string? Path { get; init; }

    public string? Title { get; init; }
}

public sealed record LabelsConfig
{
    public string Approved { get; init; } = "approved";

    public string Stale { get; init; } = "stale";
}

public sealed record DashboardConfig
{
    public string Title { get; init; } = "Documentation Status";
}

public sealed record DriftConfig
{
    public string DefaultBranch { get; init; } = "dev";
}

public sealed record LinksConfig
{
    /// <summary>Optional: linkify source refs at the baseline SHA.</summary>
    public string? RepoBlobUrl { get; init; }
}

public sealed record MermaidConfig
{
    public string Renderer { get; init; } = "tools/render-mermaid.mjs";
}
