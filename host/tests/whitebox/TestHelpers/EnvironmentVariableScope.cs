namespace DevHub.Whitebox.TestHelpers;

/// <summary>
/// 在测试期间临时设置环境变量，并在释放时恢复原值。
/// </summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string _name;
    private readonly string? _originalValue;

    /// <summary>
    /// 创建环境变量作用域。
    /// </summary>
    /// <param name="name">环境变量名称。</param>
    /// <param name="value">临时值；为 <see langword="null"/> 时表示清除该环境变量。</param>
    public EnvironmentVariableScope(string name, string? value)
    {
        _name = name;
        _originalValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// 恢复环境变量原值。
    /// </summary>
    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_name, _originalValue);
    }
}
