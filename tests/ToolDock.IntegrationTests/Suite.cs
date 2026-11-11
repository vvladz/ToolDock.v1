using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ToolDock.Common;
using ToolDock.Updating;

namespace ToolDock.IntegrationTests;

internal static partial class Suite
{
    private static readonly string Executable = Path.Combine(AppContext.BaseDirectory, "ToolDock.IntegrationTests.exe");
    private static int _passed;
    private static string? _filter;

    public static async Task RunAsync(string? filter = null)
    {
        _filter = filter;
        await Test("path validation and legacy state", ValidationAsync);
        await Test("native junctions with shell metacharacters", JunctionAsync);
        await Test("concurrent log rotation", LogRotationAsync);
        await Test("successful install, source identity and failed catalog changes", UpdatesAsync);
        await Test("URL source validation and GitHub state compatibility", UrlValidationAsync);
        await Test("URL ZIP content updates, failures and source changes", UrlUpdatesAsync);
        await Test("URL single files, entry points and version retention", UrlFilesAsync);
        await Test("interrupted activation, obsolete shims and user files", RecoveryAsync);
        await Test("persistent reconciliation, outcomes and failure suppression", ReconciliationAsync);
        await Test("version retention and live leases", RetentionAsync);
        await Test("notification command contract and timeout", NotificationsAsync);
        await Test("prompt daemon exits and intentional lifecycle commands", DaemonsAsync);
        await Test("reconciliation survives a Starter restart", DurableReconciliationAsync);
        await Test("custom install root and overlapping clients", ClientsAsync);
        Console.WriteLine($"PASS: {_passed} integration scenarios");
    }

    private static async Task Test(string name, Func<Task> test)
    {
        if (_filter is not null && !name.Contains(_filter, StringComparison.OrdinalIgnoreCase)) return;
        await test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Unsafe input was accepted.");
    }

    private static Task ValidationAsync()
    {
        var previousHome = Environment.GetEnvironmentVariable("TOOLDOCK_HOME");
        try
        {
            Environment.SetEnvironmentVariable("TOOLDOCK_HOME", null);
            var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tooldock");
            var paths = new ToolDockPaths();
            var suffix = "." + ToolDockPaths.PathIdentity(expectedRoot);
            Require(string.Equals(paths.Root, expectedRoot, StringComparison.OrdinalIgnoreCase), "Wrong default installation root.");
            Require(paths.StarterPipeName == "ToolDock.Starter.v1" + suffix &&
                paths.StarterMutexName == @"Local\ToolDock.Starter" + suffix &&
                paths.UpdateMutexName == @"Local\ToolDock.Update" + suffix,
                "Default installation does not use root-specific IPC names.");
        }
        finally { Environment.SetEnvironmentVariable("TOOLDOCK_HOME", previousHome); }

        using var f = new Fixture();
        foreach (var name in new[] { "tdctl", "TDCTL", "ToolDock.Client" })
            Reject(() => Validation.ValidateCatalog(new ToolCatalog { Tools = new() { ["app"] = Fixture.Definition(command: name) } }));
        foreach (var arguments in new string[][] { null!, [null!], ["bad\0value"] })
            Reject(() => Validation.ValidateCatalog(new ToolCatalog { Tools = new()
            {
                ["app"] = new ToolDefinition { Repo = "example/app", Asset = "app.zip", Commands = new()
                {
                    ["app"] = new CommandDefinition { Executable = "app.exe", Arguments = arguments }
                } }
            } }));
        foreach (var path in new[] { "../bad.exe", "..\\bad.exe", "C:\\bad.exe", "\\bad.exe", "a/../../bad.exe" })
            Reject(() => Validation.ResolveExecutable(f.Paths.Tools, "test", path));
        Require(Validation.ResolveExecutable(f.Paths.Tools, "test", "nested/app.exe") == Path.Combine(f.Paths.Tools, "nested", "app.exe"), "Nested path rejected.");
        var legacy = JsonSerializer.Deserialize<InstalledState>("{\"tools\":{\"app\":{\"version\":\"v0\",\"path\":\"tools/app/v0/app.exe\"}}}", JsonFiles.Options)!;
        Require(legacy.Tools["app"].Repository is null && legacy.ActiveCatalog is null, "Legacy state cannot load.");
        return Task.CompletedTask;
    }

    private static async Task JunctionAsync()
    {
        using var f = new Fixture();
        var target = Path.Combine(f.Paths.Tools, Validation.ValidateVersion("v1 & echo harmless %test%!^"));
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "proof"), "ok");
        var junction = Path.Combine(f.Paths.Tools, "current with spaces");
        await Junction.SwitchAsync(junction, target, default);
        Require(await File.ReadAllTextAsync(Path.Combine(junction, "proof")) == "ok", "Junction target was shell-parsed.");
        Require(new DirectoryInfo(junction).ResolveLinkTarget(true)!.FullName == target, "Wrong junction target.");
    }

    private static async Task LogRotationAsync()
    {
        using var f = new Fixture();
        var path = Path.Combine(f.Paths.Logs, "concurrent.log");
        var workers = Enumerable.Range(0, 3).Select(i => Fixture.Start(Executable, ["--log-worker", path, i.ToString()])).ToArray();
        try
        {
            await Task.WhenAll(workers.Select(p => p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20))));
            Require(workers.All(p => p.ExitCode == 0), "Concurrent log writer failed.");
            var lines = Directory.GetFiles(f.Paths.Logs, "concurrent.log*").SelectMany(File.ReadAllLines).ToArray();
            Require(lines.Length == 300 && lines.Distinct().Count() == 300, "Rotation lost or interleaved log records.");
        }
        finally { foreach (var p in workers) p.Dispose(); }
    }

    private static async Task UpdatesAsync()
    {
        using (var legacy = new Fixture())
        {
            var root = Path.Combine(legacy.Paths.Tools, "app", "v0");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "app.exe"), "legacy");
            await JsonFiles.WriteAtomicAsync(legacy.Paths.CatalogCacheFile, new ToolCatalog
            { Variables = new() { ["endpoint"] = "old" }, Tools = new() { ["app"] = Fixture.Definition() } });
            await JsonFiles.WriteAtomicAsync(legacy.Paths.InstalledStateFile, new InstalledState
            { Tools = new() { ["app"] = new InstalledTool { Version = "v0" } } });
            legacy.Source.Catalog.Variables["endpoint"] = "new";
            legacy.Source.FailDownload = true;
            Require(legacy.Run().Status == UpdateStatus.CompletedWithErrors, "Legacy migration did not exercise failure.");
            Require((await legacy.Snapshot()).State.Tools["app"].CatalogVariables!["endpoint"] == "old", "Failed legacy update changed catalog variables.");
        }
        using var f = new Fixture();
        var first = f.Run();
        Require(first is { Status: UpdateStatus.Completed, Updated: 1 }, $"Install failed: {f.Log}");
        var initial = await f.Snapshot();
        Require(File.Exists(Path.Combine(f.Paths.Bin, "app.cmd")), "No command shim.");
        Require(f.Run() is { Status: UpdateStatus.Completed, Updated: 0 }, "Unchanged version counted as updated.");
        Require(f.Events.Messages.Count == 1, "Unchanged update emitted success.");
        f.Source.Catalog = new ToolCatalog { Variables = new() { ["endpoint"] = "new" }, Tools = new() { ["app"] = Fixture.Definition("nested/new.exe") } };
        f.Source.Version = "v2";
        f.Source.FailDownload = true;
        Require(f.Run().Status == UpdateStatus.CompletedWithErrors, "Download failure was ignored.");
        var failed = await f.Snapshot();
        Require(failed.Catalog.Tools["app"].Commands["app"].Executable == "app.exe" &&
            failed.State.Tools["app"].Root == initial.State.Tools["app"].Root, "Failed update replaced active definitions.");
        f.Source.FailDownload = false;
        Require(f.Run().Status == UpdateStatus.CompletedWithErrors, "Missing executable after extraction was accepted.");
        Require((await f.Snapshot()).State.Tools["app"].Version == "v1", "Staging failure changed active state.");
        f.Source.Zip = Fixture.Archive(("nested/new.exe", "second"));
        Require(f.Run().Status == UpdateStatus.Completed, "Second install failed.");
        var before = (await f.Snapshot()).State.Tools["app"].Root!;
        var successes = f.Events.Messages.Count(x => x.Type == "tool-update-success");
        f.Source.Catalog.Tools["app"] = Fixture.Definition("nested/new.exe", repo: "other/app");
        f.Source.Zip = Fixture.Archive(("nested/new.exe", "other repository"));
        Require(f.Run().Updated == 1, "Same-tag source change was ignored.");
        var after = (await f.Snapshot()).State.Tools["app"].Root!;
        Require(after != before && File.ReadAllText(Path.Combine(f.Paths.Root, after, "nested/new.exe")) == "other repository", "Old asset reused for a new source.");
        f.Source.Catalog.Tools["app"] = Fixture.Definition("nested/new.exe", repo: "other/app", asset: "other.zip");
        Require(f.Run().Updated == 1, "Same-tag asset change was ignored.");
        Require(f.Events.Messages.Count(x => x.Type == "tool-update-success") == successes, "Same-version source change emitted a version success event.");
        var enabled = f.Source.Catalog.Tools["app"];
        f.Source.Catalog.Tools["app"] = new ToolDefinition { Repo = enabled.Repo, Asset = enabled.Asset, Commands = enabled.Commands, Enabled = false };
        var eventCount = f.Events.Messages.Count;
        Require(f.Run() is { Status: UpdateStatus.Completed, Checked: 0, Updated: 0 } && f.Events.Messages.Count == eventCount,
            "Disabled package generated an event or update.");
        f.Source.Catalog.Tools["app"] = enabled;
        f.Events.Throw = true;
        f.Source.Version = "v3";
        Require(f.Run().Status == UpdateStatus.Completed, "Handler failure changed package outcome.");
    }

    private static async Task RecoveryAsync()
    {
        using var f = new Fixture();
        Require(f.Run().Status == UpdateStatus.Completed, "Initial install failed.");
        var old = await f.Snapshot();
        f.Source.Catalog.Tools["app"] = Fixture.Definition(command: "new-command");
        f.Source.Version = "v2";
        // A directory blocks shim publication after the new state and current target have been committed.
        Directory.CreateDirectory(Path.Combine(f.Paths.Bin, "new-command.cmd"));
        Require(f.Run().Status == UpdateStatus.CompletedWithErrors, "Injected activation failure did not occur.");
        var interrupted = await f.Snapshot();
        Require(interrupted.State.Tools["app"].PendingReconciliation is not null, "Interrupted activation lost pending reconciliation.");
        Directory.Delete(Path.Combine(f.Paths.Bin, "new-command.cmd"));
        await File.WriteAllTextAsync(f.Paths.CatalogCacheFile, "interrupted cache");
        Directory.Delete(Path.Combine(f.Paths.Tools, "app", "current"));
        Require(f.Run().Status == UpdateStatus.Completed, "Next run did not repair activation.");
        var recovered = await f.Snapshot();
        Require(recovered.State.Tools["app"].PendingReconciliation is null && File.Exists(Path.Combine(f.Paths.Bin, "new-command.cmd")), "Activation did not converge.");
        Require(!File.Exists(Path.Combine(f.Paths.Bin, "app.cmd")), "Obsolete command left behind.");
        Require(new DirectoryInfo(Path.Combine(f.Paths.Tools, "app", "current")).ResolveLinkTarget(true)!.FullName ==
            recovered.PackageRoot(f.Paths, "app"), "Current and state diverged.");
        await File.WriteAllTextAsync(Path.Combine(f.Paths.Bin, "new-command.cmd"), "user file");
        f.Source.Catalog = new ToolCatalog();
        Require(f.Run().Status == UpdateStatus.Completed, "Package removal failed.");
        Require(File.ReadAllText(Path.Combine(f.Paths.Bin, "new-command.cmd")) == "user file", "User command deleted.");
        Require(Directory.Exists(old.PackageRoot(f.Paths, "app")), "Package data deleted on removal.");
        // Removing and reassigning the same generated name must leave the replacement available.
        File.Delete(Path.Combine(f.Paths.Bin, "new-command.cmd"));
        f.Source.Catalog = new ToolCatalog { Tools = new() { ["replacement"] = Fixture.Definition(command: "new-command") } };
        Require(f.Run().Status == UpdateStatus.Completed && File.Exists(Path.Combine(f.Paths.Bin, "new-command.cmd")), "Reassigned command missing.");
        f.Source.Catalog = new ToolCatalog();
        Require(f.Run().Status == UpdateStatus.Completed && !File.Exists(Path.Combine(f.Paths.Bin, "new-command.cmd")), "Removed package left a generated shim.");
    }

    private static async Task ReconciliationAsync()
    {
        using var f = new Fixture();
        var requests = new List<string>();
        f.Reconcile = (request, _) => { requests.Add(request); throw new IOException("reply lost"); };
        Require(f.Run().Status == UpdateStatus.CompletedWithErrors, "Reconciliation error ignored.");
        Require(f.Run().Status == UpdateStatus.CompletedWithErrors && requests[0] == requests[1], "Pending identity changed on retry.");
        Require(f.Events.Messages.Count == 1 && f.Events.Messages[0].Type == "tool-update-failure", "Repeated failure spammed notifications.");
        Require(f.Events.Messages[0].Text.Contains("Checking app") && f.Events.Messages[0].Text.Contains("reply lost"), "Current-attempt log context missing.");
        f.Reconcile = (request, _) => { requests.Add(request); return Task.FromResult("OK already applied"); };
        Require(f.Run().Status == UpdateStatus.Completed, "Retry did not complete.");
        Require((await f.Snapshot()).State.Tools["app"].PendingReconciliation is null, "Retry remained pending.");
        Require(f.Events.Messages.Count(x => x.Type == "tool-update-success") == 1, "Reconciled update success missing or duplicated.");
        f.Source.FailCatalog = true;
        var count = f.Events.Messages.Count;
        Require(f.Run().Status == UpdateStatus.Failed && f.Run().Status == UpdateStatus.Failed, "Catalog failure ignored.");
        Require(f.Events.Messages.Count == count + 1 && f.Events.Messages[^1].Text.Contains("Fetching catalog"), "Run failures not suppressed or missing context.");
        f.Source.FailCatalog = false;
        Require(f.Run().Status == UpdateStatus.Completed, "Successful check failed.");
        f.Source.FailCatalog = true;
        f.Run();
        Require(f.Events.Messages.Count == count + 2, "Success did not reset failure suppression.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Require(f.Run(cancellation.Token).Status == UpdateStatus.Cancelled, "Cancellation not recorded.");
        var starts = f.Log.Split("Update started").Length - 1;
        var outcomes = f.Log.Split("Update finished:").Length - 1;
        Require(starts == outcomes && f.Log.Contains("status=Cancelled") && f.Log.Contains("status=CompletedWithErrors"), "Final outcome missing or duplicated.");
        using var mutex = new Mutex(false, f.Paths.UpdateMutexName);
        mutex.WaitOne();
        try
        {
            UpdateResult? blocked = null;
            var thread = new Thread(() => blocked = f.Run());
            thread.Start();
            thread.Join();
            Require(blocked?.Status == UpdateStatus.AlreadyRunning, "Concurrent coordinator was not excluded.");
        }
        finally { mutex.ReleaseMutex(); }
        Require(starts == f.Log.Split("Update started").Length - 1, "Already-running check logged a start.");
        var notices = new UpdateNotifications(f.Paths, f.Events, f.Logs.CreateLogger("test"));
        count = f.Events.Messages.Count;
        await notices.FailureAsync("changed", new IOException("first failure"), "attempt one");
        await notices.FailureAsync("changed", new IOException("first failure"), "attempt two");
        await notices.FailureAsync("changed", new IOException("different failure"), "attempt three");
        Require(f.Events.Messages.Count == count + 2, "Changed failure was suppressed.");
        using var attempt = new AttemptLog(f.Logs);
        var captured = attempt.CreateLogger("capture");
        for (var i = 0; i < 50; i++) captured.LogInformation("line {Line}", i);
        Require(attempt.Text.Split('\n').Length == 20 && !attempt.Text.Contains("line 0\n"), "Attempt log did not bound its lines.");
        captured.LogInformation("{LongLine}", new string('x', 9000));
        Require(System.Text.Encoding.UTF8.GetByteCount(attempt.Text) <= 4096, "Attempt excerpt exceeded 4 KiB.");
    }
}
