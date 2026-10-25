using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Services;

/// <summary>A release on GitHub that is newer than the running version.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Name, string Notes, string PageUrl,
    string ZipUrl, long ZipSize, string? ChecksumUrl, bool Prerelease);

/// <summary>
/// Updates from GitHub Releases (github.com/JeongTaeWoong99/CustomRecorder).
/// <list type="number">
/// <item>Check: GitHub API → newest release whose version is higher than this exe.</item>
/// <item>Download the release zip and verify it against the published SHA-256.</item>
/// <item>Install: a running exe cannot be overwritten but can be renamed, so the current exe becomes
///   <c>*.old.exe</c>, the new one takes its name, and the new one is started. It removes the old file once it runs.</item>
/// </list>
/// </summary>
public static class UpdateService
{
    public const string Owner = "JeongTaeWoong99";
    public const string Repo = "CustomRecorder";
    public static string RepoUrl => $"https://github.com/{Owner}/{Repo}";
    public static string ReleasesUrl => $"{RepoUrl}/releases";
    public const string AfterUpdateArgument = "--after-update";

    // Lazy: built on first use, after Current is set (static fields initialise in declaration order).
    private static readonly Lazy<HttpClient> LazyHttp = new(CreateClient);
    private static HttpClient Http => LazyHttp.Value;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CustomRecorder/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Version of the running program (Major.Minor.Build).</summary>
    public static Version Current { get; } = ReadCurrent();

    private static Version ReadCurrent()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        return ReleaseVersion.Normalize(v);
    }

    public static string CurrentText => $"v{Current}";

    public static Version? ParseTag(string tag) => ReleaseVersion.Parse(tag);

    /// <summary>Returns the newest newer release, or null when this is the latest.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(bool includePrerelease, CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=20", ct);
        if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429)
            throw new InvalidOperationException("GitHub 요청 한도에 걸렸습니다. 잠시 후 다시 확인하세요.");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        ReleaseInfo? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            bool pre = r.GetProperty("prerelease").GetBoolean();
            if (pre && !includePrerelease) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            var version = ParseTag(tag);
            if (version == null || version <= Current || (best != null && version <= best.Version)) continue;

            string? zip = null, sha = null; long size = 0;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString();
                if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) { zip = url; size = a.GetProperty("size").GetInt64(); }
                else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
            }
            if (zip == null) continue; // build still uploading, or not a program release

            best = new ReleaseInfo(version, tag, r.GetProperty("name").GetString() is { Length: > 0 } n ? n : tag,
                r.GetProperty("body").GetString() ?? "", r.GetProperty("html_url").GetString() ?? ReleasesUrl,
                zip, size, sha, pre);
        }
        Log.Info(best == null ? $"업데이트 확인: 최신 ({CurrentText})" : $"업데이트 확인: {best.Tag} 있음 (현재 {CurrentText})");
        return best;
    }

    /// <summary>Downloads and verifies the release; returns the path of the new exe (in a temp folder).</summary>
    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "CustomRecorder", "update", release.Version.ToString());
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "release.zip");

        using (var response = await Http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.ZipSize;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zipPath);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        // Integrity: the release publishes "<sha256>  <file name>".
        if (release.ChecksumUrl == null) throw new InvalidOperationException("릴리스에 체크섬 파일이 없어 설치하지 않았습니다.");
        var expected = ReleaseVersion.ChecksumFromFile(await Http.GetStringAsync(release.ChecksumUrl, ct));
        string actual;
        await using (var fs = File.OpenRead(zipPath)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (actual != expected)
            throw new InvalidOperationException("받은 파일이 손상됐습니다 (SHA-256 불일치). 다시 시도하세요.");

        var extract = Path.Combine(dir, "files");
        ZipFile.ExtractToDirectory(zipPath, extract);
        var exe = Directory.GetFiles(extract, "CustomRecorder.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidOperationException("업데이트 파일에 CustomRecorder.exe가 없습니다.");
        Log.Info($"업데이트 받음: {release.Tag} ({new FileInfo(zipPath).Length / 1048576.0:F1} MB, SHA-256 확인)");
        return exe;
    }

    /// <summary>Swaps the running exe for <paramref name="newExe"/> and starts it. The caller then exits.</summary>
    public static void InstallAndRestart(string newExe, IEnumerable<string> extraArgs)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 위치를 알 수 없습니다.");
        var old = OldPath(current);
        TryDelete(old);
        try
        {
            File.Move(current, old);
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"이 폴더에 쓸 권한이 없어 업데이트할 수 없습니다.\n{Path.GetDirectoryName(current)}");
        }
        try
        {
            File.Copy(newExe, current);
            var psi = new ProcessStartInfo(current) { UseShellExecute = false };
            psi.ArgumentList.Add(AfterUpdateArgument);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            foreach (var a in extraArgs) psi.ArgumentList.Add(a);
            Process.Start(psi);
            Log.Info($"업데이트 설치: {current} (이전 파일 → {Path.GetFileName(old)})");
        }
        catch
        {
            // Put the working version back.
            TryDelete(current);
            File.Move(old, current);
            throw;
        }
    }

    /// <summary>Started by the updater: wait until the previous process has exited (it holds the single-instance lock).</summary>
    public static void WaitForPreviousInstance(string[] args)
    {
        int i = Array.IndexOf(args, AfterUpdateArgument);
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(15000); }
        catch (ArgumentException) { /* already gone */ }
    }

    /// <summary>Removes the previous exe left by an update (call once the new version is up and running).</summary>
    public static void CleanupOld()
    {
        var current = Environment.ProcessPath;
        if (current == null) return;
        var old = OldPath(current);
        if (File.Exists(old) && TryDelete(old)) Log.Info($"이전 버전 파일 정리: {Path.GetFileName(old)}");
    }

    private static string OldPath(string exe) =>
        Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + ".old.exe");

    private static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch { return false; }
    }
}
