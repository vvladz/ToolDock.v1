using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolDock.Common;

public sealed class ToolDockConfig
{
    public required string CatalogUrl { get; init; }
    public string? NotificationCommand { get; init; }
    public int NotificationTimeoutSeconds { get; init; } = 10;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ToolCatalog
{
    public Dictionary<string, string> Variables { get; init; } = [];
    public Dictionary<string, ToolDefinition> Tools { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ToolDefinition
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Repo { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Asset { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileName { get; init; }
    public bool Enabled { get; init; } = true;
    public Dictionary<string, CommandDefinition> Commands { get; init; } = [];
    public Dictionary<string, DaemonDefinition> Daemons { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CommandDefinition
{
    public required string Executable { get; init; }
    public string[] Arguments { get; init; } = [];
    public Dictionary<string, EnvironmentValue> Environment { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DaemonDefinition
{
    public required string Executable { get; init; }
    public string[] Arguments { get; init; } = [];
    public bool Autostart { get; init; }
    public bool RestartOnUpdate { get; init; }
    public Dictionary<string, EnvironmentValue> Environment { get; init; } = [];
}

[JsonConverter(typeof(EnvironmentValueJsonConverter))]
public sealed class EnvironmentValue
{
    private EnvironmentValue(string? literal, string? variable, string? secret)
    {
        Literal = literal;
        Variable = variable;
        Secret = secret;
    }

    public string? Literal { get; }
    public string? Variable { get; }
    public string? Secret { get; }

    public static EnvironmentValue FromLiteral(string value) => new(value, null, null);
    public static EnvironmentValue FromVariable(string name) => new(null, name, null);
    public static EnvironmentValue FromSecret(string name) => new(null, null, name);
}

internal sealed class EnvironmentValueJsonConverter : JsonConverter<EnvironmentValue>
{
    public override EnvironmentValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return EnvironmentValue.FromLiteral(reader.GetString()!);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Environment value must be a string or a variable/secret reference.");
        }

        string? kind = null;
        string? value = null;
        var ended = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Invalid environment reference.");
            }

            var property = reader.GetString()!;
            if (kind is not null ||
                property is not ("variable" or "secret"))
            {
                throw new JsonException($"Unknown or duplicate environment reference property: {property}");
            }

            kind = property;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Environment {kind} reference must be a string.");
            }
            value = reader.GetString();
        }

        ended = reader.TokenType == JsonTokenType.EndObject;

        if (!ended || kind is null || value is null)
        {
            throw new JsonException("Environment reference must contain exactly one variable or secret property.");
        }

        return kind == "variable"
            ? EnvironmentValue.FromVariable(value)
            : EnvironmentValue.FromSecret(value);
    }

    public override void Write(Utf8JsonWriter writer, EnvironmentValue value, JsonSerializerOptions options)
    {
        if (value.Literal is not null)
        {
            writer.WriteStringValue(value.Literal);
            return;
        }

        writer.WriteStartObject();
        if (value.Variable is not null)
        {
            writer.WriteString("variable", value.Variable);
        }
        else if (value.Secret is not null)
        {
            writer.WriteString("secret", value.Secret);
        }
        else
        {
            throw new JsonException("Environment value has no source.");
        }
        writer.WriteEndObject();
    }
}

public sealed class InstalledState
{
    public Dictionary<string, InstalledTool> Tools { get; init; } = [];
    // Published with the version paths in one atomic write. Null denotes legacy state.
    public ToolCatalog? ActiveCatalog { get; set; }
    public List<string> GeneratedCommands { get; set; } = [];
}

public sealed class InstalledTool
{
    public required string Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Root { get; init; }

    public bool EntryPointsTracked { get; init; }
    public List<string> Commands { get; init; } = [];
    public string? Repository { get; init; }
    public string? Asset { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceId { get; init; }
    public string? ActivationId { get; init; }
    public Dictionary<string, string>? CatalogVariables { get; set; }
    public PendingReconciliation? PendingReconciliation { get; set; }

    // Legacy state stored the path of the package's single executable.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
}

public sealed class PendingReconciliation
{
    public required string Id { get; init; }
    public bool FirstInstall { get; init; }
    public string? PreviousVersion { get; init; }
    public bool VersionChanged { get; init; }
}
