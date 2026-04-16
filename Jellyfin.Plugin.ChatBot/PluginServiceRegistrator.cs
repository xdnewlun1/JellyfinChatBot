using Jellyfin.Plugin.ChatBot.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ChatBot;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<OllamaService>();
        serviceCollection.AddSingleton<LibrarySearchService>();
        serviceCollection.AddSingleton<SeerrService>();
        serviceCollection.AddHostedService<StartupService>();
    }
}
