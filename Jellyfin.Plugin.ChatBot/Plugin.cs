using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ChatBot.Configuration;
using Jellyfin.Plugin.ChatBot.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ChatBot;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        // Clear the Jellyfin↔Jellyseerr user mapping cache so permission changes take effect immediately.
        SeerrService.InvalidateUserCache();
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "ChatBot";

    public override string Description => "AI chatbot powered by Ollama with library search and Jellyseerr integration";

    public override Guid Id => new Guid("a5b6c7d8-e9f0-4a1b-8c2d-3e4f5a6b7c8d");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "chatbot-config",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        };
    }
}
