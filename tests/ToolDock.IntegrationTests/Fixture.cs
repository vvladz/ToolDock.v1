using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ToolDock.Common;
using ToolDock.Common.Logging;
using ToolDock.Updating;

namespace ToolDock.IntegrationTests;

internal sealed class Fixture : IDisposable
{
    public ToolDockPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "ToolDock-tests-" + Guid.NewGuid().ToString("N")));
    public FakeSource Source { get; } = new();
    public RecordingNotifications Events { get; } = new();
    public ILoggerFactory Logs { get; }
    private readonly HttpClient _http;
    public Func<string, CancellationToken, Task<string>> Reconcile { get; set; } = (_, _) => Task.FromResult("OK reconciled");

    public Fixture()
    {
        Paths.EnsureDirectories();
        Logs = LoggerFactory.Create(b => b.AddProvider(new RotatingFileLoggerProvider(Path.Combine(Paths.Logs, "ToolDock.Updater.log"))));
        _http = new HttpClient(Source);
        JsonFiles.WriteAtomicAsync(Paths.ConfigFile, new ToolDockConfig { CatalogUrl = "https://test.local/catalog" }).GetAwaiter().GetResult();
    }

    public UpdateResult Run(CancellationToken token = default) => new UpdateCoordinator(Paths, Logs)
    { HttpClient = _http, Notifications = Events, Reconcile = Reconcile }.Run(token);

    public Task<InstalledSnapshot> Snapshot() => InstalledSnapshot.ReadAsync(Paths);
    public string Log => File.ReadAllText(Path.Combine(Paths.Logs, "ToolDock.Updater.log"));

    public static ToolDefinition Definition(string executable = "app.exe", string command = "app", string repo = "example/app", string asset = "app.zip")
        => new() { Repo = repo, Asset = asset, Commands = new() { [command] = new() { Executable = executable } } };

    public static byte[] Archive(params (string Name, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        return memory.ToArray();
    }

    public void Dispose()
    {
        _http.Dispose();
        Logs.Dispose();
        // Remove links themselves before recursive cleanup; never follow a test junction.
        for (var attempt = 0; ; attempt++)
        {
            try { DeleteTestTree(new DirectoryInfo(Paths.Root)); break; }
            catch (Exception exception) when (attempt < 10 && exception is IOException or UnauthorizedAccessException)
            { Thread.Sleep(100); }
        }
    }

    private static void DeleteTestTree(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo child && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) DeleteTestTree(child);
            else entry.Delete();
        }
        directory.Delete();
    }

    public static Process Start(string executable, IEnumerable<string> args, string? root = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (root is null) info.Environment.Remove("TOOLDOCK_HOME");
        else info.Environment["TOOLDOCK_HOME"] = root;
        return Process.Start(info)!;
    }
}

internal sealed class RecordingNotifications : INotificationSender
{
    public List<(string Type, string Text)> Messages { get; } = [];
    public bool Throw { get; set; }
    public Task SendAsync(string eventType, string message, CancellationToken cancellationToken = default)
    {
        if (Throw) throw new IOException("handler failed");
        lock (Messages) Messages.Add((eventType, message));
        return Task.CompletedTask;
    }
}

internal sealed class FakeSource : HttpMessageHandler
{
    public ToolCatalog Catalog { get; set; } = new() { Tools = new() { ["app"] = Fixture.Definition() } };
    public string Version { get; set; } = "v1";
    public byte[] Zip { get; set; } = Fixture.Archive(("app.exe", "first"));
    public bool FailCatalog { get; set; }
    public bool FailDownload { get; set; }
    public int Downloads { get; private set; }
    public int ReleaseRequests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = request.RequestUri!;
        if (url.AbsolutePath == "/catalog")
        {
            if (FailCatalog) throw new HttpRequestException("catalog unavailable");
            return Task.FromResult(Json(Catalog));
        }
        if (url.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
        {
            ReleaseRequests++;
            var repository = string.Join('/', url.Segments.Skip(2).Take(2)).Replace("//", "/").Trim('/');
            var assets = Catalog.Tools.Values.Where(d => string.Equals(d.Repo, repository, StringComparison.OrdinalIgnoreCase))
                .Select(d => new { name = d.Asset, browser_download_url = "https://test.local/download" }).Distinct().ToArray();
            return Task.FromResult(Json(new { tag_name = Version, assets }));
        }
        Downloads++;
        if (FailDownload) throw new HttpRequestException("download unavailable");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip) });
    }
    private static HttpResponseMessage Json(object value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, JsonFiles.Options), Encoding.UTF8, "application/json") };
}
