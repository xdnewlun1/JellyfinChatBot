using System;
using System.Collections.Generic;
using System.Security.Claims;
using Jellyfin.Plugin.ChatBot.Api.Models;
using Jellyfin.Plugin.ChatBot.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ChatBot.Api;

[ApiController]
[Route("ChatBot/Search")]
[Authorize]
public class LibrarySearchController : ControllerBase
{
    private readonly LibrarySearchService _librarySearchService;

    public LibrarySearchController(LibrarySearchService librarySearchService)
    {
        _librarySearchService = librarySearchService;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<List<LibrarySearchResult>> Search(
        [FromQuery] string query,
        [FromQuery] string? type = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
        {
            return BadRequest("Invalid search query.");
        }

        // Validate type parameter if provided
        if (type != null && type != "movie" && type != "series")
        {
            return BadRequest("Invalid type. Must be 'movie' or 'series'.");
        }

        var raw = User.FindFirstValue("Jellyfin-UserId")
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("userId");
        if (!Guid.TryParse(raw, out var userId) || userId == Guid.Empty)
        {
            return Forbid();
        }

        var results = _librarySearchService.Search(userId, query, type);
        return Ok(results);
    }
}
