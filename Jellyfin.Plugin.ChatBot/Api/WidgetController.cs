using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ChatBot.Api;

[ApiController]
[Route("ChatBot/Widget")]
public class WidgetController : ControllerBase
{
    [HttpGet("chatbot.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetJavaScript()
    {
        return GetEmbeddedResource("Jellyfin.Plugin.ChatBot.Web.chatbot.js", "application/javascript");
    }

    [HttpGet("chatbot.css")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetStylesheet()
    {
        return GetEmbeddedResource("Jellyfin.Plugin.ChatBot.Web.chatbot.css", "text/css");
    }

    private ActionResult GetEmbeddedResource(string resourceName, string contentType)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream(resourceName);

        if (stream == null)
        {
            return NotFound();
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "no-store";
        return File(stream, contentType);
    }
}
