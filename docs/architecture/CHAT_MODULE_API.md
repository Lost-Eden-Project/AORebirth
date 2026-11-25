# Chat module API

ChatEngine loads external modules: trusted .NET assemblies that run in the chat process. They are meant for relaying
channels outside the game, for example to Discord or IRC, and back, and for moderating chat. A module builds against
`AORebirth.ChatModules.Abstractions` (`AORebirth/Libraries/Source/AORebirth.ChatModules.Abstractions`). That assembly
is the whole contract and holds no ChatEngine type. It is separate from the zone module API
([ZONE_MODULE_API.md](ZONE_MODULE_API.md)).

The current version is **1.2** (1.1 adds channel commands; 1.2 adds line filters and players). The major version of the Abstractions `AssemblyVersion` is the contract. ChatEngine
refuses a module built against a different major version, and minor versions only add members.

## Loading

`Config/ChatModules.json` sits beside the GameData tree, or beside the process:

```json
{
  "Enabled": ["DiscordRelay"],
  "Required": [],
  "Directory": "ChatModules",
  "Capabilities": {
    "DiscordRelay": ["read-channels", "post-channels"],
    "LevelBot": ["consume-commands"],
    "ChatModeration": ["filter-chat", "message-players", "consume-commands"]
  }
}
```

- Each module lives in `ChatModules/<Id>/`, with a `module.json` file (`Id`, `Assembly`, `Version`). It loads into its
  own load context, and the Abstractions assembly is shared.
- Every public `IChatModule` gets `Initialize(IChatModuleContext)` before the chat listener opens, and `Shutdown()` at
  exit. There is no hot reload and no unloading.
- If a module fails to load, it is skipped and logged. If it is also listed in `Required`, chat startup fails.
- `context.GetConfig<T>()` reads `ChatModules/<Id>/config.json`, for example the channels to relay and a webhook URL.
- ChatEngine runs with the ASP.NET Core shared framework (1.2), so a module may serve a private HTTP API for the
  website the way zone modules do (a slim `WebApplication` on the private network, `X-Api-Key` on every request).
  Start it in `Initialize`, stop it in `Shutdown`, and reference `Microsoft.AspNetCore.App` as a framework, not a
  copied package.

## Channels

`context.Channels` is safe to use from any thread.

| Member | What it does |
|---|---|
| `List()`, `Find(type, id)` | Snapshots of the channels that exist now: the ChatChannels.json channels, plus organization, team and raid channels while they exist. Type plus id is a channel's identity. |
| `MessagePosted` | Raised for every line that a player, a BotService bot or another module says in a channel. Lines starting with `.` or `/` are commands and are not raised. A module never sees its own posts. |
| `Post(type, id, senderName, text)` | Says `text` in the channel as `senderName`, the way the chat server's bots speak. The line is also written to the chat log and raised to the other modules. Returns a `ChatPostResult`. |

**Delivery.** `MessagePosted` handlers run on the module's own delivery thread, one line at a time and in the order
the engine accepted them. They never run on a network thread, so a handler may do I/O. A slow handler delays only its
own module.
- While a module is 4096 lines behind, newer lines are dropped and the drop is logged.
- A handler that fails 10 times in a row is unsubscribed.

**Posting.**
- Sender names are 1 to 64 characters with no control characters, and text is 1 to 1024 characters.
- Each distinct sender name, ignoring case, keeps one chat identity for the life of the process, up to 65536 names.
- The text is sent exactly as given, AO markup included. A module that relays outside text decides what markup it lets
  through.

## Commands (1.1)

During `Initialize`, a module claims a channel command with `context.Channels.RegisterCommand("!level", handler)`.

```csharp
context.Channels.RegisterCommand("!level", cmd =>
    cmd.ReplyPrivate("LevelBot", "Level " + cmd.Args + " needs ..."));
```

- **Names:** one prefix character, any punctuation or symbol except `.` and `/` (those go to the zone), then 1 to 32
  letters, digits, `-` or `_`. Names are compared ignoring case. A name another module already claimed fails the second
  module's load. Registering after `Initialize` throws.
- **Consumed:** a player's channel line whose first word is a registered name is shown to nobody. The channel, BotService
  and the other modules (including `MessagePosted`) do not see it. It is still written to the chat log. Lines that do not
  match, `!roll` for example, go out as usual, so a module can still answer those with `Post`.
- **Handler:** runs on the module's delivery thread, in order with its `MessagePosted` lines, so it may do I/O. The
  `IChatCommand` it gets has `Channel`, `SenderId`, `SenderName`, `SenderGmLevel` (for permission checks), `Name`,
  `Args` (the rest of the line, trimmed), `Text` and `Timestamp`.
- **`ReplyPrivate(senderName, text)`:** a tell to the issuer alone. It uses the same name and text rules and sender
  identities as `Post`, and returns `RecipientOffline` once the issuer has logged off. A player cannot tell the sender
  back.
- **Failures:** if the handler throws, the issuer gets a system line "`<name>` failed." and the exception is logged; the
  command stays registered. When the module's queue is full, the issuer is told it is busy.
- Only channel lines from players are matched. Vicinity chat, tells, bot posts and module posts never are.

## Line filters (1.2)

During `Initialize`, a module adds a filter with `context.RegisterLineFilter(filter)`. An `IChatLineFilter` sees a
player's line before anyone receives it and returns `ChatFilterResult.Pass`, `ChatFilterResult.Replace(text)` or
`ChatFilterResult.Block(notice)`.

```csharp
context.RegisterLineFilter(new WordFilter(config));   // Filter(ChatLine line) => Pass / Replace / Block
```

- **What is filtered:** every player line in a channel (Global, Shopping, team, raid, organization and the other
  ChatChannels.json channels) and every player say, whisper and shout in vicinity. `ChatLine` has `Scope` (`Channel` or
  `Vicinity`), `Channel` (null for vicinity), `VicinityType` (`Say`, `Whisper`, `Shout`), `SenderId`, `SenderName`,
  `SenderGmLevel`, `Text`, `OriginalText` and `Timestamp`.
- **Not filtered:** chat commands (`.`, `/` and registered module commands), BotService bots, module posts, NPC speech
  and tells.
- **Thread:** `Filter` runs synchronously on the thread delivering the line (a client's network thread for a channel,
  the World link for vicinity). It must be fast and in memory: no I/O, no waiting. A filter slower than 20 ms is logged.
- **Chain:** filters run in module load order. Each sees the previous filter's text in `Text`. The first `Block` ends
  the chain.
- **Replace:** the new text goes to every recipient, BotService and the other modules' `MessagePosted`. It must be 1
  to 1024 characters; anything else is ignored and logged. Channel text is sent as Latin-1, as the client sends it.
- **Block:** nobody receives the line, and no bot or module sees it. A non-empty notice is shown to the speaker as a
  system line.
- **Chat log:** a channel line is logged as said, with `[filtered by <module>]` or `[blocked by <module>]` after the
  sender.
- **Failures:** a filter that throws is logged (the first time and every 1000th) and the line goes on as it was.

## Players (1.2)

`context.Players` is safe from any thread, including inside a filter. BotService bots are never returned.

| Member | What it does |
|---|---|
| `Find(characterId)`, `FindByName(name)` | The online character (`CharacterId`, `Name`, `GmLevel`), or null. Names are compared ignoring case. |
| `SendSystemMessage(characterId, text)` | A system line to one online character. Returns `RecipientOffline` when it is not online. |

## Capabilities

The `Capabilities` entry is optional. A module with no entry may use everything. A module with an entry may use only
what it lists; any other member throws `UnauthorizedAccessException`, and the engine logs a warning the first time.

| Capability | Grants |
|---|---|
| `read-channels` | subscribing to `MessagePosted` |
| `post-channels` | `Post` |
| `consume-commands` | `RegisterCommand` and `ReplyPrivate` (1.1) |
| `filter-chat` | `RegisterLineFilter` (1.2) |
| `message-players` | `Players.SendSystemMessage` (1.2) |
