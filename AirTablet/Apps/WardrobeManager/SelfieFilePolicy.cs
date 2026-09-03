using System.Text.RegularExpressions;

namespace WardrobeManager;

internal static class SelfieFilePolicy
{
    public static IReadOnlyList<string> ReplacedCaptures(string folder, string replacement, Guid presetId, IEnumerable<string> referencedImages)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = Path.GetFullPath(replacement);
        if (!Directory.Exists(root) || !File.Exists(current) ||
            !string.Equals(Path.GetDirectoryName(current), root, StringComparison.OrdinalIgnoreCase)) return [];
        var referenced = referencedImages.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pattern = $@"^WardrobeSelfie-{presetId:N}.*-\d{{8}}-\d{{6}}-\d{{3}}\.png$";
        var replacedAt = File.GetLastWriteTimeUtc(current);
        return Directory.EnumerateFiles(root, "*.png", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Where(path => !path.Equals(current, StringComparison.OrdinalIgnoreCase) && !referenced.Contains(path))
            .Where(path => Regex.IsMatch(Path.GetFileName(path), pattern, RegexOptions.IgnoreCase))
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Where(path => File.GetLastWriteTimeUtc(path) <= replacedAt).ToList();
    }
}
