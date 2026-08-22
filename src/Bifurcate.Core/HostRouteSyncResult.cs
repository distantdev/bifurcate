namespace Bifurcate.Core;

public sealed record HostRouteSyncResult(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Unresolved)
{
    public static readonly HostRouteSyncResult None = new([], [], []);
}
