using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ChatBot;

public class StartupService : IHostedService
{
    private const string InjectionMarker = "<!-- CHATBOT_PLUGIN -->";
    private const string InjectionBlock = @"
<!-- CHATBOT_PLUGIN -->
<link rel=""stylesheet"" href=""/ChatBot/Widget/chatbot.css"">
<script src=""/ChatBot/Widget/chatbot.js"" defer></script>
<!-- /CHATBOT_PLUGIN -->";

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<StartupService> _logger;

    public StartupService(IApplicationPaths appPaths, ILogger<StartupService> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            InjectWidget();
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogInformation(
                "ChatBot cannot write to index.html (read-only). Use the JavaScript Injector plugin to load " +
                "/ChatBot/Widget/chatbot.js and /ChatBot/Widget/chatbot.css, or run Jellyfin with write access to its web folder.");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "ChatBot could not modify index.html. Use JavaScript Injector as a fallback.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to inject ChatBot widget into index.html");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void InjectWidget()
    {
        var webPath = _appPaths.WebPath;
        if (string.IsNullOrEmpty(webPath))
        {
            _logger.LogWarning("WebPath is not set, cannot inject ChatBot widget");
            return;
        }

        var indexPath = Path.Combine(webPath, "index.html");
        if (!File.Exists(indexPath))
        {
            _logger.LogWarning("index.html not found at {Path}", indexPath);
            return;
        }

        var content = File.ReadAllText(indexPath);

        if (content.Contains(InjectionMarker, StringComparison.Ordinal))
        {
            _logger.LogInformation("ChatBot widget already injected into index.html");
            return;
        }

        // Inject before </body>
        var bodyCloseIndex = content.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyCloseIndex < 0)
        {
            _logger.LogWarning("Could not find </body> tag in index.html");
            return;
        }

        content = content.Insert(bodyCloseIndex, InjectionBlock + Environment.NewLine);
        File.WriteAllText(indexPath, content);

        _logger.LogInformation("ChatBot widget injected into index.html successfully");
    }
}
