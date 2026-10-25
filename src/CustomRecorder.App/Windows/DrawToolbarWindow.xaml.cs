using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CustomRecorder.App.Services;
using CustomRecorder.Core.Capture;
using CustomRecorder.Core.Interop;

namespace CustomRecorder.App.Windows;

/// <summary>
/// "화면에 그리기": a transparent layer over one monitor that you draw on with the mouse (it shows up in screen and
/// region recordings), plus this floating toolbar (never recorded).
/// </summary>
public partial class DrawToolbarWindow : Window
{
    private readonly DrawLayerWindow _layer;
    private Color _color = (Color)ColorConverter.ConvertFromString("#FF4D5E");
    private double _size = 5;

    /// <summary>Raised after each stroke, so other toolbars can put themselves back above the drawing layer.</summary>
    public event Action? LayerTouched;

    public DrawToolbarWindow(MonitorInfo monitor)
    {
        InitializeComponent();
        WindowTheme.Apply(this, 0, smallCorners: true);
        _layer = new DrawLayerWindow(monitor);
        _layer.KeyCommand += OnKey;
        _layer.PreviewMouseUp += (_, _) => { RaiseAbove(); LayerTouched?.Invoke(); };
        PreviewKeyDown += (_, e) => { if (OnKey(e.Key)) e.Handled = true; };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            CaptureExclusion.Apply(hwnd);
        };
        Loaded += (_, _) =>
        {
            // Top centre of the monitor, in physical pixels.
            var hwnd = new WindowInteropHelper(this).Handle;
            double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int w = (int)(ActualWidth * s);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, monitor.Bounds.X + (monitor.Bounds.Width - w) / 2,
                monitor.Bounds.Y + (int)(16 * s), 0, 0, 0x0001 /* SWP_NOSIZE */);
        };
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not ButtonBase) DragMove(); };
        Closed += (_, _) => _layer.Close();
        _layer.Show();
        Apply();
    }

    private void RaiseAbove()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | NativeMethods.SWP_NOACTIVATE);
    }

    private bool OnKey(Key key)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (key)
        {
            case Key.Escape: Close(); return true;
            case Key.Z when ctrl: _layer.Undo(); return true;
            case Key.Delete: _layer.Clear(); return true;
            case Key.D1: PenTool.IsChecked = true; return true;
            case Key.D2: MarkerTool.IsChecked = true; return true;
            case Key.D3: EraserTool.IsChecked = true; return true;
            case Key.D4: PointerTool.IsChecked = true; return true;
        }
        return false;
    }

    private void Apply()
    {
        if (_layer == null) return;
        if (PointerTool.IsChecked == true) _layer.SetMode(DrawLayerWindow.Mode.PassThrough, _color, _size);
        else if (EraserTool.IsChecked == true) _layer.SetMode(DrawLayerWindow.Mode.Erase, _color, _size);
        else if (MarkerTool.IsChecked == true) _layer.SetMode(DrawLayerWindow.Mode.Marker, _color, _size);
        else _layer.SetMode(DrawLayerWindow.Mode.Pen, _color, _size);
    }

    private void Tool_Checked(object sender, RoutedEventArgs e) => Apply();

    private void Color_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Background: SolidColorBrush b }) _color = b.Color;
        Apply();
    }

    private void Size_Checked(object sender, RoutedEventArgs e)
    {
        _size = sender == ThinSize ? 3 : sender == ThickSize ? 9 : 5;
        Apply();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => _layer.Undo();
    private void Clear_Click(object sender, RoutedEventArgs e) => _layer.Clear();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>The transparent drawing surface itself (recorded together with the screen).</summary>
public sealed class DrawLayerWindow : Window
{
    public enum Mode { Pen, Marker, Erase, PassThrough }

    private readonly InkCanvas _ink = new() { Background = Brushes.Transparent, UseCustomCursor = true, Cursor = Cursors.Pen };
    private readonly MonitorInfo _monitor;
    private IntPtr _hwnd;

    public event Func<Key, bool>? KeyCommand;

    public DrawLayerWindow(MonitorInfo monitor)
    {
        _monitor = monitor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // almost invisible, but catches the mouse
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Content = _ink;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE)); // drawing never raises it above our toolbars
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, monitor.Bounds.X, monitor.Bounds.Y,
                monitor.Bounds.Width, monitor.Bounds.Height, 0);
        };
        PreviewKeyDown += (_, e) => { if (KeyCommand?.Invoke(e.Key) == true) e.Handled = true; };
    }

    public void SetMode(Mode mode, Color color, double size)
    {
        bool pass = mode == Mode.PassThrough;
        if (_hwnd != IntPtr.Zero)
        {
            var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex = pass ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
        }
        if (pass) return;

        _ink.EditingMode = mode == Mode.Erase ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.Ink;
        _ink.Cursor = mode == Mode.Erase ? Cursors.Cross : Cursors.Pen;
        bool marker = mode == Mode.Marker;
        _ink.DefaultDrawingAttributes = new System.Windows.Ink.DrawingAttributes
        {
            Color = color,
            Width = marker ? size * 4 : size,
            Height = marker ? size * 4 : size,
            IsHighlighter = marker,
            StylusTip = marker ? System.Windows.Ink.StylusTip.Rectangle : System.Windows.Ink.StylusTip.Ellipse,
            FitToCurve = true,
        };
    }

    public void Undo()
    {
        if (_ink.Strokes.Count > 0) _ink.Strokes.RemoveAt(_ink.Strokes.Count - 1);
    }

    public void Clear() => _ink.Strokes.Clear();
}
