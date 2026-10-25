using System.IO;
using Microsoft.Win32;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Services;

/// <summary>"Start with Windows": a value under HKCU\...\Run that launches the app hidden in the tray.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "CustomRecorder";
    public const string TrayArgument = "--tray";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(Name) is string;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// If "start with Windows" points at an exe that no longer exists (the folder was moved), point it at this exe.
    /// A registered exe that still exists is left alone.
    /// </summary>
    public static void Repair()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(Name) is not string value) return;
            string registered = value.StartsWith('"') ? value[1..Math.Max(1, value.IndexOf('"', 1))] : value.Split(' ')[0];
            if (File.Exists(registered) || Environment.ProcessPath == null) return;
            key.SetValue(Name, $"\"{Environment.ProcessPath}\" {TrayArgument}");
            Log.Info($"자동 실행 경로 복구: {registered} → {Environment.ProcessPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"자동 실행 경로 확인 실패: {ex.Message}");
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\" {TrayArgument}");
            else key.DeleteValue(Name, throwOnMissingValue: false);
            Log.Info($"Windows 시작 시 실행: {enabled}");
        }
        catch (Exception ex)
        {
            Log.Error("자동 실행 설정 실패", ex);
        }
    }
}
