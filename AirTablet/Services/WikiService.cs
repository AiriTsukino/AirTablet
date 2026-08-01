namespace AirTablet.Services;

internal enum WikiBlockKind
{
    Heading,
    Subheading,
    Paragraph,
    Bullet,
    Tip,
    Warning,
    Code,
    Divider,
}

internal sealed record WikiBlock(WikiBlockKind Kind, string Text);

internal sealed class WikiArticle
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = "Untitled";
    public string Summary { get; init; } = string.Empty;
    public string Category { get; init; } = "Guides";
    public int Order { get; init; } = 100;
    public IReadOnlyList<WikiBlock> Blocks { get; init; } = [];

    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        var searchable = string.Join('\n',
            new[] { Title, Summary, Category }
                .Concat(Blocks.Select(block => block.Text)));
        return search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class WikiService
{
    private const string WikiRelativeDirectory = @"Resources\Wiki";

    public IReadOnlyList<WikiArticle> Articles { get; private set; } = [];
    public string Status { get; private set; } = "Loading wiki...";

    public WikiService() => Reload();

    public void Reload()
    {
        try
        {
            var directory = Path.Combine(
                DalamudServices.PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty,
                WikiRelativeDirectory);
            if (!Directory.Exists(directory))
            {
                Articles = [];
                Status = $"Wiki files were not found at {WikiRelativeDirectory}.";
                return;
            }

            Articles = Directory.GetFiles(directory, "*.wiki.txt")
                .Select(ParseArticle)
                .Where(article => article is not null)
                .Cast<WikiArticle>()
                .OrderBy(article => article.Order)
                .ThenBy(article => article.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Status = Articles.Count == 0
                ? "No wiki articles are available."
                : $"Loaded {Articles.Count} wiki article{(Articles.Count == 1 ? string.Empty : "s")}.";
        }
        catch (Exception ex)
        {
            Articles = [];
            Status = "The bundled wiki could not be loaded.";
            DalamudServices.Log.Warning(ex, "AirTablet could not load its bundled wiki.");
        }
    }

    private static WikiArticle? ParseArticle(string path)
    {
        var id = Path.GetFileName(path).Replace(".wiki.txt", string.Empty, StringComparison.OrdinalIgnoreCase);
        var title = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(path));
        var summary = string.Empty;
        var category = "Guides";
        var order = 100;
        var blocks = new List<WikiBlock>();
        var codeLines = new List<string>();
        var inCode = false;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            if (line.Equals("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    blocks.Add(new WikiBlock(WikiBlockKind.Code, string.Join('\n', codeLines)));
                    codeLines.Clear();
                }
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                codeLines.Add(line);
                continue;
            }

            if (TryMetadata(line, "@id", out var value)) { id = value; continue; }
            if (TryMetadata(line, "@title", out value)) { title = value; continue; }
            if (TryMetadata(line, "@summary", out value)) { summary = value; continue; }
            if (TryMetadata(line, "@category", out value)) { category = value; continue; }
            if (TryMetadata(line, "@order", out value))
            {
                if (int.TryParse(value, out var parsedOrder)) order = parsedOrder;
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed == "---") { blocks.Add(new WikiBlock(WikiBlockKind.Divider, string.Empty)); continue; }
            if (trimmed.StartsWith("## ")) { blocks.Add(new WikiBlock(WikiBlockKind.Subheading, trimmed[3..].Trim())); continue; }
            if (trimmed.StartsWith("# ")) { blocks.Add(new WikiBlock(WikiBlockKind.Heading, trimmed[2..].Trim())); continue; }
            if (trimmed.StartsWith("- ")) { blocks.Add(new WikiBlock(WikiBlockKind.Bullet, trimmed[2..].Trim())); continue; }
            if (trimmed.StartsWith("> tip:", StringComparison.OrdinalIgnoreCase)) { blocks.Add(new WikiBlock(WikiBlockKind.Tip, trimmed[6..].Trim())); continue; }
            if (trimmed.StartsWith("> warning:", StringComparison.OrdinalIgnoreCase)) { blocks.Add(new WikiBlock(WikiBlockKind.Warning, trimmed[10..].Trim())); continue; }
            blocks.Add(new WikiBlock(WikiBlockKind.Paragraph, trimmed));
        }

        if (inCode && codeLines.Count > 0)
            blocks.Add(new WikiBlock(WikiBlockKind.Code, string.Join('\n', codeLines)));
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        return new WikiArticle
        {
            Id = id.Trim(),
            Title = title.Trim(),
            Summary = summary.Trim(),
            Category = string.IsNullOrWhiteSpace(category) ? "Guides" : category.Trim(),
            Order = order,
            Blocks = blocks,
        };
    }

    private static bool TryMetadata(string line, string key, out string value)
    {
        value = string.Empty;
        if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            return false;
        if (line.Length > key.Length && !char.IsWhiteSpace(line[key.Length]))
            return false;
        value = line[key.Length..].Trim();
        return true;
    }
}
