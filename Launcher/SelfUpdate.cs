using System.Diagnostics;
using System.Reflection;

namespace FactoryWorld.Launcher;

/// <summary>
/// The launcher replaces itself when the manifest names a newer one: the new .exe is downloaded next to it and checked,
/// the running one renamed to .old (Windows allows that), the new one put in its place and started.
/// </summary>
public static class SelfUpdate
{
    public static Version Current { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public static string? ExePath => Environment.ProcessPath;

    /// <summary>Removes what an earlier update left behind.</summary>
    public static void CleanUp()
    {
        if (ExePath is { } exe && File.Exists(exe + ".old"))
        {
            try
            {
                File.Delete(exe + ".old");
            }
            catch (IOException)
            {
                // still closing: next time
            }
        }
    }

    public static bool IsNewer(ManifestLauncher? launcher) =>
        launcher is not null && Version.TryParse(launcher.Version, out var offered) && offered > Current;

    /// <summary>Downloads and starts the new launcher; true when this one should now close.</summary>
    public static async Task<bool> ApplyAsync(HttpClient http, Uri baseUrl, ManifestLauncher launcher, CancellationToken cancellationToken)
    {
        if (ExePath is not { } exe)
        {
            return false;
        }
        var next = exe + ".new";
        using (var response = await http.GetAsync(new Uri(baseUrl, $"files/{launcher.Sha256.ToLowerInvariant()}"), HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var output = File.Create(next);
            await response.Content.CopyToAsync(output, cancellationToken);
        }
        if (!string.Equals(await Updater.HashAsync(next, cancellationToken), launcher.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(next);
            throw new InvalidDataException("The new launcher does not match its checksum");
        }
        File.Move(exe, exe + ".old", overwrite: true);
        File.Move(next, exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        return true;
    }
}
