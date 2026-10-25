namespace CustomRecorder.Core.Util;

/// <summary>Version numbers of GitHub release tags ("v1.2.3", "v1.2.3-beta.1").</summary>
public static class ReleaseVersion
{
    /// <summary>"v1.2.3" / "1.2.3" / "v1.2.3-beta.1" → 1.2.3 (pre-release suffix ignored). Null if not a version.</summary>
    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        int suffix = t.IndexOfAny(new[] { '-', '+' });
        if (suffix >= 0) t = t[..suffix];
        if (!Version.TryParse(t, out var v)) return null;
        return Normalize(v);
    }

    /// <summary>Major.Minor.Build only (assembly versions carry a fourth part).</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>First token of a "&lt;sha256&gt;  &lt;file&gt;" checksum file, lower-case.</summary>
    public static string ChecksumFromFile(string content) =>
        content.Trim().Split(new[] { ' ', '\t', '\r', '\n', '*' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
}
