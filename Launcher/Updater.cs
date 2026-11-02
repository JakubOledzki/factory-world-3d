using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace FactoryWorld.Launcher;

/// <summary>Progress for the window: what is happening, and how far (bytes done of bytes to do).</summary>
public sealed record UpdateProgress(string Status, long Done, long Total);

/// <summary>
/// Brings the game in <see cref="GameDirectory"/> to the server's manifest: files missing or changed are downloaded
/// (resuming a broken download, checked against their SHA-256), files the game no longer has are removed.
/// </summary>
public sealed class Updater(HttpClient http, Uri baseUrl, string gameDirectory)
{
    private const string Installed = "installed.json";

    public string GameDirectory { get; } = gameDirectory;

    public async Task<Manifest> ManifestAsync(CancellationToken cancellationToken)
    {
        // Never a cached copy: the manifest is what says a new version is out
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, "manifest.json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync(stream, LauncherJson.Default.Manifest, cancellationToken)
            ?? throw new InvalidDataException("Empty manifest");
    }

    /// <summary>The files to download (missing, other size or other content); hashes only files it cannot vouch for.</summary>
    public async Task<List<ManifestFile>> MissingAsync(Manifest manifest, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var known = LoadInstalled().ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var missing = new List<ManifestFile>();
        var checkedFiles = 0;
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new UpdateProgress("Sprawdzam pliki gry…", checkedFiles++, manifest.Files.Count));
            var info = new FileInfo(LocalPath(file.Path));
            if (!info.Exists || info.Length != file.Size)
            {
                missing.Add(file);
                continue;
            }
            if (known.TryGetValue(file.Path, out var installed) && installed.Size == info.Length
                && installed.WrittenUtc == info.LastWriteTimeUtc && SameHash(installed.Sha256, file.Sha256))
            {
                continue;
            }
            if (!SameHash(await HashAsync(info.FullName, cancellationToken), file.Sha256))
            {
                missing.Add(file);
            }
        }
        return missing;
    }

    /// <summary>Downloads the files, then removes what the manifest no longer has and records what is installed.</summary>
    public async Task SyncAsync(Manifest manifest, IReadOnlyList<ManifestFile> missing, IProgress<UpdateProgress> progress, CancellationToken cancellationToken)
    {
        var total = missing.Sum(f => f.Size);
        long done = 0;
        foreach (var file in missing)
        {
            var name = Path.GetFileName(file.Path);
            await DownloadAsync(file, bytes =>
            {
                done += bytes;
                progress.Report(new UpdateProgress($"Pobieram {name}", done, total));
            }, cancellationToken);
        }
        RemoveExtra(manifest);
        SaveInstalled(manifest);
        progress.Report(new UpdateProgress("Gra jest aktualna", total, total));
    }

    public Process Start(Manifest manifest, string serverUrl)
    {
        var exe = LocalPath(manifest.Exe);
        return Process.Start(new ProcessStartInfo(exe, $"-ServerUrl={serverUrl}")
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("The game did not start");
    }

    private async Task DownloadAsync(ManifestFile file, Action<long> downloaded, CancellationToken cancellationToken)
    {
        var target = LocalPath(file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        for (var attempt = 1; ; attempt++)
        {
            // What this attempt has counted, taken back if it fails
            long reported = 0;
            void Report(long bytes)
            {
                reported += bytes;
                downloaded(bytes);
            }
            try
            {
                // A broken download goes on where it stopped
                var have = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (have > file.Size)
                {
                    File.Delete(part);
                    have = 0;
                }
                Report(have);
                if (have < file.Size)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, $"files/{file.Sha256.ToLowerInvariant()}"));
                    if (have > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(have, null);
                    }
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    var append = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                    if (have > 0 && !append)
                    {
                        Report(-have);  // the server sent the whole file again
                    }
                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                    await using var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        Report(read);
                    }
                }
                if (!SameHash(await HashAsync(part, cancellationToken), file.Sha256))
                {
                    File.Delete(part);
                    throw new InvalidDataException($"{file.Path}: the download does not match its checksum");
                }
                File.Move(part, target, overwrite: true);
                return;
            }
            catch (Exception e) when (attempt < 4 && e is HttpRequestException or IOException or InvalidDataException && !cancellationToken.IsCancellationRequested)
            {
                downloaded(-reported);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
            }
        }
    }

    private void RemoveExtra(Manifest manifest)
    {
        if (!Directory.Exists(GameDirectory))
        {
            return;
        }
        var wanted = new HashSet<string>(manifest.Files.Select(f => Path.GetFullPath(LocalPath(f.Path))), StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(Path.Combine(GameDirectory, Installed)),
        };
        foreach (var file in Directory.EnumerateFiles(GameDirectory, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            // What the game writes for itself (settings, logs, the login) stays
            if (full.Contains($"{Path.DirectorySeparatorChar}Saved{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!wanted.Contains(full) && !full.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(full);
            }
        }
    }

    private IEnumerable<InstalledFile> LoadInstalled()
    {
        try
        {
            var path = Path.Combine(GameDirectory, Installed);
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), LauncherJson.Default.ListInstalledFile) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void SaveInstalled(Manifest manifest)
    {
        var list = manifest.Files
            .Select(f => (File: f, Info: new FileInfo(LocalPath(f.Path))))
            .Where(x => x.Info.Exists)
            .Select(x => new InstalledFile(x.File.Path, x.Info.Length, x.Info.LastWriteTimeUtc, x.File.Sha256))
            .ToList();
        Directory.CreateDirectory(GameDirectory);
        File.WriteAllText(Path.Combine(GameDirectory, Installed), JsonSerializer.Serialize(list, LauncherJson.Default.ListInstalledFile));
    }

    private string LocalPath(string manifestPath)
    {
        var full = Path.GetFullPath(Path.Combine(GameDirectory, manifestPath.Replace('/', Path.DirectorySeparatorChar)));
        // A manifest cannot point outside the game's folder
        if (!full.StartsWith(Path.GetFullPath(GameDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path outside the game: {manifestPath}");
        }
        return full;
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static bool SameHash(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
