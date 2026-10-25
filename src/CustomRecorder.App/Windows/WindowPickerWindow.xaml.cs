using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CustomRecorder.App.Services;
using CustomRecorder.Core.Capture;
using CustomRecorder.Core.Interop;

namespace CustomRecorder.App.Windows;

/// <summary>
/// Window picker with live previews, like Alt+Tab: each card shows a DWM thumbnail of the real window
/// (the compositor draws it straight into our window, so it stays live and costs almost nothing).
/// </summary>
public partial class WindowPickerWindow : Window
{
    private const double CardWidth = 228, ThumbHeight = 128;

    private sealed class Card
    {
        public required WindowInfo Info { get; init; }
        public required Border Root { get; init; }
        public required Border Host { get; init; }   // thumbnail area
        public IntPtr Thumb;
        public bool Minimized;
    }

    private readonly List<Card> _cards = new();
    private Card? _selected;
    private IntPtr _hwnd;

    public WindowInfo? Selected { get; private set; }

    public WindowPickerWindow()
    {
        InitializeComponent();
        WindowTheme.Apply(this, 76);
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            CaptureExclusion.Apply(_hwnd);
        };
        Loaded += (_, _) => { Refresh(); SearchBox.Focus(); };
        Closed += (_, _) => ClearThumbs();
        KeyDown += (_, e) => { if (e.Key == Key.Enter && _selected != null) Ok_Click(this, e); };
    }

    // ---------------------------------------------------------------- cards

    private void Refresh()
    {
        ClearThumbs();
        _cards.Clear();
        Cards.Children.Clear();
        _selected = null;

        // Windows that can be recorded right away first; minimized ones last.
        foreach (var info in WindowInfo.GetCapturable().OrderBy(w => NativeMethods.IsIconic(w.Handle)))
        {
            var card = MakeCard(info);
            _cards.Add(card);
            Cards.Children.Add(card.Root);
        }
        EmptyText.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
        if (_cards.Count > 0) Select(_cards[0]);
        Dispatcher.BeginInvoke(UpdateThumbs, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private Card MakeCard(WindowInfo info)
    {
        bool minimized = NativeMethods.IsIconic(info.Handle);
        var host = new Border
        {
            Height = ThumbHeight,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("Panel"),
            Margin = new Thickness(8, 8, 8, 0),
        };
        var icon = WindowIcon(info.Handle);
        if (minimized)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            if (icon != null) stack.Children.Add(new Image { Source = icon, Width = 32, Height = 32, Margin = new Thickness(0, 0, 0, 8) });
            stack.Children.Add(new TextBlock { Text = "최소화된 창", Foreground = (Brush)FindResource("TextDim"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = "녹화하려면 창을 다시 띄워 주세요", Foreground = (Brush)FindResource("TextFaint"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0) });
            host.Child = stack;
        }

        var title = new TextBlock { Text = info.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 };
        var proc = new TextBlock { Text = info.ProcessName, Foreground = (Brush)FindResource("TextFaint"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        var footer = new Grid { Margin = new Thickness(10, 8, 10, 10) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        if (icon != null) footer.Children.Add(new Image { Source = icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
        var texts = new StackPanel();
        texts.Children.Add(title);
        texts.Children.Add(proc);
        Grid.SetColumn(texts, 1);
        footer.Children.Add(texts);

        var body = new StackPanel();
        body.Children.Add(host);
        body.Children.Add(footer);

        var root = new Border
        {
            Width = CardWidth,
            Margin = new Thickness(6),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("Surface"),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(2),
            Child = body,
            Cursor = Cursors.Hand,
            ToolTip = $"{info.Title}\n{info.ProcessName}",
        };
        var card = new Card { Info = info, Root = root, Host = host, Minimized = minimized };
        root.MouseEnter += (_, _) => { if (card != _selected) root.BorderBrush = (Brush)FindResource("Border"); };
        root.MouseLeave += (_, _) => { if (card != _selected) root.BorderBrush = Brushes.Transparent; };
        root.MouseLeftButtonDown += (_, e) =>
        {
            Select(card);
            if (e.ClickCount == 2) Ok_Click(this, e);
        };
        return card;
    }

    private void Select(Card card)
    {
        if (_selected != null) _selected.Root.BorderBrush = Brushes.Transparent;
        _selected = card;
        card.Root.BorderBrush = (Brush)FindResource("Accent");
        SelectionText.Text = card.Minimized
            ? $"'{card.Info.Title}' 창은 최소화되어 있어 다시 띄워야 녹화됩니다."
            : $"선택: {card.Info.Title}";
        OkButton.IsEnabled = true;
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in _cards)
        {
            bool show = q.Length == 0
                        || c.Info.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                        || c.Info.ProcessName.Contains(q, StringComparison.CurrentCultureIgnoreCase);
            c.Root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---------------------------------------------------------------- DWM thumbnails

    private void UpdateThumbs()
    {
        if (_hwnd == IntPtr.Zero || !IsLoaded) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        // Visible part of the scroll area, in window coordinates.
        var viewport = Scroller.TransformToAncestor(this).TransformBounds(new Rect(0, 0, Scroller.ActualWidth, Scroller.ActualHeight));

        foreach (var c in _cards)
        {
            if (c.Minimized) continue;
            if (c.Thumb == IntPtr.Zero && DwmRegisterThumbnail(_hwnd, c.Info.Handle, out c.Thumb) != 0) { c.Thumb = IntPtr.Zero; continue; }

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_VISIBLE | DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_SOURCECLIENTAREAONLY | DWM_TNP_OPACITY,
                opacity = 255,
                fSourceClientAreaOnly = 0,
            };
            if (c.Root.Visibility != Visibility.Visible || !c.Host.IsVisible || DwmQueryThumbnailSourceSize(c.Thumb, out var src) != 0 || src.cx <= 0 || src.cy <= 0)
            {
                props.fVisible = 0;
                DwmUpdateThumbnailProperties(c.Thumb, ref props);
                continue;
            }

            // Fit the window image inside the card area, keeping its aspect ratio.
            var host = c.Host.TransformToAncestor(this).TransformBounds(new Rect(0, 0, c.Host.ActualWidth, c.Host.ActualHeight));
            host.Inflate(-6, -6);
            double scale = Math.Min(host.Width / src.cx, host.Height / src.cy);
            double w = src.cx * scale, h = src.cy * scale;
            var dest = new Rect(host.X + (host.Width - w) / 2, host.Y + (host.Height - h) / 2, w, h);

            // Clip to the scroll viewport and cut the same share off the source.
            var visible = Rect.Intersect(dest, viewport);
            if (visible.IsEmpty || visible.Width < 1 || visible.Height < 1)
            {
                props.fVisible = 0;
                DwmUpdateThumbnailProperties(c.Thumb, ref props);
                continue;
            }
            props.fVisible = 1;
            props.rcSource = new RECT
            {
                Left = (int)((visible.X - dest.X) / scale),
                Top = (int)((visible.Y - dest.Y) / scale),
                Right = (int)((visible.Right - dest.X) / scale),
                Bottom = (int)((visible.Bottom - dest.Y) / scale),
            };
            props.rcDestination = new RECT
            {
                Left = (int)Math.Round(visible.X * dpi.DpiScaleX),
                Top = (int)Math.Round(visible.Y * dpi.DpiScaleY),
                Right = (int)Math.Round(visible.Right * dpi.DpiScaleX),
                Bottom = (int)Math.Round(visible.Bottom * dpi.DpiScaleY),
            };
            DwmUpdateThumbnailProperties(c.Thumb, ref props);
        }
    }

    private void ClearThumbs()
    {
        foreach (var c in _cards)
        {
            if (c.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(c.Thumb);
            c.Thumb = IntPtr.Zero;
        }
    }

    private static ImageSource? WindowIcon(IntPtr hwnd)
    {
        IntPtr h = IntPtr.Zero;
        foreach (var type in new[] { 1 /* ICON_BIG */, 2 /* ICON_SMALL2 */, 0 /* ICON_SMALL */ })
        {
            if (SendMessageTimeout(hwnd, 0x007F /* WM_GETICON */, (IntPtr)type, IntPtr.Zero, 0x0002, 60, out h) != IntPtr.Zero && h != IntPtr.Zero) break;
            h = IntPtr.Zero;
        }
        if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, -14 /* GCLP_HICON */);
        if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, -34 /* GCLP_HICONSM */);
        if (h == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- events

    private void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateThumbs();

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
        var first = _cards.FirstOrDefault(c => c.Root.Visibility == Visibility.Visible);
        if (_selected?.Root.Visibility != Visibility.Visible && first != null) Select(first);
        Dispatcher.BeginInvoke(UpdateThumbs, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        Selected = _selected.Info;
        DialogResult = true;
    }

    // ---------------------------------------------------------------- interop

    private const int DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public int fVisible;
        public int fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
}
