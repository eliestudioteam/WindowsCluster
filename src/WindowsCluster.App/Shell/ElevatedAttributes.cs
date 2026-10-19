using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WindowsCluster.App.Shell;

/// <summary>
/// Changes the Hidden attribute of Public Desktop shortcuts, which only administrators may
/// modify. The app re-launches itself elevated (one UAC prompt per batch) in a tiny helper
/// mode that touches nothing but the Hidden attribute of files directly in the Public Desktop.
/// </summary>
public static class ElevatedAttributes
{
    private const string HideSwitch = "--hide-public-desktop-items";
    private const string ShowSwitch = "--show-public-desktop-items";

    /// <summary>Runs the elevated helper. Returns false if the user declined UAC or it failed.</summary>
    public static bool Run(bool hide, IReadOnlyList<string> paths)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || paths.Count == 0)
            return false;
        var arguments = (hide ? HideSwitch : ShowSwitch) + " " + string.Join(' ', paths.Select(p => $"\"{p}\""));
        try
        {
            using var helper = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (helper is null)
                return false;
            helper.WaitForExit(60_000);
            return helper.HasExited && helper.ExitCode == 0;
        }
        catch (Win32Exception ex)
        {
            // 1223 = the user cancelled the UAC prompt.
            Debug.WriteLine($"[ElevatedAttributes] {ex.NativeErrorCode}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Helper mode entry point, called from App startup. Returns an exit code, or null when
    /// the command line is not a helper request.
    /// </summary>
    public static int? TryRunHelper(string[] args)
    {
        if (args.Length < 2 || (args[0] != HideSwitch && args[0] != ShowSwitch))
            return null;

        var hide = args[0] == HideSwitch;
        var publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var failures = 0;
        foreach (var path in args.Skip(1))
        {
            // Never act on anything outside the Public Desktop, whatever the arguments say.
            var full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(full), publicDesktop, StringComparison.OrdinalIgnoreCase) ||
                (!File.Exists(full) && !Directory.Exists(full)))
            {
                failures++;
                continue;
            }
            try
            {
                var attributes = File.GetAttributes(full);
                File.SetAttributes(full, hide ? attributes | FileAttributes.Hidden : attributes & ~FileAttributes.Hidden);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }
}
