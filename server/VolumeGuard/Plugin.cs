using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.VolumeGuard;

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin Instance { get; private set; } = null!;
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override string Name => "Volume Guard";
    public override string Description => "Shared volume profiles and night schedules for the Volume Guard Android TV companion. Audio processing runs in the companion player.";
    public override Guid Id => new("c49aef32-6c2e-462c-a5ee-40e18c8d28af");
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "volumeguard", EmbeddedResourcePath = GetType().Namespace + ".Web.config.html" }];
    internal static readonly object ConfigurationLock = new();
}
