using Microsoft.Extensions.Logging;
using ToolDock.Common;

namespace ToolDock.Updating;

internal sealed class UpdateEngine(
    ToolDockPaths paths,
    AttemptLog attemptLog,
    HttpClient http,
    Func<string, CancellationToken, Task<string>> reconcile,
    UpdateNotifications notifications)
{
    private readonly ILogger _log = attemptLog.CreateLogger(nameof(UpdateEngine));
    public int Checked { get; private set; }
    public int Updated { get; private set; }
    public int Failed { get; private set; }

    public async Task<UpdateResult> RunAsync(CancellationToken cancellationToken)
    {
        var snapshot = await InstalledSnapshot.ReadAsync(paths, cancellationToken);
        var state = snapshot.State;
        state.ActiveCatalog = snapshot.Catalog;
        foreach (var installed in state.Tools.Values)
            installed.CatalogVariables ??= snapshot.Catalog.Variables;
        state.GeneratedCommands = state.GeneratedCommands.Concat(state.Tools.SelectMany(p =>
                p.Value.EntryPointsTracked ? p.Value.Commands : new List<string> { p.Key }))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Repair projections before contacting the network, including an interrupted previous run.
        await PublishViewsAsync(state, cancellationToken);
        var config = await JsonFiles.ReadRequiredAsync<ToolDockConfig>(paths.ConfigFile, cancellationToken);
        _log.LogInformation("Fetching catalog");
        var desired = await new CatalogClient(http).FetchAsync(config.CatalogUrl, cancellationToken);
        await notifications.SucceededAsync(null);

        // Package removal removes entry points only; version directories and installed records remain.
        var active = new ToolCatalog { Variables = desired.Variables, Tools = new(state.ActiveCatalog.Tools) };
        foreach (var name in active.Tools.Keys.ToArray())
        {
            var definition = desired.Tools.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (definition is null) active.Tools.Remove(name);
            else if (!definition.Enabled) active.Tools[name] = definition;
        }
        state.ActiveCatalog = active;
        await JsonFiles.WriteAtomicAsync(paths.InstalledStateFile, state, cancellationToken);
        await PublishViewsAsync(state, cancellationToken);
        var installer = new ReleaseInstaller(http, paths, attemptLog.CreateLogger<ReleaseInstaller>());

        foreach (var (name, definition) in desired.Tools.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!definition.Enabled)
            {
                _log.LogInformation("Skipping disabled package {Package}", name);
                continue;
            }
            Checked++;
            attemptLog.Clear();
            try
            {
                var installed = state.Tools.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
                // Finish the previous activation before attempting a newer release.
                if (installed?.PendingReconciliation is not null)
                    await ReconcileAsync(state, name, installed, cancellationToken);
                var (staged, changed) = await installer.PrepareAsync(name, definition, installed, cancellationToken);
                var root = paths.ResolveUnderRoot(staged.Root ?? Path.Combine("tools", name, staged.Version));
                ReleaseInstaller.ValidateExecutables(name, root, definition);
                foreach (var command in definition.Commands.Keys) CommandShims.Check(paths, command);
                var activation = changed ? Guid.NewGuid().ToString("N") : staged.ActivationId;
                var result = new InstalledTool
                {
                    Version = staged.Version, Root = staged.Root, Repository = definition.Repo, Asset = definition.Asset,
                    SourceId = staged.SourceId,
                    EntryPointsTracked = true, Commands = definition.Commands.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList(),
                    CatalogVariables = desired.Variables, ActivationId = activation,
                    PendingReconciliation = changed ? new PendingReconciliation
                    {
                        Id = activation!, FirstInstall = installed is null, PreviousVersion = installed?.Version,
                        VersionChanged = installed?.Version != staged.Version
                    } : installed?.PendingReconciliation
                };
                var candidate = new InstalledState
                {
                    Tools = new(state.Tools, StringComparer.OrdinalIgnoreCase),
                    ActiveCatalog = new ToolCatalog
                    {
                        Variables = desired.Variables,
                        Tools = new(state.ActiveCatalog!.Tools, StringComparer.OrdinalIgnoreCase)
                    },
                    GeneratedCommands = state.GeneratedCommands.Union(result.Commands, StringComparer.OrdinalIgnoreCase).ToList()
                };
                candidate.Tools[name] = result;
                candidate.ActiveCatalog.Tools[name] = definition;
                Validation.ValidateCatalog(candidate.ActiveCatalog);
                // The atomic state commit publishes both executable roots and their launch definitions.
                await JsonFiles.WriteAtomicAsync(paths.InstalledStateFile, candidate, cancellationToken);
                state = candidate;
                await PublishViewsAsync(state, cancellationToken);
                if (changed) Updated++;
                await ReconcileAsync(state, name, result, cancellationToken);
                await notifications.SucceededAsync(name);
                await new VersionRetention(paths, _log).PruneAsync(name, result, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                Failed++;
                _log.LogError(exception, "Failed to update {Package}", name);
                await notifications.FailureAsync(name, exception, attemptLog.Text);
            }
        }
        return new UpdateResult(Failed == 0 ? UpdateStatus.Completed : UpdateStatus.CompletedWithErrors, Checked, Updated, Failed);
    }

    private async Task ReconcileAsync(InstalledState state, string name, InstalledTool installed, CancellationToken cancellationToken)
    {
        if (installed.PendingReconciliation is not { } pending) return;
        var response = await reconcile($"package-updated {name} {(pending.FirstInstall ? "installed" : "updated")} {pending.Id}", cancellationToken);
        if (!response.StartsWith("OK", StringComparison.Ordinal)) throw new IOException($"Starter rejected {name}: {response}");
        _log.LogInformation("Starter replied: {Response}", response);
        installed.PendingReconciliation = null;
        try { await JsonFiles.WriteAtomicAsync(paths.InstalledStateFile, state, cancellationToken); }
        catch { installed.PendingReconciliation = pending; throw; }
        if (pending.VersionChanged)
            await notifications.SendAsync("tool-update-success", $"Package updated: {name}\nVersion: {pending.PreviousVersion ?? "none"} -> {installed.Version}");
    }

    internal async Task PublishViewsAsync(InstalledState state, CancellationToken cancellationToken)
    {
        var catalog = state.ActiveCatalog!;
        await JsonFiles.WriteAtomicAsync(paths.CatalogCacheFile, catalog, cancellationToken);
        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, definition) in catalog.Tools)
        {
            var installed = state.Tools.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            if (installed is null) continue;
            var root = paths.ResolveUnderRoot(installed.Root ?? Path.Combine("tools", name, installed.Version));
            var current = Path.Combine(paths.Tools, name, "current");
            var target = Directory.Exists(current) ? new DirectoryInfo(current).ResolveLinkTarget(true)?.FullName : null;
            if (!string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                await Junction.SwitchAsync(current, root, cancellationToken);
            if (!definition.Enabled) continue;
            foreach (var command in definition.Commands.Keys)
            {
                commands.Add(command);
                if (File.Exists(Path.Combine(paths.Bin, $"{command}.cmd")) && !CommandShims.IsOwned(paths, command))
                {
                    _log.LogWarning("Preserving user command {Command}", command);
                    continue;
                }
                await CommandShims.WriteAsync(paths, command, cancellationToken);
            }
        }
        foreach (var command in state.GeneratedCommands.Except(commands, StringComparer.OrdinalIgnoreCase))
        {
            // Legacy and current generated shims have a known exact body. User files are never removed.
            try { Validation.ValidateCommandName(command); }
            catch (InvalidDataException) { continue; }
            if (CommandShims.IsOwned(paths, command))
            {
                File.Delete(Path.Combine(paths.Bin, $"{command}.cmd"));
                _log.LogInformation("Removed obsolete command {Command}", command);
            }
        }
    }
}
