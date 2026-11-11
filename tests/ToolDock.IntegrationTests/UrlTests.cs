using System.Text;
using System.Text.Json;
using ToolDock.Common;
using ToolDock.Updating;

namespace ToolDock.IntegrationTests;

internal static partial class Suite
{
    private static ToolDefinition UrlDefinition(string url = "https://test.local/package.zip",
        string? format = null, string? fileName = null, string executable = "app.exe")
        => new() { Url = url, Format = format, FileName = fileName,
            Commands = new() { ["app"] = new() { Executable = executable } } };

    private static async Task UrlValidationAsync()
    {
        foreach (var source in new[]
        {
            "{}", "{\"repo\":\"example/app\"}", "{\"asset\":\"app.zip\"}",
            "{\"repo\":\"example/app\",\"asset\":\"app.zip\",\"url\":\"https://example.org/app.zip\"}",
            "{\"asset\":\"app.zip\",\"url\":\"https://example.org/app.zip\"}",
            "{\"url\":\"\"}", "{\"url\":\"http://example.org/app.zip\"}",
            "{\"url\":\"file:///C:/app.zip\"}", "{\"url\":\"../app.zip\"}",
            "{\"url\":\"https://user:password@example.org/app.zip\"}",
            "{\"url\":\"https://example.org/app.zip#ignored\"}",
            "{\"url\":\"https://example.org/app.zip\",\"format\":\"tar\"}",
            "{\"url\":\"https://example.org/app.zip\",\"fileName\":\"app.exe\"}",
            "{\"repo\":\"example/app\",\"asset\":\"app.zip\",\"format\":\"file\",\"fileName\":\"app.exe\"}"
        })
        {
            var definition = JsonSerializer.Deserialize<ToolDefinition>(source, JsonFiles.Options)!;
            definition.Commands.Add("app", new() { Executable = "app.exe" });
            Reject(() => Validation.ValidateCatalog(new() { Tools = new() { ["app"] = definition } }));
        }
        foreach (var fileName in new string?[] { null, "", "../app.exe", "nested/app.exe", "C:\\app.exe", "..",
                     "app.exe:stream", "app.exe.", ".tooldock-version.json" })
            Reject(() => Validation.ValidateCatalog(new() { Tools = new()
                { ["app"] = UrlDefinition(format: "file", fileName: fileName) } }));

        // Pre-URL installations have no SourceId. They must keep their paths and avoid a download.
        using var f = new Fixture();
        Require(f.Run().Status == UpdateStatus.Completed, "Initial GitHub install failed.");
        var snapshot = await f.Snapshot();
        var original = snapshot.State.Tools["app"];
        snapshot.State.Tools["app"] = new InstalledTool
        {
            Version = original.Version, Root = original.Root, Repository = original.Repository, Asset = original.Asset,
            EntryPointsTracked = original.EntryPointsTracked, Commands = original.Commands, ActivationId = original.ActivationId
        };
        await JsonFiles.WriteAtomicAsync(f.Paths.InstalledStateFile, snapshot.State);
        var markerPath = Path.Combine(snapshot.PackageRoot(f.Paths, "app"), ReleaseInstaller.MarkerName);
        await File.WriteAllTextAsync(markerPath, "{\"version\":\"v1\",\"repository\":\"example/app\",\"asset\":\"app.zip\",\"installedAt\":\"2026-01-01T00:00:00Z\"}");
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 0 } && f.Source.Downloads == 1,
            "Legacy GitHub state was reinstalled.");
        Require((await f.Snapshot()).State.Tools["app"].Root == original.Root, "Legacy version directory changed.");
        f.Source.Version = "v2";
        Require(f.Run().Status == UpdateStatus.Completed, "GitHub version change failed.");
        f.Source.Version = "v1";
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 1 } && f.Source.Downloads == 2,
            "Legacy version marker could not be reused.");
    }

    private static async Task UrlUpdatesAsync()
    {
        using var f = new Fixture();
        var reconciliations = 0;
        f.Reconcile = (_, _) => { reconciliations++; return Task.FromResult("OK reconciled"); };
        f.Source.Catalog.Tools["app"] = UrlDefinition();
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 1 }, $"URL install failed: {f.Log}");
        var first = await f.Snapshot();
        Require(f.Source.ReleaseRequests == 0 && f.Source.Downloads == 1, "URL source called GitHub or downloaded twice.");
        Require(first.State.Tools["app"].Version.StartsWith("sha256-", StringComparison.Ordinal), "URL version is not content-based.");
        Require(File.Exists(Path.Combine(f.Paths.Bin, "app.cmd")), "URL command shim is missing.");
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 0 } && f.Source.Downloads == 2,
            "An immediate second update did not check the URL or reinstalled identical content.");
        Require((await f.Snapshot()).State.Tools["app"].ActivationId == first.State.Tools["app"].ActivationId && reconciliations == 1,
            "Identical content triggered activation or daemon reconciliation.");
        Require(f.Events.Messages.Count == 1, "Identical URL content generated a notification.");
        f.Source.Catalog.Tools["app"] = UrlDefinition(format: "zip");
        Require(f.Run().Updated == 0, "Explicit ZIP format changed the default source identity.");

        var secondPayload = Fixture.Archive(("app.exe", "second"));
        f.Source.Zip = secondPayload;
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 1 }, "New content at the same URL was ignored.");
        var second = await f.Snapshot();
        Require(second.State.Tools["app"].Version != first.State.Tools["app"].Version &&
            File.ReadAllText(Path.Combine(second.PackageRoot(f.Paths, "app"), "app.exe")) == "second", "Wrong URL version installed.");
        Require(File.ReadAllText(Path.Combine(first.PackageRoot(f.Paths, "app"), "app.exe")) == "first", "Old URL version was overwritten.");

        f.Source.Catalog.Tools["app"] = UrlDefinition("https://test.local/other.zip", executable: "new.exe");
        f.Source.FailDownload = true;
        Require(f.Run().Status == UpdateStatus.CompletedWithErrors, "URL download failure was ignored.");
        f.Source.FailDownload = false;
        foreach (var badPackage in new[] { new byte[] { 1, 2, 3 }, Fixture.Archive(("missing.exe", "invalid")) })
        {
            f.Source.Zip = badPackage;
            Require(f.Run().Status == UpdateStatus.CompletedWithErrors, "Invalid URL package was activated.");
        }
        var failed = await f.Snapshot();
        Require(failed.State.Tools["app"].Root == second.State.Tools["app"].Root &&
            failed.Catalog.Tools["app"].Commands["app"].Executable == "app.exe", "Failed URL update changed active files or definitions.");
        Require(!Directory.EnumerateFileSystemEntries(f.Paths.Temp).Any() &&
            !Directory.EnumerateDirectories(Path.Combine(f.Paths.Tools, "app"), ".install-*").Any(), "Failed URL download left temporary files.");

        f.Source.Zip = secondPayload;
        f.Source.Catalog.Tools["app"] = UrlDefinition("https://test.local/other.zip");
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 1 }, "Changing URL with identical bytes was ignored.");
        var other = await f.Snapshot();
        Require(other.State.Tools["app"].Version == second.State.Tools["app"].Version &&
            other.State.Tools["app"].Root != second.State.Tools["app"].Root, "Changed URL reused the old source directory.");

        f.Source.Catalog.Tools["app"] = Fixture.Definition();
        f.Source.Version = other.State.Tools["app"].Version;
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 1 }, "URL to GitHub switch with the same version was ignored.");
        f.Source.Catalog.Tools["app"] = UrlDefinition("https://test.local/other.zip");
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 1 }, "GitHub to URL switch with the same version was ignored.");
        Require((await f.Snapshot()).State.Tools["app"].Root == other.State.Tools["app"].Root, "Existing URL marker was not reused.");
    }

    private static async Task UrlFilesAsync()
    {
        using var f = new Fixture();
        f.Source.Catalog.Tools["app"] = UrlDefinition("https://test.local/binary", "file", "app.exe");
        f.Source.Zip = Encoding.UTF8.GetBytes("first binary");
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 1 }, $"Single-file install failed: {f.Log}");
        var first = await f.Snapshot();
        Require(File.ReadAllBytes(Path.Combine(first.PackageRoot(f.Paths, "app"), "app.exe")).SequenceEqual(f.Source.Zip),
            "Single-file payload was not copied exactly.");
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 0 }, "Identical single file was reinstalled.");
        f.Source.Zip = [];
        Require(f.Run().Status == UpdateStatus.CompletedWithErrors &&
            (await f.Snapshot()).State.Tools["app"].Root == first.State.Tools["app"].Root, "Empty binary replaced a working installation.");
        f.Source.Zip = Encoding.UTF8.GetBytes("first binary");
        f.Source.Catalog.Tools["app"] = UrlDefinition("https://test.local/binary", "file", "renamed.exe", "renamed.exe");
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 1 }, "Changed binary fileName was ignored.");
        var renamed = await f.Snapshot();
        Require(renamed.State.Tools["app"].Root != first.State.Tools["app"].Root &&
            File.Exists(Path.Combine(renamed.PackageRoot(f.Paths, "app"), "renamed.exe")), "Renamed binary was not staged separately.");

        for (var i = 0; i < 4; i++)
        {
            f.Source.Zip = Encoding.UTF8.GetBytes($"binary {i}");
            Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 1 }, "Changed binary was ignored.");
        }
        var versions = Directory.EnumerateDirectories(Path.Combine(f.Paths.Tools, "app"))
            .Where(p => !Path.GetFileName(p).Equals("current", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(p, ReleaseInstaller.MarkerName))).ToArray();
        Require(versions.Length == 3, "URL versions did not participate in retention.");
        Require(!Directory.EnumerateFileSystemEntries(f.Paths.Temp).Any(), "Single-file update left temporary downloads.");
    }
}
