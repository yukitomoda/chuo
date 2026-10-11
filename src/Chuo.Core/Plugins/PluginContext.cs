using Chuo.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Chuo.Core.Plugins;

internal sealed class PluginContext(string pluginId, string pluginDirectory, IConfiguration configuration) : IPluginContext
{
    public string PluginId { get; } = pluginId;

    public string PluginDirectory { get; } = pluginDirectory;

    public string DataDirectory
    {
        get
        {
            var dir = ChuoPaths.PluginDataDirectory(PluginId);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public IConfiguration Configuration { get; } = configuration;
}
