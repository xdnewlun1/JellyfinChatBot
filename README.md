# Jellyfin ChatBot Plugin

**An AI-powered media assistant that lives right inside your Jellyfin server.**

Give your users a smart, conversational way to explore your library, get personalized recommendations, discover new content, and request what's missing — all without leaving the Jellyfin UI.

---

## What It Does

Jellyfin ChatBot adds a small, unobtrusive chat bubble to the bottom-right corner of your Jellyfin interface. When a user clicks it, they get a conversation with an AI assistant that actually *knows your library* — and knows what you've been watching.

Ask it anything:

> "Do we have any Christopher Nolan movies?"

> "Recommend me a sad movie"

> "What should I watch based on my history?"

> "Something like Interstellar?"

> "Find me highly rated sci-fi from the last 5 years"

The assistant searches your library in real-time, checks your watch history for personalized recommendations, discovers content on TMDB, and responds with formatted results complete with poster art, year, and descriptions. If something isn't available and you run Jellyseerr or Overseerr, it can let users submit a request with one click — right from the chat.

---

## Why You'd Want This

### Your users stop asking you for things

No more "hey do we have X?" messages in your group chat. No more walking people through Jellyseerr. The bot handles the lookup and the request flow in a single conversation.

### It actually knows your library

This isn't a generic chatbot bolted onto a media server. It uses Jellyfin's internal library APIs to search your actual content — movies, series, the real data. When it says "yes, we have that," it's because it checked.

### Personalized recommendations

The bot can look at a user's watch history, analyze their genre and rating patterns, and suggest content they'll actually enjoy — from your library first, then from TMDB for things they could request.

### Jellyseerr requests without the learning curve

Not every user on your server knows what Jellyseerr is or how to use it. The chatbot walks them through it naturally: "We don't have that yet — would you like me to request it?" One button click and it's done.

### Runs on your hardware, not someone else's cloud

ChatBot talks to any OpenAI-compatible `/chat/completions` endpoint — [Ollama](https://ollama.com), LM Studio, llama.cpp, vLLM, LiteLLM, or a hosted provider. Point it at a local model and nothing leaves your network (except TMDB API calls if you enable that feature); point it at a hosted one if you'd rather not run the hardware. Pick whatever model fits: llama3.2 on a modest machine, or something larger if you have the GPU for it.

You can also list **fallback models**. If the primary is down, out of VRAM, or rate-limited, the plugin walks the list in order rather than handing the user an error.

### Stays out of the way

The chat badge is a small floating button. It hides automatically when anyone is watching something — no popups over your movies, no overlays during playback. It's there when you want it and gone when you don't.

---

## Features

- **Library search** — real-time search across movies and series with filters for genre, year range, tags, and community rating
- **Watch history analysis** — the bot sees what you've watched to make personalized recommendations
- **TMDB discovery** — discover movies and TV shows by genre, year, rating, and popularity via TMDB (optional, requires free API key)
- **"Similar to" recommendations** — ask for content similar to a specific title and get TMDB recommendations
- **Jellyseerr / Overseerr integration** — search TMDB and submit media requests from the chat (optional)
- **Tool-calling architecture** — the LLM decides when to search, check history, or discover, keeping conversations natural
- **Media result cards** — poster thumbnails, titles, years, and descriptions inline in the chat
- **Playback-aware** — automatically hides during video playback and fullscreen
- **Conversation memory** — chat history persists across page navigations within a session
- **Fully configurable** — model, system prompt, temperature, result limits, TMDB, and Seerr settings all from the Jellyfin admin dashboard
- **Any OpenAI-compatible backend** — Ollama, LM Studio, llama.cpp, vLLM, LiteLLM, OpenAI, or anything else speaking the same API
- **Model fallback chain** — configure a primary model plus ordered fallbacks; a failing model is skipped for 60 seconds
- **Connection testing** — verify your endpoint and model chain from the config page before going live
- **Mobile responsive** — works on phones and tablets

---

## Requirements

- **Jellyfin 10.10+**
- **An OpenAI-compatible chat endpoint** your Jellyfin server can reach — e.g. [Ollama](https://ollama.com) (its `/v1` API), LM Studio, llama.cpp, vLLM, LiteLLM, or a hosted provider
- **[TMDB API key](https://www.themoviedb.org/settings/api)** *(optional)* — free, enables discovery and recommendation tools
- **Jellyseerr or Overseerr** *(optional)* — only needed if you want the request feature

---

## Installation

### Via Plugin Manifest (Recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Add a new repository with this manifest URL:
   ```
   https://github.com/xdnewlun1/JellyfinChatBot/releases/download/manifest/manifest.json
   ```
3. Go to **Catalog**, find **ChatBot**, and install it
4. Restart Jellyfin

### Manual Installation

1. Download the latest release zip from [Releases](https://github.com/xdnewlun1/JellyfinChatBot/releases)
2. Extract the DLL to your Jellyfin plugins directory:
   ```bash
   mkdir -p /var/lib/jellyfin/plugins/ChatBot
   unzip chatbot-*.zip -d /var/lib/jellyfin/plugins/ChatBot/
   ```
3. Restart Jellyfin

### Building from Source

```bash
dotnet build -c Release
cp Jellyfin.Plugin.ChatBot/bin/Release/net8.0/Jellyfin.Plugin.ChatBot.dll \
   /var/lib/jellyfin/plugins/ChatBot/
```

### Setup

1. Go to **Dashboard > Plugins > ChatBot** and configure:
   - Your API base URL (default: `http://localhost:11434/v1`)
   - An API key, if your backend needs one (local backends usually don't)
   - Which model to use (default: `llama3.2`), plus any fallback models
   - Optionally enable TMDB and provide your API key
   - Optionally enable Jellyseerr and provide the URL + API key

2. Click **Test AI Connection** to verify everything works.

That's it. The chat badge will appear for logged-in users on every page.

---

## Configuration Options

| Setting | Default | Description |
|---|---|---|
| API Base URL | `http://localhost:11434/v1` | Base URL of your OpenAI-compatible endpoint. A bare host with no path gets `/v1` appended |
| API Key | — | Sent as `Authorization: Bearer`. Leave empty for local backends |
| Model | `llama3.2` | Primary model to use |
| Fallback Models | — | Comma or newline separated, tried in order when the primary fails |
| Request Timeout (seconds) | `120` | How long to wait for one model before falling back to the next |
| System Prompt | *(built-in)* | Customize the assistant's personality and behavior |
| Temperature | `0.7` | Lower = more focused, higher = more creative |
| TMDB Enabled | `false` | Toggle TMDB discovery and recommendation tools |
| TMDB API Key | — | Free key from [themoviedb.org](https://www.themoviedb.org/settings/api) |
| Seerr Enabled | `false` | Toggle Jellyseerr/Overseerr integration |
| Seerr URL | — | Your Jellyseerr/Overseerr address |
| Seerr API Key | — | Found in Seerr under Settings > General |
| Max Conversation Turns | `20` | How much context to keep (affects model memory usage) |
| Search Result Limit | `10` | Max results returned per library search |

---

## LLM Tools

The assistant has access to these tools, which it calls automatically based on the conversation:

| Tool | Requires | Description |
|---|---|---|
| `search_library` | — | Search Jellyfin library with filters: query, genre, year range, tags, community rating |
| `list_genres` | — | List all genres in the library |
| `get_watch_history` | — | Get user's recently watched items with genres and ratings |
| `discover_tmdb` | TMDB | Discover movies/TV by genre, year, rating, popularity |
| `get_tmdb_recommendations` | TMDB | Get "similar to X" recommendations for a specific title |
| `search_seerr` | Seerr | Search TMDB via Jellyseerr for requestable titles |

---

## How It Works Under the Hood

The plugin registers API endpoints inside Jellyfin and injects a lightweight JavaScript chat widget into the web UI on server startup.

When a user sends a message, the backend prepends your system prompt, attaches tool definitions, and POSTs the conversation to your endpoint's `/chat/completions`. If the model decides to call a tool — say, searching your library or checking watch history — the backend executes that against Jellyfin's internal APIs, feeds the results back to the model, and lets it compose a natural-language response. This loop can chain multiple tools in a single turn (e.g., check watch history, discover on TMDB, search library for availability, make a recommendation).

Model selection walks the chain you configured: the primary model first, then each fallback in order. A model that times out or errors is put on a 60-second cooldown so the next chat turn doesn't pay the same timeout again. Once a model answers, the rest of that turn's tool rounds stay pinned to it.

The frontend is a self-contained IIFE with no framework dependencies. It persists conversations in `sessionStorage`, auto-hides during playback by monitoring for active `<video>` elements and fullscreen state, and authenticates using the existing Jellyfin session token.

---

## Privacy

Chat processing happens between your Jellyfin server and whatever endpoint you configure. Point it at a local backend and no data leaves your network; point it at a hosted provider and conversations — including library titles the bot looks up — are sent to that provider, so choose accordingly. Other external calls are:

- **TMDB API** — if you enable TMDB discovery (genre/title lookups, no user data sent)
- **Jellyseerr/TMDB** — if you enable the request feature (this is how Jellyseerr already works)

---

## License

MIT
