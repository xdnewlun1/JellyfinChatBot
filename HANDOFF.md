# ChatBot Plugin — Session Handoff

Jellyfin plugin named "ChatBot" (codename **Cthuwu**) that embeds an Ollama-powered chat widget into the Jellyfin web UI, with library search + Jellyseerr request integration.

## Repo layout

```
Jellyfin.Plugin.ChatBot/
  Plugin.cs                      # BasePlugin; GUID a5b6c7d8-e9f0-4a1b-8c2d-3e4f5a6b7c8d
  PluginServiceRegistrator.cs    # DI registrations (services + hosted StartupService)
  StartupService.cs              # Writes <script>/<link> into jellyfin-web/index.html (often fails on locked-down installs — fallback is JS Injector)
  Configuration/
    PluginConfiguration.cs       # Ollama + Seerr settings, system prompt, limits
    configPage.html              # Plugin admin page. Inline <script> MUST be inside #chatbot-config-page div (see Gotchas)
  Web/
    chatbot.js                   # Widget (fetches via ApiClient; badge, panel, request modal, library card click-through)
    chatbot.css                  # Eldritch purple/teal theme. Vars at :root (--cthuwu-primary etc.)
  Api/
    ChatController.cs            # POST /ChatBot/Chat — main chat endpoint + tool dispatch + rate limit
    LibrarySearchController.cs   # GET /ChatBot/Search — admin/direct endpoint
    SeerrController.cs           # Seerr: TestConnection, RequestOptions, Request, Search
    WidgetController.cs          # Serves chatbot.js / chatbot.css as embedded resources
    Models/
      ChatRequest.cs / ChatResponse.cs / ChatMessage.cs
      LibrarySearchResult.cs
      SeerrRequestDto.cs / SeerrSearchResult.cs
  Services/
    OllamaService.cs             # /api/chat + tool-calling loop; /api/tags for model list
    LibrarySearchService.cs      # ILibraryManager wrapper. Uses reflection for User (see Gotchas)
    SeerrService.cs              # Jellyseerr client. X-Api-User acting-as flow, MemoryCache for user mapping
    RateLimiter.cs               # In-proc per-user token bucket keyed on Jellyfin GUID
build.yaml                       # Jellyfin manifest metadata, targetAbi
sec_findings.md                  # Pen-test report (already addressed)
```

## Build & deploy

```
dotnet build -c Release
```

DLL lands at `Jellyfin.Plugin.ChatBot/bin/Release/net8.0/Jellyfin.Plugin.ChatBot.dll`. Deploy by copying to the user's Jellyfin plugins dir (usually `/var/lib/jellyfin/plugins/ChatBot_*/`) and restarting Jellyfin. `configPage.html`, `chatbot.js`, `chatbot.css` are embedded resources — *any* edit requires rebuild + redeploy + hard-refresh.

The user runs Jellyfin on a remote machine; do not assume filesystem access to `jellyfin-web/` or plugins dir from this workspace. They deploy DLLs themselves.

## Runtime topology

- User → Jellyfin web UI (chatbot widget loaded via JS Injector plugin, snippet:  `<link rel="stylesheet" href="/ChatBot/Widget/chatbot.css"><script src="/ChatBot/Widget/chatbot.js" defer></script>`).
- Widget → `/ChatBot/Chat` → Ollama (server-side fetch to configured Ollama URL, default `http://localhost:11434`).
- Request flow: widget → `/ChatBot/Seerr/RequestOptions` (loads modal) → `/ChatBot/Seerr/Request` → Jellyseerr (server-side; Jellyseerr is behind proxy, Jellyfin reaches it via internal hostname).

The LLM has two tools: `search_library`, `list_genres`, and `search_seerr`. Notably there is **no** `request_media` tool — requests must be user-initiated via the UI button. This is a deliberate prompt-injection guard (comment in `ChatController.BuildTools`).

## Permissions model

- Library search is scoped to the caller's Jellyfin user via `InternalItemsQuery.User`, so parental controls / per-user library ACLs apply. Resolved in `LibrarySearchService.ResolveUser` via reflection.
- Jellyseerr requests use the admin `SeerrApiKey` but send `X-Api-User: <mapped-seerr-user-id>` so Jellyseerr applies the user's own quotas / approval rules.
- Mapping: `SeerrService.ResolveSeerrUserIdAsync` pages `/api/v1/user` looking for `jellyfinUserId` match. Cached in `MemoryCache` (SizeLimit=1024, positive 10m TTL, negative 60s TTL). Cache is cleared by `Plugin.UpdateConfiguration` override.
- If no Jellyseerr user is mapped, the request is rejected — users without a linked Jellyseerr account cannot piggyback on admin auth.

## Gotchas (important, non-obvious)

### 1. Inline `<script>` must be inside `#chatbot-config-page`
Jellyfin's admin dashboard only evaluates scripts contained inside the `data-role="page"` root div. A `<script>` tag after the closing `</div>` is silently skipped. Symptom: Save reloads the page clearing all inputs, Test button does nothing, no console errors. Fix already applied: `configPage.html` closes `</div>` after `</script>`.

### 2. Jellyfin 10.11 moved the `User` type
10.10 has `Jellyfin.Data.Entities.User`; 10.11 has `Jellyfin.Database.Implementations.Entities.User`. The plugin targets Jellyfin.Controller 10.10.6 (see `.csproj`). On 10.11 servers, any static reference to `User` causes `TypeLoadException` at JIT time.

`LibrarySearchService` avoids this by calling `IUserManager.GetUserById` via `MethodInfo.Invoke` and setting `InternalItemsQuery.User` via `PropertyInfo.SetValue`. Do not replace with direct `_userManager.GetUserById(id)` — it will break on 10.11. If you bump the SDK target, you can remove the reflection.

### 3. `GetItemList` vs `GetItemsResult`
`ILibraryManager.GetItemList(InternalItemsQuery)` had a signature change between Jellyfin patches (`List<BaseItem>` → `IReadOnlyList<BaseItem>`), causing `MissingMethodException`. We use `GetItemsResult(query).Items` which has been stable.

### 4. Widget injection
`StartupService.InjectWidget` mutates `jellyfin-web/index.html` in place. On most installs (Docker, Fedora rpm, systemd unit with read-only web path) this throws `UnauthorizedAccessException` — we log and move on. Production fallback is the JavaScript Injector plugin. The user *is* using JS Injector.

### 5. Do **not** add `request_media` as an LLM tool
Prompt injection in media metadata / user input would let an attacker trigger auto-requests. The Request button is user-only, by design. See comment in `ChatController.BuildTools`.

### 6. Seerr date parsing
`releaseDate` / `firstAirDate` can be empty strings. Length-check before substring or it throws `ArgumentOutOfRangeException`.

### 7. TV requests need `seasons`
Bare `{mediaType: "tv", mediaId: N}` to Jellyseerr returns 400. Send `seasons: "all"` or an int array. Handled in `SeerrService.RequestMediaAsync`.

### 8. Browser cache on HTML edits
Embedded HTML is served fresh from the DLL on each restart, but the browser caches aggressively. Always hard-refresh (Ctrl+Shift+R) after deploying.

## Security posture

`sec_findings.md` is a pen-test report. Everything HIGH/MEDIUM and most LOW items are patched:

Patched:
- **HIGH-1** Library ACL scoping via `InternalItemsQuery.User` (reflection).
- **HIGH-2** Per-user token bucket rate limiter (`RateLimiter.cs`).
- **MEDIUM-1** `serverId`/`profileId`/`rootFolder` validated against Jellyseerr's offered set before forwarding.
- **MEDIUM-2** Upstream Seerr error body no longer reflected to client (still logged).
- **MEDIUM-3** Tool error JSON no longer includes `ex.Message`.
- **MEDIUM-4 / LOW-5** `MemoryCache` with negative caching and config-change invalidation.
- **MEDIUM-5** `Math.Clamp` on admin-configurable limits.
- **MEDIUM-6** `nosniff` + `no-store` on widget endpoints.
- **LOW-1** `escapeHtml` covers `'` and `/`.
- **LOW-4** `CreateClient` fails closed when `SeerrEnabled=false`.
- **LOW-6** `sessionStorage` chat history cleared on token change.
- **LOW-8** Queries logged as length, not value.
- **LOW-10** `[RequestSizeLimit]` on state-changing endpoints.

Intentionally deferred (user hasn't asked): LOW-2 (markdown regex hardening), LOW-3 (OllamaApiKey field), LOW-7 (atomic `index.html` write + uninstall hook), LOW-9 (Origin check), LOW-11 (doc-only).

## Features currently shipped

- Chat with library + genre + Seerr search via Ollama.
- Floating "Cthuwu" badge (bottom-right, squid icon, purple gradient) — hidden during video playback.
- Request modal with server / quality profile / root folder dropdowns, TV seasons with "All" master checkbox.
- Clickable library result cards → navigate to `#/details?id=<id>&serverId=<sid>`.
- Config page with Test Ollama + Test Jellyseerr buttons.
- Personality-laden default system prompt (light occult-cute, one flavor-touch per reply).

## Known environment

- User: Xander Newlun, runs `cthuwusecurity` Jellyfin with Jellyseerr behind a reverse proxy at `jellyseerr.home.cthuwusecurity.com`.
- Jellyfin version: 10.11.8 (as of the last session).
- Uses JS Injector + File Transformation plugins for the widget injection path.
- Other active plugins include Jellyfin Enhanced, JellyTag — their log lines frequently interleave with ours.

## When making changes

- UI change? Rebuild → redeploy DLL → hard-refresh. Both `configPage.html` (admin) and `chatbot.js`/`chatbot.css` (widget) live inside the DLL.
- Adding an endpoint? Match existing pattern: `[Authorize]` required, validate inputs, return `BadRequest` with short generic messages, log details server-side.
- New LLM tool? Define in `ChatController.BuildTools` + handle in `ExecuteToolAsync`. Do **not** let it mutate state.
- Config change? Hook `Plugin.UpdateConfiguration` if the change needs cache invalidation or side effects.
- Version compatibility: test against both 10.10 and 10.11 mentally. If you reference anything from `Jellyfin.Data.Entities` directly, it will break on 10.11. Use reflection or bump SDK target.

## Common user requests (history from last session)

- "Test button doesn't work" → always check: script inside page div, DLL actually redeployed, hard-refreshed.
- "Search doesn't find thematic stuff" → `SearchTerm` matches title only. Query manually against overview too (already done).
- "Requests fail with 400" → usually missing `seasons` for TV or Jellyseerr user not linked.
- "No results suddenly" → likely version skew (10.11 `User` type issue).
