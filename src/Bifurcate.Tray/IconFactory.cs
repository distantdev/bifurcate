using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Bifurcate.Core;
using Drawing = System.Drawing;

namespace Bifurcate.Tray;

/// <summary>
/// Draws the icon at runtime, in the colour of the current worst status, so the tray icon carries
/// the status at a glance. The executable's own icon is the fixed Bifurcate.ico instead, since
/// Explorer and the taskbar read that one and a status colour would be meaningless there.
/// </summary>
internal static class IconFactory
{
    // Keyed by theme as well, because the same severity is a different colour on a dark taskbar.
    private static readonly Dictionary<(StatusSeverity Severity, bool OnDark), Drawing.Icon> Cache = [];

    public static Drawing.Icon Tray(StatusSeverity severity)
    {
        (StatusSeverity severity, bool onDark) key = (severity, ThemeManager.TrayIsDark);
        if (Cache.TryGetValue(key, out Drawing.Icon? cached)) { return cached; }

        Drawing.Icon icon = Create(SeverityPalette.TrayColor(severity));
        Cache[key] = icon;
        return icon;
    }

    public static BitmapSource WindowImage()
    {
        using Drawing.Icon icon = Create(SeverityPalette.GdiColor(StatusSeverity.Good, ThemeManager.IsDark));
        return Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }

    /// <summary>A trunk splitting into two branches: traffic taking one path or the other.</summary>
    private static Drawing.Icon Create(Drawing.Color color)
    {
        using Drawing.Bitmap bitmap = new(32, 32);
        using Drawing.Graphics graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using Drawing.Pen pen = new(color, 4.5f)
        {
            StartCap = Drawing.Drawing2D.LineCap.Round,
            EndCap = Drawing.Drawing2D.LineCap.Round,
        };

        graphics.DrawLine(pen, 16, 29, 16, 17);
        graphics.DrawLine(pen, 16, 17, 5, 5);
        graphics.DrawLine(pen, 16, 17, 27, 5);

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // Clone, because the icon returned by FromHandle does not own the handle.
            using Drawing.Icon borrowed = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
