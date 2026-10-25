namespace CustomRecorder.Core.Util;

/// <summary>File names from a user pattern such as "{종류}_{날짜}_{시간}".</summary>
public static class FileNaming
{
    public const string DefaultPattern = "{종류}_{날짜}_{시간}";
    public const int MaxLength = 120;

    /// <summary>Fills in {종류} {날짜} {시간} {대상} and makes the result a valid Windows file name (without extension).</summary>
    public static string Format(string? pattern, string kind, string target, DateTime time)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = DefaultPattern;
        var name = pattern
            .Replace("{종류}", kind)
            .Replace("{날짜}", time.ToString("yyyy-MM-dd"))
            .Replace("{시간}", time.ToString("HH-mm-ss"))
            .Replace("{대상}", target);
        // Windows-invalid characters (listed explicitly so the result is the same on every OS).
        foreach (var c in "<>:\"/\\|?*") name = name.Replace(c, '_');
        name = new string(name.Select(ch => ch < 32 ? '_' : ch).ToArray());
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length > MaxLength) name = name[..MaxLength].TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(name) ? $"{kind}_{time:yyyy-MM-dd_HH-mm-ss}" : name;
    }

    /// <summary>Returns <paramref name="path"/>, or the same name with _2, _3… if it already exists.</summary>
    public static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
