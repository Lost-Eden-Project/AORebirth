# Chat module API

ChatEngine loads external modules: trusted .NET assemblies that run in the chat process. They are meant for relaying
channels outside the game, for example to Discord or IRC, and back. A module builds against
`AORebirth.ChatModules.Abstractions` (`AORebirth/Libraries/Source/AORebirth.ChatModules.Abstractions`). That assembly
is the whole contract and holds no ChatEngine type. It is separate from the zone module API
([ZONE_MODULE_API.md](ZONE_MODULE_API.md)).

The current version is **1.0**. The major version of the Abstractions `AssemblyVersion` is the contract. ChatEngine
refuses a module built against a different major version, and minor versions only add members.

## Loading

`Config/ChatModules.json` sits beside the GameData tree, or beside the process:

```json
{
  "Enabled": ["DiscordRelay"],
  "Required": [],
  "Directory": "ChatModules",
  "Capabilities": { "DiscordRelay": ["read-channels", "post-channels"] }
}
```

- Each module lives in `ChatModules/<Id>/`, with a `module.json` file (`Id`, `Assembly`, `Version`). It loads into its
  own load context, and the Abstractions assembly is shared.
- Every public `IChatModule` gets `Initialize(IChatModuleContext)` before the chat listener opens, and `Shutdown()` at
  exit. There is no hot reload and no unloading.
- If a module fails to load, it is skipped and logged. If it is also listed in `Required`, chat startup fails.
- `context.GetConfig<T>()` reads `ChatModules/<Id>/config.json`, for example the channels to relay and a webhook URL.

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

## Capabilities

The `Capabilities` entry is optional. A module with no entry may use everything. A module with an entry may use only
what it lists; any other member throws `UnauthorizedAccessException`, and the engine logs a warning the first time.

| Capability | Grants |
|---|---|
| `read-channels` | subscribing to `MessagePosted` |
| `post-channels` | `Post` |
