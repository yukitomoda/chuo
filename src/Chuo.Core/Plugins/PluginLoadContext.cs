using System.Reflection;
using System.Runtime.Loader;

namespace Chuo.Core.Plugins;

/// <summary>
/// プラグインごとの collectible な ALC。ホストと共有するのは Chuo.Abstractions と Microsoft.Extensions.* に限り、
/// それ以外の依存はプラグインのフォルダから、この ALC の中でだけ解決する（要件定義書 3.1）。
/// </summary>
internal sealed class PluginLoadContext(string pluginId, string entryPath)
    : AssemblyLoadContext($"plugin:{pluginId}", isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryPath);

    public static bool IsShared(AssemblyName name) =>
        name.Name is { } n && (n == "Chuo.Abstractions" || n.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // null を返すと既定の ALC（ホスト）で解決される
        if (IsShared(assemblyName))
        {
            return null;
        }
        return _resolver.ResolveAssemblyToPath(assemblyName) is { } path ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
        _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : 0;
}
