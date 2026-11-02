using System.Text.Json.Serialization;

namespace FactoryWorld.Launcher;

/// <summary>
/// What the server offers (download/manifest.json, written by Tools/dev/publish-game.ps1): the game's files with their
/// size and SHA-256, and the newest launcher. Every file is stored on the server as files/&lt;sha256&gt;, so a new
/// version uploads only what changed and the manifest is swapped in one step.
/// </summary>
public sealed record Manifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("exe")] string Exe,
    [property: JsonPropertyName("files")] IReadOnlyList<ManifestFile> Files,
    [property: JsonPropertyName("launcher")] ManifestLauncher? Launcher);

public sealed record ManifestFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record ManifestLauncher(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>What is installed: per file its size, time and hash, so unchanged files need not be hashed again.</summary>
public sealed record InstalledFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("written")] DateTime WrittenUtc,
    [property: JsonPropertyName("sha256")] string Sha256);

[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(List<InstalledFile>))]
internal sealed partial class LauncherJson : JsonSerializerContext;
