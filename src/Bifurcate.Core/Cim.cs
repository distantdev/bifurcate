using Microsoft.Management.Infrastructure;

namespace Bifurcate.Core;

/// <summary>
/// Thin helpers over MI. The VPN cmdlets are wrappers around these same CIM methods, so calling
/// them directly gives identical behavior without hosting PowerShell.
/// </summary>
internal static class Cim
{
    public const string VpnNamespace = @"root\Microsoft\Windows\RemoteAccess\Client";
    public const string StandardNamespace = @"root\StandardCimv2";

    public static CimSession CreateSession() => CimSession.Create(null);

    public static CimMethodResult InvokeStatic(
        CimSession session,
        string cimNamespace,
        string className,
        string methodName,
        params CimMethodParameter[] parameters)
    {
        CimMethodParametersCollection collection = [];
        foreach (CimMethodParameter parameter in parameters) { collection.Add(parameter); }
        return session.InvokeMethod(cimNamespace, className, methodName, collection);
    }

    /// <summary>The PS_* cmdlet classes return their results in a "cmdletOutput" out parameter.</summary>
    public static List<CimInstance> CmdletOutput(CimMethodResult result) =>
        result.OutParameters["cmdletOutput"]?.Value switch
        {
            CimInstance[] many => [.. many],
            CimInstance one => [one],
            _ => [],
        };

    public static string StringOf(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value?.ToString() ?? "";

    public static bool BoolOf(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value is bool value && value;

    public static uint UIntOf(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value is uint value ? value : 0;

    public static CimMethodParameter In(string name, string value) =>
        CimMethodParameter.Create(name, value, CimType.String, CimFlags.In);

    public static CimMethodParameter In(string name, bool value) =>
        CimMethodParameter.Create(name, value, CimType.Boolean, CimFlags.In);

    public static CimMethodParameter In(string name, string[] value) =>
        CimMethodParameter.Create(name, value, CimType.StringArray, CimFlags.In);
}
