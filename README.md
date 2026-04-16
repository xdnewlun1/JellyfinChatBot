# Jellyfin ChatBot Plugin

**An AI-powered media assistant that lives right inside your Jellyfin server.**

Give your users a smart, conversational way to explore your library, discover what's available, and request what's missing — all without leaving the Jellyfin UI.

---

## What It Does

Jellyfin ChatBot adds a small, unobtrusive chat bubble to the bottom-right corner of your Jellyfin interface. When a user clicks it, they get a conversation with an AI assistant that actually *knows your library*.

Ask it anything:

> "Do we have any Christopher Nolan movies?"

> "Is there a show called Severance?"

> "I'm looking for something like Arrival — what do we have?"

The assistant searches your library in real-time and responds with formatted results complete with poster art, year, and descriptions. If something isn't available and you run Jellyseerr or Overseerr, it can search TMDB and let users submit a request with one click — right from the chat.

---

## Why You'd Want This

### Your users stop asking you for things

No more "hey do we have X?" messages in your group chat. No more walking people through Jellyseerr. The bot handles the lookup and the request flow in a single conversation.

### It actually knows your library

This isn't a generic chatbot bolted onto a media server. It uses Jellyfin's internal library APIs to search your actual content — movies, series, the real data. When it says "yes, we have that," it's because it checked.

### Jellyseerr requests without the learning curve

Not every user on your server knows what Jellyseerr is or how to use it. The chatbot walks them through it naturally: "We don't have that yet — would you like me to request it?" One button click and it's done.

### Runs on your hardware, not someone else's cloud

ChatBot connects to an [Ollama](https://ollama.com) instance that you host. Your conversations, your library data, your models — nothing leaves your network. Pick whatever model fits your hardware: llama3.2 on a modest machine, or something larger if you have the GPU for it.

### Stays out of the way

The chat badge is a small floating button. It hides automatically when anyone is watching something — no popups over your movies, no overlays during playback. It's there when you want it and gone when you don't.

---

## Features

- **Library search** — real-time search across movies and series using Jellyfin's internal APIs
- **Jellyseerr / Overseerr integration** — search TMDB and submit media requests from the chat (optional)
- **Tool-calling architecture** — the LLM decides when to search or request, keeping conversations natural
- **Media result cards** — poster thumbnails, titles, years, and descriptions inline in the chat
- **Playback-aware** — automatically hides during video playback and fullscreen
- **Conversation memory** — chat history persists across page navigations within a session
- **Fully configurable** — model, system prompt, temperature, result limits, and Seerr settings all from the Jellyfin admin dashboard
- **Connection testing** — verify your Ollama setup from the config page before going live
- **Mobile responsive** — works on phones and tablets

---

## Requirements

- **Jellyfin 10.10+**
- **[Ollama](https://ollama.com)** running somewhere your Jellyfin server can reach (same machine, LAN, etc.)
- **Jellyseerr or Overseerr** *(optional)* — only needed if you want the request feature

---

## Installation

1. Build the plugin:
   ```bash
   dotnet build -c Release
   ```

2. Copy the DLL to your Jellyfin plugins directory:
   ```bash
   mkdir -p /var/lib/jellyfin/plugins/ChatBot
   cp Jellyfin.Plugin.ChatBot/bin/Release/net8.0/Jellyfin.Plugin.ChatBot.dll \
      /var/lib/jellyfin/plugins/ChatBot/
   ```

3. Restart Jellyfin.

4. Go to **Dashboard > Plugins > ChatBot** and configure:
   - Your Ollama URL (default: `http://localhost:11434`)
   - Which model to use (default: `llama3.2`)
   - Optionally enable Jellyseerr and provide the URL + API key

5. Click **Test Ollama Connection** to verify everything works.

That's it. The chat badge will appear for logged-in users on every page.

---

## Configuration Options

| Setting | Default | Description |
|---|---|---|
| Ollama URL | `http://localhost:11434` | Where your Ollama instance is running |
| Ollama Model | `llama3.2` | Any model you've pulled in Ollama |
| System Prompt | *(built-in)* | Customize the assistant's personality and behavior |
| Temperature | `0.7` | Lower = more focused, higher = more creative |
| Seerr Enabled | `false` | Toggle Jellyseerr/Overseerr integration |
| Seerr URL | — | Your Jellyseerr/Overseerr address |
| Seerr API Key | — | Found in Seerr under Settings > General |
| Max Conversation Turns | `20` | How much context to keep (affects model memory usage) |
| Search Result Limit | `10` | Max results returned per library search |

---

## How It Works Under the Hood

The plugin registers API endpoints inside Jellyfin and injects a lightweight JavaScript chat widget into the web UI on server startup.

When a user sends a message, the backend prepends your system prompt, attaches tool definitions (library search, Seerr search, media request), and forwards the conversation to Ollama. If the model decides to call a tool — say, searching your library — the backend executes that search against Jellyfin's `ILibraryManager`, feeds the results back to the model, and lets it compose a natural-language response. This loop can chain multiple tools in a single turn (e.g., search library, find nothing, search Seerr, offer to request).

The frontend is a self-contained IIFE with no framework dependencies. It persists conversations in `sessionStorage`, auto-hides during playback by monitoring for active `<video>` elements and fullscreen state, and authenticates using the existing Jellyfin session token.

---

## Privacy

All chat processing happens between your Jellyfin server and your Ollama instance. No data is sent to external AI services. The only external call is to TMDB (via Jellyseerr) if you enable the request feature — which is how Jellyseerr already works.

---

## License

MIT
