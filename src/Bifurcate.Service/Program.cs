using Bifurcate.Core;
using Bifurcate.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

string verb = args.FirstOrDefault(argument => argument.StartsWith('-') || argument.StartsWith('/'))
    ?.ToLowerInvariant() ?? "";

switch (verb)
{
    case "--install":
        return ServiceInstaller.Install();

    case "--uninstall":
        return ServiceInstaller.Uninstall();

    case "--check":
        return await Diagnostics.RunAsync(CancellationToken.None);

    case "--help" or "-h" or "-?" or "/?":
        PrintUsage();
        return 0;

    case "" or "--console":
        break;

    default:
        Console.Error.WriteLine($"Unknown option '{verb}'.");
        PrintUsage();
        return 1;
}

// No args are passed to the builder on purpose: configuration comes from the JSON file, and a bare
// flag such as --console is not valid command-line configuration syntax.
HostApplicationBuilder builder = Host.CreateApplicationBuilder();
builder.Services.AddWindowsService(options => options.ServiceName = BifurcateInfo.ServiceName);
builder.Logging.AddBifurcateFile(BifurcateInfo.LogDirectory);
builder.Services.AddHostedService<KeepAliveWorker>();

await builder.Build().RunAsync();
return 0;

static void PrintUsage()
{
    Console.WriteLine($"""
        {BifurcateInfo.ServiceDisplayName}

        Run with no arguments to start as a Windows service.

          --install     Register and start the service. Needs an elevated prompt.
          --uninstall   Stop and remove the service, and clean up its firewall rules.
          --console     Run in this window instead of as a service, for troubleshooting.
          --check       Print the current configuration and observed state, then exit.

        Config file: {BifurcateInfo.ConfigPath}
        Logs:        {BifurcateInfo.LogDirectory}
        """);
}
