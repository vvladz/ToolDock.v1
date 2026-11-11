using System.Text.RegularExpressions;

namespace ToolDock.Common;

public static partial class Validation
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentNamePattern();

    public static void ValidateCatalog(ToolCatalog catalog)
    {
        if (catalog.Variables is null)
        {
            throw new InvalidDataException("Catalog variables must be an object.");
        }

        var variableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in catalog.Variables)
        {
            ValidateValueName(name);
            if (!variableNames.Add(name))
            {
                throw new InvalidDataException($"Duplicate catalog variable name differs only by case: {name}");
            }
            if (value is null)
            {
                throw new InvalidDataException($"Catalog variable value cannot be null: {name}");
            }
            ValidateEnvironmentValue(value);
        }

        var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commandNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var daemonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, definition) in catalog.Tools)
        {
            ValidateToolName(name);
            if (!packageNames.Add(name))
            {
                throw new InvalidDataException($"Duplicate package name differs only by case: {name}");
            }

            ValidateSource(name, definition);

            if (definition.Commands.Count == 0 && definition.Daemons.Count == 0)
            {
                throw new InvalidDataException($"Package {name} has no commands or daemons.");
            }

            foreach (var (commandName, command) in definition.Commands)
            {
                ValidateToolName(commandName);
                ValidateCommandName(commandName);
                if (!commandNames.Add(commandName))
                {
                    throw new InvalidDataException($"Duplicate command name differs only by case: {commandName}");
                }

                _ = ValidateExecutablePath($"command {commandName}", command.Executable);
                ValidateArguments($"Command {commandName}", command.Arguments);
                ValidateEnvironment($"command {commandName}", command.Environment);
            }

            foreach (var (daemonName, daemon) in definition.Daemons)
            {
                ValidateToolName(daemonName);
                if (!daemonNames.Add(daemonName))
                {
                    throw new InvalidDataException($"Duplicate daemon name differs only by case: {daemonName}");
                }

                _ = ValidateExecutablePath($"daemon {daemonName}", daemon.Executable);
                ValidateArguments($"Daemon {daemonName}", daemon.Arguments);
                ValidateEnvironment($"daemon {daemonName}", daemon.Environment);
            }
        }
    }

    private static void ValidateSource(string name, ToolDefinition definition)
    {
        if (definition.Url is null)
        {
            if (definition.Repo is null || !RepositoryPattern().IsMatch(definition.Repo) ||
                definition.Repo.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid GitHub repository for {name}: {definition.Repo}");
            if (string.IsNullOrWhiteSpace(definition.Asset) ||
                definition.Asset.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || definition.Asset is "." or "..")
                throw new InvalidDataException($"Invalid release asset name for {name}.");
            if (definition.Format is not null || definition.FileName is not null)
                throw new InvalidDataException($"Package {name}: format and fileName require a URL source.");
            return;
        }

        if (definition.Repo is not null || definition.Asset is not null)
            throw new InvalidDataException($"Package {name} must specify either repo and asset, or url.");
        if (!Uri.TryCreate(definition.Url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException($"Package URL for {name} must be absolute HTTPS without user info or a fragment.");
        if (definition.Format is not (null or "zip" or "file"))
            throw new InvalidDataException($"Package {name}: format must be zip or file.");
        if (definition.Format == "file")
        {
            if (string.IsNullOrWhiteSpace(definition.FileName) ||
                definition.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                definition.FileName.EndsWith('.') || definition.FileName.EndsWith(' ') ||
                definition.FileName.StartsWith(".tooldock-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package {name}: file format requires a plain fileName.");
            _ = ValidateExecutablePath(name, definition.FileName);
        }
        else if (definition.FileName is not null)
            throw new InvalidDataException($"Package {name}: fileName requires file format.");
    }

    public static void ValidateValueName(string name)
    {
        if (!ToolNamePattern().IsMatch(name) || name is "." or "..")
        {
            throw new InvalidDataException($"Invalid value name: {name}");
        }
    }

    public static void ValidateEnvironmentValue(string value)
    {
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new InvalidDataException("Environment values cannot contain NUL or newline characters.");
        }
    }

    public static void ValidateToolName(string name)
    {
        if (!ToolNamePattern().IsMatch(name) || name is "." or "..")
        {
            throw new InvalidDataException($"Invalid tool name: {name}");
        }
    }

    public static string ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 100 || version is "." or ".." ||
            version.EndsWith('.') || version.EndsWith(' ') ||
            version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException($"Release tag cannot be used as a version directory: {version}");
        }

        return version;
    }

    public static string ValidateExecutablePath(string owner, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException($"Executable path for {owner} must be relative.");
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var validationRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "ToolDockPathValidation");
        var full = Path.GetFullPath(normalized, validationRoot);
        var basePath = validationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ||
            normalized.IndexOfAny(['%', '!', '"', '^', '&', '|', '<', '>', '\r', '\n']) >= 0)
        {
            throw new InvalidDataException($"Unsafe executable path for {owner}.");
        }

        return normalized;
    }

    public static void ValidateCommandName(string name)
    {
        ValidateToolName(name);
        if (new[] { "tdctl", "ToolDock.Client", "ToolDock.Starter", "ToolDock.Updater", "starter", "updater" }
            .Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Command name is reserved by ToolDock: {name}");
        }
    }

    public static string ResolveExecutable(string root, string owner, string relativePath)
    {
        var normalized = ValidateExecutablePath(owner, relativePath);
        var resolved = Path.GetFullPath(normalized, root);
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Executable path for {owner} escapes the installed directory.");
        }
        return resolved;
    }

    public static ToolDefinition FindDefinition(ToolCatalog catalog, string name)
        => catalog.Tools.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value
           ?? throw new KeyNotFoundException($"Unknown package: {name}");

    public static (string PackageName, ToolDefinition Package, DaemonDefinition Daemon) FindDaemon(
        ToolCatalog catalog,
        string name)
    {
        foreach (var (packageName, package) in catalog.Tools)
        {
            var daemon = package.Daemons
                .FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
            if (daemon.Value is not null)
            {
                return (packageName, package, daemon.Value);
            }
        }

        throw new KeyNotFoundException($"Unknown daemon: {name}");
    }

    public static (string PackageName, ToolDefinition Package, CommandDefinition Command) FindCommand(
        ToolCatalog catalog,
        string name)
    {
        foreach (var (packageName, package) in catalog.Tools)
        {
            var command = package.Commands
                .FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
            if (command.Value is not null)
            {
                return (packageName, package, command.Value);
            }
        }

        throw new KeyNotFoundException($"Unknown command: {name}");
    }

    public static InstalledTool FindInstalled(InstalledState state, string name)
        => state.Tools.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value
           ?? throw new KeyNotFoundException($"Package is not installed: {name}");

    private static void ValidateArguments(string owner, string[] arguments)
    {
        if (arguments is null || arguments.Any(argument => argument is null || argument.Contains('\0')))
        {
            throw new InvalidDataException($"{owner} has an invalid argument.");
        }
    }

    private static void ValidateEnvironment(string owner, Dictionary<string, EnvironmentValue> environment)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in environment)
        {
            if (!EnvironmentNamePattern().IsMatch(name) || !names.Add(name))
            {
                throw new InvalidDataException($"Invalid or duplicate environment name for {owner}: {name}");
            }

            if (value.Literal is not null)
            {
                ValidateEnvironmentValue(value.Literal);
            }
            else if (value.Variable is not null)
            {
                ValidateValueName(value.Variable);
            }
            else if (value.Secret is not null)
            {
                ValidateValueName(value.Secret);
            }
            else
            {
                throw new InvalidDataException($"Environment value for {owner}/{name} has no source.");
            }
        }
    }
}
