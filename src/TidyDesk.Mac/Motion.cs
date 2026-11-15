using System.Diagnostics;

namespace TidyDesk.Mac;

/// <summary>Follows the system's "Reduce motion" setting (macOS: Accessibility → Display → Reduce motion).</summary>
internal static class Motion
{
    private static readonly Lazy<bool> ReducedValue = new(ReadReduced);

    public static bool Animate => !ReducedValue.Value;

    private static bool ReadReduced()
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        try
        {
            var start = new ProcessStartInfo("/usr/bin/defaults") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "read", "com.apple.universalaccess", "reduceMotion" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process is null)
                return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            return output.Trim() == "1";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false; // the key is missing until the setting is first changed
        }
    }
}
