using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CustomRecorder.App.Services;
using CustomRecorder.App.Windows;
using CustomRecorder.Core.Interop;
using CustomRecorder.Core.Util;

namespace CustomRecorder.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private const string ShowSignalName = "CustomRecorder.ShowWindow";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("처리되지 않은 예외", args.ExceptionObject as Exception);

        // Started by the updater: the previous version is still closing and holds the single-instance lock.
        UpdateService.WaitForPreviousInstance(e.Args);

        // One instance only: global hotkeys can belong to a single process.
        _singleInstance = new Mutex(true, "CustomRecorder.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        CaptureExclusion.Enabled = !e.Args.Contains("--visible");
        if (!CaptureExclusion.Enabled) Log.UseFolder(System.IO.Path.Combine(AppContext.BaseDirectory, "logs")); // dev/test mode
        Log.Info($"===== CustomRecorder {UpdateService.CurrentText} 시작 (OS {Environment.OSVersion.Version}, exclude={CaptureExclusion.Enabled}) =====");
        Autostart.Repair();

        var main = new MainWindow();
        MainWindow = main;
        if (e.Args.Contains(Autostart.TrayArgument))
        {
            // Started with Windows: stay in the tray (hotkeys and tray menu work; the window opens from the icon).
            new System.Windows.Interop.WindowInteropHelper(main).EnsureHandle();
            Log.Info("트레이로 시작");
        }
        else main.Show();

        // A second launch asks this instance to come forward (works even while hidden in the tray).
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => main.ShowFromTray());
        }) { IsBackground = true, Name = "ShowSignal" }.Start();
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
            AllowSetForegroundWindow(-1); // we were just launched by the user, so we may hand focus to the running copy
            signal.Set();
            return;
        }
        catch (WaitHandleCannotBeOpenedException) { /* older instance: fall back to window activation */ }

        var self = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(self.ProcessName))
        {
            if (p.Id == self.Id || p.MainWindowHandle == IntPtr.Zero) continue;
            NativeMethods.ShowWindow(p.MainWindowHandle, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(p.MainWindowHandle);
            break;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI 예외", e.Exception);
        MessageBox.Show($"오류가 발생했습니다.\n\n{e.Exception.Message}\n\n자세한 내용: {Log.FilePath}", "CustomRecorder",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
