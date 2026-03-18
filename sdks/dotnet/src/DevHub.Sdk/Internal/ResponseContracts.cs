using DevHub.Sdk.Models;

namespace DevHub.Sdk.Internal;

internal sealed class OkOnlyContract
{
    public bool Ok { get; set; }
}

internal sealed class ListDefinitionsContract
{
    public bool Ok { get; set; }

    public List<AppDefinition> Definitions { get; set; } = [];
}

internal sealed class GetDefinitionContract
{
    public bool Ok { get; set; }

    public AppDefinition Definition { get; set; } = new();
}

internal sealed class RegisterInstanceContract
{
    public bool Ok { get; set; }

    public AppInstance Instance { get; set; } = new();
}

internal sealed class HeartbeatContract
{
    public bool Ok { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }
}

internal sealed class ListInstancesContract
{
    public bool Ok { get; set; }

    public List<AppInstance> Instances { get; set; } = [];
}
