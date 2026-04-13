using System.Globalization;

namespace DevHub.Sdk.UnityPublish;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("用法：DevHub.Sdk.UnityPublish <已 publish 的 DevHub.Sdk.dll 路径>");
                return 1;
            }

            var result = UnityPublishAssemblyRewriter.RewriteNewtonsoftReference(args[0]);
            Console.WriteLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "已处理程序集引用：{0}（已修改={1}）",
                    result.AssemblyPath,
                    result.WasModified));
            return 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
