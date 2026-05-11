using Xunit;

internal static class TestCollections
{
    public const string ProcessEnvironment = nameof(ProcessEnvironment);
}

[CollectionDefinition(TestCollections.ProcessEnvironment, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
}
