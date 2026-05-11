using System.Net.Http;
using System.Runtime.InteropServices;

namespace DevHub.Sdk.Internal;

internal static class CompatibilityGuards
{
    internal static void ThrowIfNull(object? value, string paramName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    internal static void ThrowIfNullOrWhiteSpace(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("参数不能为空。", paramName);
        }
    }

    internal static void ThrowIfDisposed(bool disposed, object instance)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(instance.GetType().FullName);
        }
    }
}

internal static class CompatibilityIo
{
    internal static async Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync().ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return content;
    }

    internal static async Task<string> ReadAsStringAsync(HttpContent content, CancellationToken cancellationToken)
    {
        CompatibilityGuards.ThrowIfNull(content, nameof(content));
        cancellationToken.ThrowIfCancellationRequested();

        var body = await content.ReadAsStringAsync().WaitAsyncCompat(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return body;
    }
}

internal static class CompatibilityPlatform
{
    internal static bool IsWindows()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }

    internal static bool IsMacOS()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    }
}

internal static class CompatibilityPath
{
    internal static bool IsPathFullyQualified(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        if (path[0] == '/')
        {
            return true;
        }

        return path.Length >= 3
            && path[1] == ':'
            && IsDirectorySeparator(path[2]);
    }

    private static bool IsDirectorySeparator(char value)
    {
        return value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
    }
}

internal static class TaskCompatibilityExtensions
{
    internal static async Task<T> WaitAsyncCompat<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        CompatibilityGuards.ThrowIfNull(task, nameof(task));

        if (task.IsCompleted || !cancellationToken.CanBeCanceled)
        {
            return await task.ConfigureAwait(false);
        }

        var cancellationTask = Task.Delay(Timeout.Infinite, cancellationToken);
        var completedTask = await Task.WhenAny(task, cancellationTask).ConfigureAwait(false);
        if (completedTask == task)
        {
            return await task.ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
    }
}
