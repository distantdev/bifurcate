using System.Windows.Media;
using Bifurcate.Core;
using Drawing = System.Drawing;

namespace Bifurcate.Tray;

/// <summary>
/// One definition of the status colors, shared by the tray icon and the window. Each severity has a
/// pair: the light one is dark enough to read on white, the dark one bright enough to read on
/// near-black. Using either on the wrong background is legible but muddy.
/// </summary>
internal static class SeverityPalette
{
    private static readonly Dictionary<StatusSeverity, string> Light = new()
    {
        [StatusSeverity.Good] = "#1B7F3B",
        [StatusSeverity.Warning] = "#C06000",
        [StatusSeverity.Bad] = "#B3261E",
        [StatusSeverity.Neutral] = "#5A5A5A",
    };

    private static readonly Dictionary<StatusSeverity, string> Dark = new()
    {
        [StatusSeverity.Good] = "#4CC38A",
        [StatusSeverity.Warning] = "#E9A23B",
        [StatusSeverity.Bad] = "#F2555A",
        [StatusSeverity.Neutral] = "#A6A6A6",
    };

    private static readonly Dictionary<StatusSeverity, Brush> LightBrushes = Freeze(Light);
    private static readonly Dictionary<StatusSeverity, Brush> DarkBrushes = Freeze(Dark);

    public static Brush Brush(StatusSeverity severity) =>
        ThemeManager.IsDark ? DarkBrushes[severity] : LightBrushes[severity];

    /// <summary>
    /// For the tray icon, which sits on the taskbar and so follows the taskbar's theme rather than
    /// whichever theme the windows are using.
    /// </summary>
    public static Drawing.Color TrayColor(StatusSeverity severity) =>
        GdiColor(severity, ThemeManager.TrayIsDark);

    public static Drawing.Color GdiColor(StatusSeverity severity, bool onDark) =>
        Drawing.ColorTranslator.FromHtml(onDark ? Dark[severity] : Light[severity]);

    /// <summary>The tint behind the routing mode button that is currently saved.</summary>
    public static Brush ActiveChoice { get; } = Frozen("#D8EAD8");

    public static Brush ActiveChoiceDark { get; } = Frozen("#24402F");

    /// <summary>The single colour that represents the whole dashboard.</summary>
    public static StatusSeverity Worst(IEnumerable<StatusLine> lines) =>
        lines.Select(line => line.Severity).DefaultIfEmpty(StatusSeverity.Neutral).Max();

    private static Dictionary<StatusSeverity, Brush> Freeze(Dictionary<StatusSeverity, string> colors) =>
        colors.ToDictionary(pair => pair.Key, pair => Frozen(pair.Value));

    private static Brush Frozen(string hex)
    {
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
