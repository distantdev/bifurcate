using Microsoft.Management.Infrastructure;

namespace Bifurcate.Core;

/// <summary>
/// Reads the Windows service state. Uses CIM rather than ServiceController to avoid another
/// dependency, and reading is allowed for standard users, which keeps the tray unelevated.
/// </summary>
public static class ServiceStateReader
{
    public static ServiceState Read(string serviceName = BifurcateInfo.ServiceName)
    {
        try
        {
            using CimSession session = Cim.CreateSession();
            string escaped = serviceName.Replace("'", "''");
            CimInstance? service = session
                .QueryInstances(@"root\cimv2", "WQL",
                    $"SELECT Name, State FROM Win32_Service WHERE Name = '{escaped}'")
                .FirstOrDefault();

            if (service is null) { return ServiceState.NotInstalled; }

            return Cim.StringOf(service, "State").Equals("Running", StringComparison.OrdinalIgnoreCase)
                ? ServiceState.Running
                : ServiceState.Stopped;
        }
        catch (CimException)
        {
            return ServiceState.Unknown;
        }
    }
}
