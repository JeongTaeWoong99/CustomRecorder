namespace CustomRecorder.Core.Util;

/// <summary>Minimal file logger: %LocalAppData%\CustomRecorder\logs\app.log</summary>
public static class Log
{
    private const long MaxBytes = 5L << 20;   // 5 MB per file
    private const int KeepFiles = 3;          // app.log, app.1.log, app.2.log

    private static readonly object Gate = new();
    private static string? _path;
    private static int _writes;

    public static string FilePath
    {
        get
        {
            if (_path != null) return _path;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CustomRecorder", "logs");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "app.log");
            return _path;
        }
    }

    /// <summary>Overrides the log folder (development builds log next to the executable).</summary>
    public static void UseFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "app.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}\n{ex}");

    /// <summary>Keeps the log from growing forever: app.log → app.1.log → app.2.log, oldest dropped.</summary>
    private static void RotateIfLarge()
    {
        var path = FilePath;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes) return;
        string Numbered(int i) => Path.Combine(info.DirectoryName!, $"app.{i}.log");
        File.Delete(Numbered(KeepFiles - 1));
        for (int i = KeepFiles - 2; i >= 1; i--)
            if (File.Exists(Numbered(i))) File.Move(Numbered(i), Numbered(i + 1));
        File.Move(path, Numbered(1));
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (_writes++ % 200 == 0) RotateIfLarge();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never crash the recorder
        }
    }
}
