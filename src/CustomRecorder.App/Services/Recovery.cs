using System.IO;
using CustomRecorder.Core.Encoding;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Services;

/// <summary>Start-up housekeeping: leftovers from a crash or a forced shutdown.</summary>
public static class Recovery
{
    /// <summary>Recordings that were never tidied (the app or PC stopped while recording or right after).</summary>
    public static List<string> FindUnfinished(string folder)
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(folder)) return result;
            var since = DateTime.Now.AddDays(-30);
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*.mp4"))
            {
                if (f.LastWriteTime < since || f.Length < 1024) continue;
                try { if (Fmp4Defragmenter.IsFragmented(f.FullName)) result.Add(f.FullName); }
                catch (Exception ex) { Log.Warn($"복구 검사 실패: {f.Name}: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Warn($"복구 검사 실패: {ex.Message}"); }
        return result;
    }

    /// <summary>Deletes temporary files we created that a crash left behind. Safe at start-up (single instance).</summary>
    public static void CleanTemp(string folder)
    {
        int removed = 0;
        void TryDelete(Action delete) { try { delete(); removed++; } catch { /* in use or gone */ } }

        if (Directory.Exists(folder))
            foreach (var f in Directory.EnumerateFiles(folder, "~*.remux.tmp")) TryDelete(() => File.Delete(f));

        var temp = Path.GetTempPath();
        var replay = Path.Combine(temp, "CustomRecorder", "replay");
        if (Directory.Exists(replay))
            foreach (var d in Directory.EnumerateDirectories(replay)) TryDelete(() => Directory.Delete(d, true));
        foreach (var f in Directory.EnumerateFiles(temp, "CustomRecorder_*.mp4")) TryDelete(() => File.Delete(f));

        if (removed > 0) Log.Info($"임시 파일 정리: {removed}개");
    }
}
