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

    private static readonly Regex SpecRefRegex = new(
        """\[Trait\("SpecRef",\s*"([^"]+)"\)\]""",
        RegexOptions.Compiled);

    private static readonly Regex CategoryRegex = new(
        """\[Trait\("Category",\s*"(Spec|Impl)"\)\]\s*public\s+(?:sealed\s+)?class""",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex SpecificationHeadingRegex = new(
        """^#{2,4}\s+((?:\d+\.)*\d+[A-Z]?)\b""",
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
        var specificationClauses = GetSpecificationClauses();

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

                var expectedClauses = GetExpectedSpecRefs(methodName);
                Assert.True(
                    expectedClauses.Count > 0,
                    $"Spec 测试命名不符合条款格式: {Path.GetFileName(testFile)}::{methodName}");

                var specRefMatches = SpecRefRegex.Matches(attributesBlock)
                    .Select(matchItem => matchItem.Groups[1].Value)
                    .ToList();
                Assert.True(
                    specRefMatches.Count > 0,
                    $"Spec 测试缺少 SpecRef 标记: {Path.GetFileName(testFile)}::{methodName}");
                Assert.Equal(expectedClauses, specRefMatches);

                foreach (var specRef in specRefMatches)
                {
                    Assert.Contains(
                        specRef,
                        specificationClauses);
                }
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

    private static IReadOnlyList<string> GetExpectedSpecRefs(string methodName)
    {
        const string prefix = "Spec_";
        if (!methodName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return [];
        }

        var tokens = methodName[prefix.Length..].Split('_', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<string>();

        for (var index = 0; index < tokens.Length;)
        {
            if (string.Equals(tokens[index], "And", StringComparison.Ordinal))
            {
                index += 1;
                continue;
            }

            if (!IsClauseToken(tokens[index]) || index + 1 >= tokens.Length || !IsClauseToken(tokens[index + 1]))
            {
                break;
            }

            var clauseParts = new List<string> { tokens[index], tokens[index + 1] };
            index += 2;

            while (index < tokens.Length && IsClauseToken(tokens[index]))
            {
                clauseParts.Add(tokens[index]);
                index += 1;
            }

            results.Add(string.Join(".", clauseParts));

            if (index >= tokens.Length || !string.Equals(tokens[index], "And", StringComparison.Ordinal))
            {
                break;
            }
        }

        return results;
    }

    private static bool IsClauseToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        if (token.All(char.IsDigit))
        {
            return true;
        }

        return token.Length > 1
            && token[..^1].All(char.IsDigit)
            && char.IsUpper(token[^1]);
    }

    private static HashSet<string> GetSpecificationClauses()
    {
        var specificationPath = FindSpecificationPath();
        var specification = File.ReadAllText(specificationPath);
        return SpecificationHeadingRegex.Matches(specification)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FindSpecificationPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var specificationPath = Path.Combine(directory.FullName, "docs", "specification", "protocol", "Specification.md");
            if (File.Exists(specificationPath))
            {
                return specificationPath;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("无法定位 docs/specification/protocol/Specification.md。");
    }
}
