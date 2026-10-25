using System.Windows;
using System.Windows.Threading;
using CustomRecorder.App.Services;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App.Windows;

/// <summary>Update check at startup / every few hours, the toolbar "업데이트" button, and restarting into a new version.</summary>
public partial class MainWindow
{
    private ReleaseInfo? _availableUpdate;
    private string? _openSettingsPage;
    private readonly DispatcherTimer _updateTimer = new();

    private void InitUpdates()
    {
        // Give startup (recovery, capture devices) a moment first.
        _updateTimer.Interval = TimeSpan.FromSeconds(8);
        _updateTimer.Tick += async (_, _) =>
        {
            if (_updateTimer.Interval < TimeSpan.FromHours(1))
            {
                UpdateService.CleanupOld(); // the new version is running: the previous exe can go
                _updateTimer.Interval = TimeSpan.FromHours(6); // long sessions (tray) still hear about new versions
            }
            if (_settings.AutoCheckUpdates) await CheckUpdatesQuietlyAsync();
        };
        _updateTimer.Start();
    }

    private async Task CheckUpdatesQuietlyAsync()
    {
        try
        {
            _availableUpdate = await UpdateService.CheckAsync(_settings.UpdatePrereleases);
        }
        catch (Exception ex)
        {
            Log.Warn($"업데이트 자동 확인 실패: {ex.Message}"); // offline etc. — not worth bothering the user
            return;
        }
        UpdatePill.Visibility = _availableUpdate != null ? Visibility.Visible : Visibility.Collapsed;
        if (_availableUpdate != null && UpdatePill.Template.FindName("UpdatePillText", UpdatePill) is System.Windows.Controls.TextBlock text)
            text.Text = $"업데이트 {_availableUpdate.Tag}";
    }

    private void UpdatePill_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateBlocker is string why) { StatusText.Text = why; return; }
        _openSettingsPage = "PageAbout";
        Settings_Click(this, e);
    }

    /// <summary>Why an update cannot be installed right now, or null.</summary>
    private string? UpdateBlocker =>
        _recorder != null ? "녹화 중에는 업데이트할 수 없습니다. 녹화를 끝낸 뒤 다시 누르세요."
        : _replay != null ? "리플레이가 켜져 있습니다. 리플레이를 끈 뒤 다시 누르세요."
        : _tidying > 0 ? "녹화 파일을 마무리하는 중입니다. 잠시 후 다시 누르세요."
        : null;

    private void InstallUpdate(string newExe)
    {
        try
        {
            var args = CaptureExclusion.Enabled ? Array.Empty<string>() : new[] { "--visible" };
            UpdateService.InstallAndRestart(newExe, args);
        }
        catch (Exception ex)
        {
            Log.Error("업데이트 설치 실패", ex);
            MessageBox.Show(this, $"업데이트를 설치하지 못했습니다. 지금 버전은 그대로입니다.\n\n{ex.Message}", "CustomRecorder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ExitApp(); // the new version waits for this process to exit
    }
}
