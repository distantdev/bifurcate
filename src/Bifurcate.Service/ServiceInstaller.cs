using System.Diagnostics;
using System.Security.Principal;
using Bifurcate.Core;

namespace Bifurcate.Service;

/// <summary>
/// Registers and removes the Windows service. Lives here rather than in the install script so the
/// service's own registration details stay next to the code that implements it.
/// </summary>
internal static class ServiceInstaller
{
    private const int ServiceDoesNotExist = 1060;

    public static int Install()
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("--install needs an elevated prompt.");
            return 1;
        }

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine this executable's path.");

        if (Exists())
        {
            Console.WriteLine($"Service '{BifurcateInfo.ServiceName}' already exists. Replacing it.");
            Sc("stop", BifurcateInfo.ServiceName);
            Sc("delete", BifurcateInfo.ServiceName);
            if (!WaitUntilGone())
            {
                Console.Error.WriteLine("The old service did not go away. Close Services and try again.");
                return 1;
            }
        }

        // sc.exe wants "key= value" as two tokens. The binPath value keeps its own quotes so the
        // registry entry is a quoted path rather than the classic unquoted service path problem.
        (int code, string output) = Sc(
            "create", BifurcateInfo.ServiceName,
            "binPath=", $"\"{executable}\"",
            "DisplayName=", BifurcateInfo.ServiceDisplayName,
            "start=", "auto",
            "obj=", "LocalSystem");

        if (code != 0)
        {
            Console.Error.WriteLine($"Could not create the service: {output.Trim()}");
            return code;
        }

        Sc("description", BifurcateInfo.ServiceName, BifurcateInfo.ServiceDescription);

        // Restart rather than sit dead if it ever faults.
        Sc("failure", BifurcateInfo.ServiceName,
            "reset=", "86400",
            "actions=", "restart/60000/restart/60000/restart/60000");

        (int startCode, string startOutput) = Sc("start", BifurcateInfo.ServiceName);
        if (startCode != 0)
        {
            Console.Error.WriteLine($"Installed, but could not start: {startOutput.Trim()}");
            return startCode;
        }

        Console.WriteLine($"Installed and started '{BifurcateInfo.ServiceDisplayName}'.");
        return 0;
    }

    public static int Uninstall()
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("--uninstall needs an elevated prompt.");
            return 1;
        }

        if (!Exists())
        {
            Console.WriteLine($"Service '{BifurcateInfo.ServiceName}' is not installed.");
        }
        else
        {
            Sc("stop", BifurcateInfo.ServiceName);
            (int code, string output) = Sc("delete", BifurcateInfo.ServiceName);
            if (code != 0)
            {
                Console.Error.WriteLine($"Could not delete the service: {output.Trim()}");
                return code;
            }

            Console.WriteLine($"Removed '{BifurcateInfo.ServiceName}'.");
        }

        // Leaving inbound block rules behind would outlive the thing that manages them.
        IReadOnlyList<string> rules = new FirewallService().RemoveOwnedRules();
        Console.WriteLine(rules.Count == 0
            ? "No firewall rules to clean up."
            : $"Removed firewall rules: {string.Join(", ", rules)}");

        using DnsBypassService dnsBypass = new();
        IReadOnlyList<string> dnsRules = dnsBypass.RemoveOwnedRules();
        Console.WriteLine(dnsRules.Count == 0
            ? "No DNS bypass rules to clean up."
            : $"Removed DNS bypass rules: {string.Join(", ", dnsRules)}");

        return 0;
    }

    private static bool Exists() => Sc("query", BifurcateInfo.ServiceName).ExitCode != ServiceDoesNotExist;

    private static bool WaitUntilGone()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (!Exists()) { return true; }
            Thread.Sleep(500);
        }

        return false;
    }

    private static (int ExitCode, string Output) Sc(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments) { startInfo.ArgumentList.Add(argument); }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not run sc.exe.");

        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
