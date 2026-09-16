using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ChatBot.Configuration;
using Jellyfin.Plugin.ChatBot.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ChatBot;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // Carry pre-OpenAI (Ollama-only) settings over to the new keys on first load,
        // otherwise the rename would silently reset an existing install to defaults.
        try
        {
            if (Configuration.Migrate())
            {
                SaveConfiguration();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to migrate ChatBot configuration.");
        }
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        // Clear the Jellyfin↔Jellyseerr user mapping cache so permission changes take effect immediately.
        SeerrService.InvalidateUserCache();
        // Drop model cooldowns so a corrected URL/key/model is retried immediately.
        ChatCompletionService.ResetCooldowns();
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "ChatBot";

    public override string Description => "AI chatbot backed by any OpenAI-compatible endpoint, with library search and Jellyseerr integration";

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
