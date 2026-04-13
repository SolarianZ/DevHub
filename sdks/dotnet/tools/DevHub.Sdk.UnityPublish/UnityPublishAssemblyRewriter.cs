using Mono.Cecil;
using Mono.Cecil.Cil;

namespace DevHub.Sdk.UnityPublish;

/// <summary>
/// Unity 本地发布阶段使用的程序集引用后处理器。
/// </summary>
public static class UnityPublishAssemblyRewriter
{
    private const string NewtonsoftAssemblyName = "Newtonsoft.Json";

    /// <summary>
    /// 清除目标程序集对 <c>Newtonsoft.Json</c> 的强签名引用信息。
    /// </summary>
    /// <param name="assemblyPath">待处理的程序集路径。</param>
    /// <returns>本次处理结果。</returns>
    /// <exception cref="ArgumentException">当路径为空白时抛出。</exception>
    /// <exception cref="FileNotFoundException">当程序集文件不存在时抛出。</exception>
    /// <exception cref="InvalidOperationException">当程序集未引用或重复引用 <c>Newtonsoft.Json</c> 时抛出。</exception>
    public static RewriteResult RewriteNewtonsoftReference(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath))
        {
            throw new ArgumentException("程序集路径不能为空。", nameof(assemblyPath));
        }

        var fullAssemblyPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullAssemblyPath))
        {
            throw new FileNotFoundException($"未找到程序集文件：{fullAssemblyPath}", fullAssemblyPath);
        }

        var portablePdbPath = Path.ChangeExtension(fullAssemblyPath, ".pdb");
        var hasPortablePdb = File.Exists(portablePdbPath);
        var readerParameters = CreateReaderParameters(hasPortablePdb);

        using var assembly = AssemblyDefinition.ReadAssembly(fullAssemblyPath, readerParameters);
        var matches = assembly.MainModule.AssemblyReferences
            .Where(reference => string.Equals(reference.Name, NewtonsoftAssemblyName, StringComparison.Ordinal))
            .ToArray();

        if (matches.Length == 0)
        {
            throw new InvalidOperationException($"程序集未引用 {NewtonsoftAssemblyName}：{fullAssemblyPath}");
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException($"程序集存在多个 {NewtonsoftAssemblyName} 引用，无法安全后处理：{fullAssemblyPath}");
        }

        var reference = matches[0];
        var wasModified = reference.PublicKeyToken.Length > 0 || reference.PublicKey.Length > 0 || reference.HasPublicKey;

        reference.PublicKey = [];
        reference.PublicKeyToken = [];
        reference.Attributes &= ~AssemblyAttributes.PublicKey;

        if (!wasModified)
        {
            return new RewriteResult(fullAssemblyPath, false);
        }

        WriteAssembly(fullAssemblyPath, assembly, hasPortablePdb);
        return new RewriteResult(fullAssemblyPath, true);
    }

    private static ReaderParameters CreateReaderParameters(bool hasPortablePdb)
    {
        if (!hasPortablePdb)
        {
            return new ReaderParameters();
        }

        return new ReaderParameters
        {
            ReadSymbols = true,
            SymbolReaderProvider = new PortablePdbReaderProvider(),
            ThrowIfSymbolsAreNotMatching = true
        };
    }

    private static void WriteAssembly(string originalAssemblyPath, AssemblyDefinition assembly, bool hasPortablePdb)
    {
        var tempAssemblyPath = Path.Combine(
            Path.GetDirectoryName(originalAssemblyPath) ?? throw new InvalidOperationException("程序集目录不能为空。"),
            Path.GetFileNameWithoutExtension(originalAssemblyPath) + ".unity-rewrite" + Path.GetExtension(originalAssemblyPath));
        var tempPdbPath = Path.ChangeExtension(tempAssemblyPath, ".pdb");
        var originalPdbPath = Path.ChangeExtension(originalAssemblyPath, ".pdb");

        try
        {
            var writerParameters = hasPortablePdb
                ? new WriterParameters
                {
                    WriteSymbols = true,
                    SymbolWriterProvider = new PortablePdbWriterProvider()
                }
                : new WriterParameters();

            assembly.Write(tempAssemblyPath, writerParameters);
            File.Move(tempAssemblyPath, originalAssemblyPath, overwrite: true);

            if (!hasPortablePdb)
            {
                return;
            }

            File.Move(tempPdbPath, originalPdbPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempAssemblyPath))
            {
                File.Delete(tempAssemblyPath);
            }

            if (File.Exists(tempPdbPath))
            {
                File.Delete(tempPdbPath);
            }
        }
    }
}

/// <summary>
/// 程序集引用后处理结果。
/// </summary>
/// <param name="AssemblyPath">已处理的程序集路径。</param>
/// <param name="WasModified">是否发生实际修改。</param>
public sealed record RewriteResult(string AssemblyPath, bool WasModified);
