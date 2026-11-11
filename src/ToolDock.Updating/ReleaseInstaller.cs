using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ToolDock.Common;

namespace ToolDock.Updating;

internal sealed class ReleaseInstaller(HttpClient http, ToolDockPaths paths, ILogger<ReleaseInstaller> log)
{
    internal const string MarkerName = ".tooldock-version.json";

    public async Task<(InstalledTool Tool, bool Changed)> PrepareAsync(string name, ToolDefinition definition,
        InstalledTool? installed, CancellationToken cancellationToken)
    {
        Validation.ValidateToolName(name);
        string? downloadPath = null;
        string? stageDirectory = null;
        try
        {
            ReleaseAsset release;
            string sourceId;
            if (definition.Url is not null)
            {
                var uri = new Uri(definition.Url);
                sourceId = HashIdentity("url\n" + uri.AbsoluteUri + "\n" + (definition.Format ?? "zip") + "\n" + definition.FileName);
                downloadPath = Path.Combine(paths.Temp, $"{name}-{Guid.NewGuid():N}.part");
                log.LogInformation("Downloading {Package} to check its content", name);
                await DownloadAsync(uri, downloadPath, cancellationToken);
                await using var downloaded = File.OpenRead(downloadPath);
                var hash = await SHA256.HashDataAsync(downloaded, cancellationToken);
                release = new ReleaseAsset("sha256-" + Convert.ToHexString(hash).ToLowerInvariant(), uri);
            }
            else
            {
                sourceId = GitHubIdentity(definition.Repo, definition.Asset)!;
                release = await new GitHubReleaseClient(http).GetLatestAsync(definition.Repo!, definition.Asset!, cancellationToken);
            }

            var version = Validation.ValidateVersion(release.Version);
            log.LogInformation("Checking {Package}: installed={InstalledVersion} latest={LatestVersion}",
                name, installed?.Version ?? "none", version);
            if (installed is not null && installed.Version == version &&
                (installed.SourceId ?? GitHubIdentity(installed.Repository, installed.Asset)) == sourceId)
                return (installed, false);

            var toolRoot = Path.Combine(paths.Tools, name);
            var finalDirectory = Path.Combine(toolRoot, $"{version}--{sourceId[..12]}");
            stageDirectory = Path.Combine(toolRoot, $".install-{Guid.NewGuid():N}");
            Directory.CreateDirectory(toolRoot);
            var marker = await JsonFiles.ReadOptionalAsync<VersionMarker>(Path.Combine(finalDirectory, MarkerName), cancellationToken);
            if (marker is null || marker.Version != version ||
                (marker.SourceId ?? GitHubIdentity(marker.Repository, marker.Asset)) != sourceId)
            {
                // Never replace an existing directory that may still be used by a process.
                if (Directory.Exists(finalDirectory)) finalDirectory += "-" + Guid.NewGuid().ToString("N");
                if (downloadPath is null)
                {
                    downloadPath = Path.Combine(paths.Temp, $"{name}-{Guid.NewGuid():N}.part");
                    log.LogInformation("Downloading {Package} {Version}", name, version);
                    await DownloadAsync(release.DownloadUri, downloadPath, cancellationToken);
                }
                Directory.CreateDirectory(stageDirectory);
                if (definition.Format == "file")
                    File.Copy(downloadPath, Validation.ResolveExecutable(stageDirectory, name, definition.FileName!));
                else
                    ZipFile.ExtractToDirectory(downloadPath, stageDirectory);
                ValidateExecutables(name, stageDirectory, definition);
                await JsonFiles.WriteAtomicAsync(Path.Combine(stageDirectory, MarkerName), new VersionMarker(
                    version, definition.Repo, definition.Asset, DateTimeOffset.UtcNow, sourceId), cancellationToken);
                Directory.Move(stageDirectory, finalDirectory);
            }
            ValidateExecutables(name, finalDirectory, definition);
            return (new InstalledTool
            {
                Version = version,
                Root = Path.GetRelativePath(paths.Root, finalDirectory),
                Repository = definition.Repo,
                Asset = definition.Asset,
                SourceId = sourceId,
                EntryPointsTracked = true,
                Commands = definition.Commands.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList()
            }, true);
        }
        finally
        {
            try
            {
                if (downloadPath is not null && File.Exists(downloadPath)) File.Delete(downloadPath);
                if (stageDirectory is not null && Directory.Exists(stageDirectory)) Directory.Delete(stageDirectory, recursive: true);
            }
            catch (IOException exception) { log.LogWarning(exception, "Could not remove temporary download for {Package}", name); }
        }
    }

    private async Task DownloadAsync(Uri uri, string downloadPath, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(downloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
        if (target.Length == 0) throw new InvalidDataException("Downloaded package is empty.");
    }

    private static string HashIdentity(string identity)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

    private static string? GitHubIdentity(string? repository, string? asset)
        => repository is null || asset is null ? null : HashIdentity(repository.ToUpperInvariant() + "\n" + asset);

    internal static void ValidateExecutables(string name, string root, ToolDefinition definition)
    {
        foreach (var relative in definition.Commands.Values.Select(c => c.Executable)
                     .Concat(definition.Daemons.Values.Select(d => d.Executable)))
        {
            if (!File.Exists(Validation.ResolveExecutable(root, name, relative)))
                throw new InvalidDataException($"Package {name} does not contain {relative}.");
        }
    }
}

internal sealed record VersionMarker(string Version, string? Repository, string? Asset, DateTimeOffset InstalledAt, string? SourceId = null);
