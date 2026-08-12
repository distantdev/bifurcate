using System.Diagnostics;
using System.IO;
using Bifurcate.Core;

namespace Bifurcate.Tray;

public enum SaveOutcome
{
    Saved,

    /// <summary>The user dismissed the elevation prompt.</summary>
    Declined,

    Failed,
}

public sealed record SaveResult(SaveOutcome Outcome, string? Error = null);

/// <summary>
/// Writes the config, elevating only if it has to. The config directory is admin-writable on
/// purpose: the service acts on these values, so letting any user edit them would let a standard
/// user aim hardening at an adapter of their choosing.
/// </summary>
internal static class ConfigApplier
{
    public const string ApplyVerb = "--apply-config";

    public static SaveResult Save(BifurcateConfig config)
    {
        try
        {
            ConfigStore.Save(config);
            return new SaveResult(SaveOutcome.Saved);
        }
        catch (UnauthorizedAccessException)
        {
            return SaveElevated(config);
        }
        catch (IOException ex)
        {
            return new SaveResult(SaveOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Handles the elevated relaunch. Returns a process exit code.</summary>
    public static int ApplyFromFile(string path)
    {
        try
        {
            BifurcateConfig? config = ConfigStore.Deserialize(File.ReadAllText(path));
            if (config is null) { return 2; }

            ConfigStore.Save(config);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 3;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static SaveResult SaveElevated(BifurcateConfig config)
    {
        string handoff = Path.Combine(Path.GetTempPath(), $"bifurcate-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(handoff, ConfigStore.Serialize(config));

        ProcessStartInfo startInfo = new(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        startInfo.ArgumentList.Add(ApplyVerb);
        startInfo.ArgumentList.Add(handoff);

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null) { return new SaveResult(SaveOutcome.Failed, "Could not start the elevated helper."); }

            process.WaitForExit();
            return process.ExitCode == 0
                ? new SaveResult(SaveOutcome.Saved)
                : new SaveResult(SaveOutcome.Failed, $"The elevated helper failed with code {process.ExitCode}.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 1223, the user dismissed the prompt.
            try { File.Delete(handoff); } catch (IOException) { }
            return new SaveResult(SaveOutcome.Declined);
        }
    }
}
