using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CustomRecorder.App.Services;

namespace CustomRecorder.App.Windows;

/// <summary>Recent files in the output folder with quick actions: play, trim, show in Explorer, delete.</summary>
public partial class LibraryWindow : Window
{
    private static readonly string[] VideoExt = { ".mp4" };
    private static readonly string[] AudioExt = { ".m4a", ".mp3", ".flac", ".wav" };
    private static readonly string[] ImageExt = { ".png", ".jpg", ".gif" };

    private readonly string _folder;

    public LibraryWindow(string folder)
    {
        _folder = folder;
        InitializeComponent();
        WindowTheme.Apply(this, 76);
        SourceInitialized += (_, _) => CaptureExclusion.Apply(new WindowInteropHelper(this).Handle);
        FolderText.Text = folder;
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        Rows.Children.Clear();
        string[] exts = FilterVideo.IsChecked == true ? VideoExt
                      : FilterAudio.IsChecked == true ? AudioExt
                      : FilterImage.IsChecked == true ? ImageExt
                      : VideoExt.Concat(AudioExt).Concat(ImageExt).ToArray();
        List<FileInfo> files;
        try
        {
            files = Directory.Exists(_folder)
                ? new DirectoryInfo(_folder).EnumerateFiles()
                    .Where(f => exts.Contains(f.Extension.ToLowerInvariant()) && f.Length > 0)
                    .OrderByDescending(f => f.LastWriteTime).Take(200).ToList()
                : new List<FileInfo>();
        }
        catch { files = new List<FileInfo>(); }

        EmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var f in files) Rows.Children.Add(MakeRow(f));
    }

    private FrameworkElement MakeRow(FileInfo file)
    {
        string ext = file.Extension.ToLowerInvariant();
        bool video = VideoExt.Contains(ext), audio = AudioExt.Contains(ext);

        var thumb = new Image { Width = 112, Height = 63, Stretch = Stretch.UniformToFill };
        var thumbBox = new Border
        {
            Width = 112, Height = 63, CornerRadius = new CornerRadius(6), ClipToBounds = true,
            Background = (Brush)FindResource("Panel"), Child = thumb,
        };
        if (audio)
            thumbBox.Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 24,
                Foreground = (Brush)FindResource("TextDim"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        else
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => thumb.Source = ShellFiles.Thumbnail(file.FullName, 224, 126));

        var name = new TextBlock { Text = file.Name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
        var kind = video ? "영상" : audio ? "소리" : "캡처";
        var info = new TextBlock
        {
            Text = $"{kind}  ·  {file.LastWriteTime:yyyy-MM-dd HH:mm}  ·  {FormatSize(file.Length)}",
            Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 3, 0, 0),
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 10, 0) };
        texts.Children.Add(name);
        texts.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(MakeAction("", "재생 / 열기", () => Open(file.FullName)));
        if (video) actions.Children.Add(MakeAction("", "자르기 · GIF 만들기 — 필요한 부분만 새 파일로 저장", () => Trim(file.FullName)));
        actions.Children.Add(MakeAction("", "탐색기에서 보기", () => Show(file.FullName)));
        actions.Children.Add(MakeAction("", "휴지통으로 보내기", () => Delete(file.FullName)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(thumbBox);
        Grid.SetColumn(texts, 1); grid.Children.Add(texts);
        Grid.SetColumn(actions, 2); grid.Children.Add(actions);

        var row = new Border
        {
            Background = (Brush)FindResource("Surface"), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 6), Child = grid,
        };
        row.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Open(file.FullName); };
        return row;
    }

    private Button MakeAction(string glyph, string tip, Action action)
    {
        var b = new Button
        {
            Style = (Style)FindResource("IconButton"), Width = 36, Height = 34, Margin = new Thickness(2, 0, 0, 0), ToolTip = tip,
            Content = new TextBlock { Text = glyph, Style = (Style)FindResource("IconText"), FontSize = 15 },
        };
        b.Click += (_, _) => action();
        return b;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F2} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F1} MB" : $"{bytes / 1024.0:F0} KB";

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"파일을 열 수 없습니다.\n{ex.Message}", "CustomRecorder"); }
    }

    private static void Show(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });

    private void Trim(string path)
    {
        CustomRecorder.Core.Util.Log.Info($"자르기 창 열기: {path}");
        try
        {
            var dlg = new TrimWindow(path) { Owner = this };
            dlg.Loaded += (_, _) => CustomRecorder.Core.Util.Log.Info($"자르기 창 표시: {dlg.Left:F0},{dlg.Top:F0} {dlg.ActualWidth:F0}x{dlg.ActualHeight:F0}");
            dlg.ShowDialog();
            Reload(); // a GIF may have been saved even when the dialog was cancelled afterwards
        }
        catch (Exception ex)
        {
            CustomRecorder.Core.Util.Log.Error("자르기 창 오류", ex);
            MessageBox.Show(this, $"자르기 창을 열 수 없습니다.\n{ex.Message}", "CustomRecorder");
        }
    }

    private void Delete(string path)
    {
        if (MessageBox.Show(this, $"'{Path.GetFileName(path)}' 파일을 휴지통으로 보낼까요?\n휴지통에서 되살릴 수 있습니다.", "CustomRecorder",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        if (!ShellFiles.Recycle(path)) MessageBox.Show(this, "삭제하지 못했습니다. 다른 프로그램에서 열려 있는지 확인하세요.", "CustomRecorder");
        Reload();
    }

    private void Filter_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) Reload(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
