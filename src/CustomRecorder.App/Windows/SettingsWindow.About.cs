using System.Diagnostics;
using System.IO;
using System.Windows;
using CustomRecorder.App.Services;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Windows;

/// <summary>정보 · 업데이트 page: version, manual update check, update settings.</summary>
public partial class SettingsWindow
{
    /// <summary>Set by the owner: returns why an update cannot be installed right now (recording…), or null.</summary>
    public Func<string?>? UpdateBlocker { get; init; }

    /// <summary>A release the owner already found (toolbar button), shown right away.</summary>
    public ReleaseInfo? KnownRelease { get; init; }

    /// <summary>After the dialog closes: the downloaded exe to install (the owner restarts into it).</summary>
    public string? PendingUpdateExe { get; private set; }

    private ReleaseInfo? _release;
    private CancellationTokenSource? _updateCts;

    private void InitAbout()
    {
        VersionText.Text = UpdateService.CurrentText;
        AutoUpdateCheck.IsChecked = Settings.AutoCheckUpdates;
        PrereleaseCheck.IsChecked = Settings.UpdatePrereleases;
        Loaded += (_, _) => { if (KnownRelease != null) ShowRelease(KnownRelease); };
        Closed += (_, _) => _updateCts?.Cancel();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateRow.Description = "확인 중…";
        try
        {
            var release = await UpdateService.CheckAsync(PrereleaseCheck.IsChecked == true);
            if (release == null)
            {
                UpdateRow.Description = $"최신 버전입니다 ({UpdateService.CurrentText}).";
                InstallUpdateButton.Visibility = Visibility.Collapsed;
                ReleaseNotesBox.Visibility = Visibility.Collapsed;
            }
            else ShowRelease(release);
        }
        catch (Exception ex)
        {
            Log.Warn($"업데이트 확인 실패: {ex.Message}");
            UpdateRow.Description = $"확인하지 못했습니다: {Friendly(ex)}";
        }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    private void ShowRelease(ReleaseInfo release)
    {
        _release = release;
        UpdateRow.Description = $"새 버전 {release.Tag}{(release.Prerelease ? " (미리보기)" : "")}이 있습니다. 받는 크기 약 {release.ZipSize / 1048576.0:F0} MB.";
        InstallUpdateButton.Content = $"{release.Tag}로 업데이트";
        InstallUpdateButton.Visibility = Visibility.Visible;
        ReleaseTitle.Text = release.Name;
        ReleaseNotes.Text = string.IsNullOrWhiteSpace(release.Notes) ? "변경 내역이 없습니다." : release.Notes.Trim();
        ReleaseNotesBox.Visibility = Visibility.Visible;
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_release == null) return;
        if (UpdateBlocker?.Invoke() is string why) { UpdateRow.Description = why; return; }

        InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = false;
        _updateCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p => UpdateRow.Description = $"받는 중… {p:P0}");
            var exe = await UpdateService.DownloadAsync(_release, progress, _updateCts.Token);
            UpdateRow.Description = "설치 준비 완료. 프로그램을 다시 시작합니다…";

            // Changes made in this dialog are kept: save as if 저장 was pressed, then the owner restarts.
            PendingUpdateExe = exe;
            Save_Click(this, new RoutedEventArgs());
            if (DialogResult != true)
            {
                PendingUpdateExe = null;
                UpdateRow.Description = "설정에 문제가 있어 저장하지 못했습니다. 단축키 페이지를 확인한 뒤 다시 누르세요.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("업데이트 실패", ex);
            UpdateRow.Description = $"업데이트하지 못했습니다: {Friendly(ex)}";
        }
        finally
        {
            if (IsLoaded) InstallUpdateButton.IsEnabled = CheckUpdateButton.IsEnabled = true;
        }
    }

    private static string Friendly(Exception ex) => ex switch
    {
        System.Net.Http.HttpRequestException => "인터넷 연결을 확인하세요.",
        TaskCanceledException => "응답이 없습니다. 잠시 후 다시 시도하세요.",
        _ => ex.Message,
    };

    private void OpenReleases_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateService.ReleasesUrl);
    private void OpenRepo_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateService.RepoUrl);

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(Log.FilePath)!;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
