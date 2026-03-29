namespace DevHub.Host.Tests.TestHelpers;

/// <summary>
/// 环境变量作用域辅助工具。
/// </summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previousValue;

    /// <summary>
    /// 设置指定环境变量，并在释放时恢复原值。
    /// </summary>
    /// <param name="name">环境变量名。</param>
    /// <param name="value">环境变量值。</param>
    public EnvironmentVariableScope(string name, string? value)
    {
        _name = name;
        _previousValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_name, _previousValue);
    }
}
