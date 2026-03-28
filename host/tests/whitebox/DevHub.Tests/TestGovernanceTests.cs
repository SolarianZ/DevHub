namespace DevHub.Tests;

using System.Text.RegularExpressions;

/// <summary>
/// 白盒测试治理规则校验。
/// </summary>
[Trait("Category", "Impl")]
public class TestGovernanceTests
{
    private static readonly Regex TestMethodRegex = new(
        """(?ms)(\[(?:Fact|Theory)\][\r\n \t]*(?:\[[^\]]+\][\r\n \t]*)*)(public\s+(?:async\s+)?(?:Task|void)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\()""",
        RegexOptions.Compiled);

    private static readonly Regex SpecNameRegex = new(
        """^Spec_(\d+)_(\d+)(?:_(\d+))?_""",
        RegexOptions.Compiled);

    private static readonly Regex SpecRefRegex = new(
        """\[Trait\("SpecRef",\s*"([^"]+)"\)\]""",
        RegexOptions.Compiled);

    private static readonly Regex CategoryRegex = new(
        """\[Trait\("Category",\s*"(Spec|Impl)"\)\]\s*public\s+(?:sealed\s+)?class""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void Impl_AllWhiteboxTests_ShouldUseSpecOrImplPrefix()
    {
        foreach (var testFile in GetTestFiles())
        {
            var content = File.ReadAllText(testFile);
            foreach (Match match in TestMethodRegex.Matches(content))
            {
                var methodName = match.Groups[3].Value;
                Assert.True(
                    methodName.StartsWith("Spec_", StringComparison.Ordinal) ||
                    methodName.StartsWith("Impl_", StringComparison.Ordinal),
                    $"测试方法命名不符合约定: {Path.GetFileName(testFile)}::{methodName}");
            }
        }
    }

    [Fact]
    public void Impl_SpecTests_ShouldDeclareSpecRefAndClauseConsistency()
    {
        foreach (var testFile in GetTestFiles())
        {
            var content = File.ReadAllText(testFile);
            foreach (Match match in TestMethodRegex.Matches(content))
            {
                var attributesBlock = match.Groups[1].Value;
                var methodName = match.Groups[3].Value;
                if (!methodName.StartsWith("Spec_", StringComparison.Ordinal))
                {
                    continue;
                }

                var clauseMatch = SpecNameRegex.Match(methodName);
                Assert.True(
                    clauseMatch.Success,
                    $"Spec 测试命名不符合条款格式: {Path.GetFileName(testFile)}::{methodName}");

                var expectedClause = clauseMatch.Groups[1].Value + "." + clauseMatch.Groups[2].Value;
                if (clauseMatch.Groups[3].Success)
                {
                    expectedClause += "." + clauseMatch.Groups[3].Value;
                }

                var specRefMatch = SpecRefRegex.Match(attributesBlock);
                Assert.True(
                    specRefMatch.Success,
                    $"Spec 测试缺少 SpecRef 标记: {Path.GetFileName(testFile)}::{methodName}");
                Assert.Equal(expectedClause, specRefMatch.Groups[1].Value);
            }
        }
    }

    [Fact]
    public void Impl_TestClassCategory_ShouldMatchContainedTests()
    {
        foreach (var testFile in GetTestFiles())
        {
            var content = File.ReadAllText(testFile);
            var methods = TestMethodRegex.Matches(content);
            if (methods.Count == 0)
            {
                continue;
            }

            var expectedCategory = "Impl";
            var allSpec = true;
            foreach (Match match in methods)
            {
                if (!match.Groups[3].Value.StartsWith("Spec_", StringComparison.Ordinal))
                {
                    allSpec = false;
                    break;
                }
            }

            if (allSpec)
            {
                expectedCategory = "Spec";
            }

            var categoryMatch = CategoryRegex.Match(content);
            Assert.True(
                categoryMatch.Success,
                $"测试类缺少 Category 标记: {Path.GetFileName(testFile)}");
            Assert.Equal(expectedCategory, categoryMatch.Groups[1].Value);
        }
    }

    private static IReadOnlyList<string> GetTestFiles()
    {
        var projectDirectory = FindProjectDirectory();
        var files = Directory
            .GetFiles(projectDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).Equals("GlobalUsings.cs", StringComparison.Ordinal))
            .Where(path => !Path.GetFileName(path).Equals("EnvironmentVariableScope.cs", StringComparison.Ordinal))
            .Where(path => !Path.GetFileName(path).Equals("TestAssemblySettings.cs", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        return files;
    }

    private static string FindProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var projectFile = Path.Combine(directory.FullName, "DevHub.Tests.csproj");
            if (File.Exists(projectFile))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("无法定位 DevHub.Tests.csproj。");
    }
}
