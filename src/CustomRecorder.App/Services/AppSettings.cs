using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CustomRecorder.Core.Effects;
using CustomRecorder.Core.Encoding;
using CustomRecorder.Core.Recording;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Services;

public enum AfterRecordAction { None, OpenFolder, ShowFile }

/// <summary>User settings, stored as JSON in %AppData%\CustomRecorder\settings.json.</summary>
public sealed class AppSettings
{
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "CustomRecorder");

    public int Fps { get; set; } = 60;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public QualityPreset Quality { get; set; } = QualityPreset.Balanced;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VideoCodec Codec { get; set; } = VideoCodec.H264;

    /// <summary>0 = automatic (from quality preset).</summary>
    public int BitrateMbps { get; set; }

    public int KeyframeSeconds { get; set; } = 2;

    /// <summary>0 = original, -1 = half, otherwise target height.</summary>
    public int OutputHeight { get; set; }

    public bool CaptureCursor { get; set; } = true;

    // ---- 효과 ----
    public bool CursorHighlight { get; set; }
    public bool ClickEffect { get; set; }
    public bool WatermarkEnabled { get; set; }
    public string WatermarkText { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WatermarkPosition WatermarkPosition { get; set; } = WatermarkPosition.BottomRight;

    public float WatermarkOpacity { get; set; } = 0.6f;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public KeyDisplayMode KeyDisplay { get; set; } = KeyDisplayMode.Off;

    public bool WebcamEnabled { get; set; }
    /// <summary>Camera symbolic link; empty = first camera.</summary>
    public string WebcamId { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WatermarkPosition WebcamPosition { get; set; } = WatermarkPosition.BottomRight;

    public float WebcamSize { get; set; } = 0.25f;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WebcamShape WebcamShape { get; set; } = WebcamShape.Circle;

    public OverlayOptions ToOverlay() => new(CursorHighlight, ClickEffect,
        WatermarkEnabled && !string.IsNullOrWhiteSpace(WatermarkText) ? WatermarkText : null, WatermarkPosition, WatermarkOpacity)
    {
        KeyDisplay = KeyDisplay,
        WebcamId = WebcamEnabled ? WebcamId : null,
        WebcamPosition = WebcamPosition,
        WebcamSize = WebcamSize,
        WebcamShape = WebcamShape,
    };
    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordMicrophone { get; set; }
    public string? MicrophoneId { get; set; }
    public float SystemVolume { get; set; } = 1f;
    public float MicrophoneVolume { get; set; } = 1f;

    /// <summary>"png" or "jpg".</summary>
    public string ScreenshotFormat { get; set; } = "png";

    public bool CopyScreenshotToClipboard { get; set; } = true;

    /// <summary>Replay buffer length in seconds.</summary>
    public int ReplaySeconds { get; set; } = 60;

    // ---- 일반 ----
    /// <summary>Close button hides to the notification area instead of quitting.</summary>
    public bool MinimizeToTray { get; set; } = true;
    /// <summary>Keep the toolbar above other windows.</summary>
    public bool AlwaysOnTop { get; set; } = true;
    /// <summary>Hide the toolbar while recording (the tray icon shows the recording state).</summary>
    public bool HideWhileRecording { get; set; }
    /// <summary>Separate one-click record / capture / replay icons in the notification area.</summary>
    public bool QuickButtons { get; set; } = true;
    /// <summary>"taskbar" = buttons on the taskbar left of the tray; "tray" = notification-area icons.</summary>
    public string QuickButtonsPlace { get; set; } = "taskbar";
    /// <summary>Check GitHub for a newer version when the program starts.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>Also offer pre-release (beta) versions.</summary>
    public bool UpdatePrereleases { get; set; }
    public int CountdownSeconds { get; set; }
    /// <summary>0 = off.</summary>
    public int AutoStopMinutes { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AfterRecordAction AfterRecording { get; set; } = AfterRecordAction.None;

    // ---- 소리 ----
    public bool SeparateAudioTracks { get; set; }
    public bool MicNoiseGate { get; set; }
    /// <summary>true = threshold from the room's background level; false = <see cref="MicNoiseGateThresholdDb"/>.</summary>
    public bool MicNoiseGateAuto { get; set; } = true;
    public float MicNoiseGateThresholdDb { get; set; } = -46f;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AudioFileFormat SoundOnlyFormat { get; set; } = AudioFileFormat.M4a;

    // ---- 단축키 ----
    public string HotkeyRecord { get; set; } = "F2";
    public string HotkeyPause { get; set; } = "Shift+F2";
    public string HotkeyScreenshot { get; set; } = "F3";
    public string HotkeySaveReplay { get; set; } = "F4";
    public string HotkeyDraw { get; set; } = "Ctrl+Shift+D";

    // ---- 파일 이름 ----
    public const string DefaultFileNamePattern = CustomRecorder.Core.Util.FileNaming.DefaultPattern;
    public string FileNamePattern { get; set; } = DefaultFileNamePattern;

    // ---- persistence ---------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CustomRecorder", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("설정 파일을 읽지 못해 기본값을 사용합니다", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("설정 저장 실패", ex);
        }
    }

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;

    public string NewRecordingPath(string target = "") => NewPath("녹화", "mp4", target);

    public string NewReplayPath(string target = "") => NewPath("리플레이", "mp4", target);

    public string NewSoundPath() => NewPath("녹음", AudioFileWriter.Extension(SoundOnlyFormat), "소리");

    public static string ReplayTempFolder() =>
        Path.Combine(Path.GetTempPath(), "CustomRecorder", "replay", Guid.NewGuid().ToString("N")[..8]);

    public string NewScreenshotPath(string target = "") => NewPath("캡처", ScreenshotFormat == "jpg" ? "jpg" : "png", target);

    /// <summary>Builds a file path from <see cref="FileNamePattern"/>; adds _2, _3… if the name is taken.</summary>
    public string NewPath(string kind, string extension, string target) =>
        UniquePath(Path.Combine(OutputFolder, FormatFileName(FileNamePattern, kind, target, DateTime.Now) + "." + extension));

    public static string FormatFileName(string pattern, string kind, string target, DateTime time) =>
        CustomRecorder.Core.Util.FileNaming.Format(pattern, kind, target, time);

    private static string UniquePath(string path) => CustomRecorder.Core.Util.FileNaming.Unique(path);
}
