using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CustomRecorder.App.Services;

/// <summary>Explorer thumbnails and recycle-bin deletion.</summary>
public static class ShellFiles
{
    /// <summary>The same thumbnail Explorer shows (video frame, image preview or file-type icon).</summary>
    public static ImageSource? Thumbnail(string path, int width, int height)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0) return null;
            try
            {
                if (factory.GetImage(new SIZE { cx = width, cy = height }, 0x1 /* SIIGBF_BIGGERSIZEOK */, out var hbmp) != 0) return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { DeleteObject(hbmp); }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch { return null; }
    }

    /// <summary>Moves a file to the recycle bin (can be restored).</summary>
    public static bool Recycle(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 0x0003, // FO_DELETE
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = 0x0040 | 0x0010 | 0x0400 | 0x0004, // ALLOWUNDO | NOCONFIRMATION | NOERRORUI | SILENT
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, out IShellItemImageFactory factory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
