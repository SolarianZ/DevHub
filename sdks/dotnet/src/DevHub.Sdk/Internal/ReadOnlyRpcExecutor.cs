using System.Text.Json;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.Internal;

internal static class ReadOnlyRpcExecutor
{
    internal delegate Task<JsonElement> SendAsyncDelegate(string method, object? parameters, CancellationToken cancellationToken);

    internal static Task<PingResult> PingAsync(
        SendAsyncDelegate sendAsync,
        object? echo,
        CancellationToken cancellationToken)
    {
        object? parameters = echo is null ? null : new Dictionary<string, object?> { ["echo"] = echo };
        return ExecuteAsync(
            sendAsync,
            "hub.ping",
            parameters,
            static result =>
            {
                var payload = ResponsePayloadReader.DeserializeRequired<PingResult>(result, "hub.ping.result");
                ResponsePayloadReader.EnsureOk(payload.Ok, "hub.ping.result");
                ResponsePayloadReader.EnsureTimestamp(payload.ServerTimeUtc, "hub.ping.result", "serverTimeUtc");
                return payload;
            },
            cancellationToken);
    }

    internal static Task<IReadOnlyList<AppDefinition>> ListDefinitionsAsync(
        SendAsyncDelegate sendAsync,
        ListDefinitionsRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            sendAsync,
            "hub.apps.listDefinitions",
            RequestPayloadFactory.BuildListDefinitionsParams(request),
            static result =>
            {
                var definitionsElement = ResponsePayloadReader.EnsurePropertyExists(
                    result,
                    "hub.apps.listDefinitions.result",
                    "definitions",
                    JsonValueKind.Array);

                var index = 0;
                foreach (var definitionElement in definitionsElement.EnumerateArray())
                {
                    ResponsePayloadReader.ValidateAppDefinitionElement(
                        definitionElement,
                        $"hub.apps.listDefinitions.result.definitions[{index}]");
                    index++;
                }

                var payload = ResponsePayloadReader.DeserializeRequired<ListDefinitionsContract>(
                    result,
                    "hub.apps.listDefinitions.result");
                ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.listDefinitions.result");
                ResponsePayloadReader.EnsureNotNull(payload.Definitions, "hub.apps.listDefinitions.result", "definitions");

                return (IReadOnlyList<AppDefinition>)payload.Definitions;
            },
            cancellationToken);
    }

    internal static Task<AppDefinition> GetDefinitionAsync(
        SendAsyncDelegate sendAsync,
        string appId,
        string scope,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            sendAsync,
            "hub.apps.getDefinition",
            RequestPayloadFactory.BuildGetDefinitionParams(appId, scope),
            static result =>
            {
                ResponsePayloadReader.ValidateAppDefinitionElement(
                    ResponsePayloadReader.EnsurePropertyExists(
                        result,
                        "hub.apps.getDefinition.result",
                        "definition",
                        JsonValueKind.Object),
                    "hub.apps.getDefinition.result.definition");

                var payload = ResponsePayloadReader.DeserializeRequired<GetDefinitionContract>(
                    result,
                    "hub.apps.getDefinition.result");
                ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.getDefinition.result");
                ResponsePayloadReader.EnsureNotNull(payload.Definition, "hub.apps.getDefinition.result", "definition");
                ResponsePayloadReader.EnsureNotEmpty(payload.Definition.AppId, "hub.apps.getDefinition.result", "definition.appId");
                ResponsePayloadReader.EnsureNotEmpty(payload.Definition.DisplayName, "hub.apps.getDefinition.result", "definition.displayName");
                return payload.Definition;
            },
            cancellationToken);
    }

    internal static Task<IReadOnlyList<AppInstance>> ListInstancesAsync(
        SendAsyncDelegate sendAsync,
        ListInstancesRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(
            sendAsync,
            "hub.apps.listInstances",
            RequestPayloadFactory.BuildListInstancesParams(request),
            static result =>
            {
                var instancesElement = ResponsePayloadReader.EnsurePropertyExists(
                    result,
                    "hub.apps.listInstances.result",
                    "instances",
                    JsonValueKind.Array);

                var index = 0;
                foreach (var instanceElement in instancesElement.EnumerateArray())
                {
                    ResponsePayloadReader.ValidateAppInstanceElement(
                        instanceElement,
                        $"hub.apps.listInstances.result.instances[{index}]");
                    index++;
                }

                var payload = ResponsePayloadReader.DeserializeRequired<ListInstancesContract>(
                    result,
                    "hub.apps.listInstances.result");
                ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.listInstances.result");
                ResponsePayloadReader.EnsureNotNull(payload.Instances, "hub.apps.listInstances.result", "instances");

                return (IReadOnlyList<AppInstance>)payload.Instances;
            },
            cancellationToken);
    }

    private static async Task<TResult> ExecuteAsync<TResult>(
        SendAsyncDelegate sendAsync,
        string method,
        object? parameters,
        Func<JsonElement, TResult> resultReader,
        CancellationToken cancellationToken)
    {
        var result = await sendAsync(method, parameters, cancellationToken);
        return resultReader(result);
    }
}
