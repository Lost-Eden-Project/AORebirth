# WorldEngine

WorldEngine is the coordinator every other AORebirth engine connects to. It holds no game simulation and uses no
database. It is the single place that decides:

- **who owns each character.** A character is in play on exactly one zone engine at a time; World hands out that
  ownership (a *lease*) and takes it back.
- **where a login goes.** On character select, World picks the zone engine and issues the cookies the client presents
  there.
- **which zone engine runs each playfield,** when you run more than one.
- **teams,** so a team can have members on different zone engines.
- **routing between engines:** chat between zones and ChatEngine, and GM commands that reach a player on another
  zone engine.

Every server needs it, including a single-zone server. LoginEngine, ChatEngine and ZoneEngine_New all wait for
WorldEngine at startup and will not serve players without it.

## Why it exists

Before WorldEngine, the engines coordinated through files in a shared folder (`AO_REBIRTH_SESSION_OWNERSHIP_DIR`) and
the `Online` column in the database. They talked to each other over ISComV2, a one-way broadcast link. That
worked for one zone engine on one machine. It could not:

- delete a character a disconnected player left behind in the world: the zone could not be asked to save and remove
  it first;
- stop two copies of a character or a playfield existing once there was a second zone engine;
- carry teams, chat or GM commands across zone engines.

WorldEngine replaces the ownership files, the `Online` flag as the source of truth, and ISComV2. The `Online` column
is still written, so tools that display it keep working, but nothing decides ownership from it any more.

## How it fits together

```
                 +-------------------+
   clients --->  |    LoginEngine    | --- select character ---+
                 +-------------------+                          |
                                                                v
                 +-------------------+   leases, routing  +-------------+
   clients --->  | ZoneEngine_New #1 | <----------------> |             |
                 +-------------------+                    | WorldEngine |
                 +-------------------+   leases, routing  |  (gRPC)     |
   clients --->  | ZoneEngine_New #2 | <----------------> |             |
                 +-------------------+                    +-------------+
                 +-------------------+     chat routing          ^
   clients --->  |    ChatEngine     | <-------------------------+
                 +-------------------+
```

- Engines talk to World over gRPC (HTTP/2). Each zone engine and ChatEngine keeps one long-lived connection to it.
  That connection carries heartbeats every 2 seconds and is the engine's proof that it is alive.
- Clients never talk to World. Its ports must not be reachable from the internet.
- World keeps everything in memory. When it restarts, the engines that were connected report back what they still
  hold, and World rebuilds its state from that (see [If World stops](#if-world-stops)).

## Ports

| Port | Default | What uses it |
|---|---|---|
| gRPC | `7600` | LoginEngine, ChatEngine and every zone engine |
| Status page | `7601` | You: `http://127.0.0.1:7601/status` (JSON, answers local requests only) |

ChatEngine no longer listens on `CommPort` (6996). The setting can stay in `Config.xml`; nothing uses it.

## Building

WorldEngine is a normal .NET 10 project:

```
dotnet build AORebirth/Server/WorldEngine/WorldEngine.csproj -c Debug
```

It builds to `AORebirth/Built/Debug/WorldEngine/WorldEngine.exe` (`Release` builds to `Built/Release/WorldEngine/`).
LoginEngine, ChatEngine and ZoneEngine_New reference its shared contracts project
(`AORebirth/Libraries/Source/AORebirth.World.Contracts`), which builds with them.

Check a build without starting it:

```
WorldEngine.exe --validate-startup
```

It prints `WORLDENGINE_STARTUP_VALIDATION_OK` and exits.

## Configuration

WorldEngine reads the same `Config.xml` as the other engines (`AO_REBIRTH_CONFIG_PATH` selects another file). All
settings are optional; leave the section out entirely to use the defaults, which suit a server where every engine
runs on one machine.

```xml
<World>
  <!-- Where World listens. Anything but a loopback address requires AO_REBIRTH_WORLD_TOKEN. -->
  <ListenIP>127.0.0.1</ListenIP>
  <Port>7600</Port>
  <!-- Status page port; -1 turns it off. -->
  <StatusPort>7601</StatusPort>
  <!-- How the other engines reach World. Defaults to http://ListenIP:Port. Set it when World runs on another host. -->
  <Url>http://127.0.0.1:7600</Url>
  <!-- How long a zone engine may go without a heartbeat before it must stop saving characters (seconds). -->
  <LeaseTtlSeconds>15</LeaseTtlSeconds>
  <!-- Extra time World waits before giving a silent engine's characters and playfields to others (seconds). -->
  <LeaseGraceSeconds>10</LeaseGraceSeconds>
  <HeartbeatMilliseconds>2000</HeartbeatMilliseconds>
  <!-- This zone engine's name among several (zone engines only). Defaults to "zone-" plus its ZonePort. -->
  <InstanceName>zone-7501</InstanceName>
</World>
```

Every engine must point at the same World, so keep the `<World>` section identical in each engine's config file
(ChatEngine may use `ChatEngine.Config.xml`).

`ZoneIP` and `ZonePort` matter more than before: World hands each zone engine's `ZoneIP:ZonePort` to clients. `ZoneIP`
must be the address players' clients can reach, not `0.0.0.0`.

### The shared token

Set the same secret in the environment of World and of every engine:

```
AO_REBIRTH_WORLD_TOKEN=<a long random string>
```

Without it, any program on the machine can talk to World as if it were a zone engine. That includes logging into any
character. World starts anyway but logs a warning, and it refuses to listen on a non-loopback address without one.
Set it on any server players use.

### Other environment variables

| Variable | Default | Meaning |
|---|---|---|
| `AO_REBIRTH_LOGIN_HANDOFF_TIMEOUT_SECONDS` | `30` | How long login cookies stay valid (5–120). Shared with LoginEngine's own timer. |
| `AO_REBIRTH_ZONE_INSTANCE` | `zone-<ZonePort>` | Zone engine instance name (same as `--instance`). |
| `AO_REBIRTH_ZONE_PORT` | `ZonePort` | Zone engine game port (same as `--zone-port`). |
| `AO_REBIRTH_DEBUG_MCP_PORT` | `DebugMcp/Port` | Zone engine debug MCP port (same as `--debug-mcp-port`). |

## Starting and stopping

**Start WorldEngine first and stop it last.** Zone engines save and release their characters through World when they
stop, so it has to still be running.

Start order:

1. WorldEngine
2. ChatEngine
3. LoginEngine
4. Each ZoneEngine_New

Stop order is the reverse: zone engines first, then LoginEngine, then ChatEngine, then WorldEngine.

The repository scripts already do this:

- `start-engines.ps1` starts WorldEngine before the others and waits for its ports.
- `stop-engines.ps1` stops WorldEngine last. `-EngineName WorldEngine` stops it alone.

WorldEngine accepts the same lifecycle arguments as the other engines: `/headless`, `/shutdown-file <path>` (it exits
when that file appears) and `/autostart`. Typing `exit` in its console window also stops it.

If an engine starts before World, it waits ("waiting for World at ...") and continues once World is up. A zone engine
does not open its game port until World has accepted it.

After every World start there is a **25-second recovery window**: zone engines that were already running report their
characters back, and new logins are refused until it ends. When you start everything together this passes unnoticed,
because the zone engines take longer than that to load.

## Running more than one zone engine

Run the same `ZoneEngine_New.exe` several times, each with its own name and ports:

```
ZoneEngine_New.exe /autostart --instance zone-7501 --zone-port 7501
ZoneEngine_New.exe /autostart --instance zone-7502 --zone-port 7502 --debug-mcp-port 7591
```

- **Names** must be unique. A second engine with a name already in use exits with an error saying so.
- **Game ports** must be unique and reachable by clients. `ZoneIP` is shared, so the instances run on the same host, or
  each host sets its own `ZoneIP` in its own config.
- **Debug MCP ports** (when DebugMcp is enabled) must differ.
- **Logs:** the instance whose port matches `ZonePort` in the config logs to `ZoneEngine_NewLog.txt`. The others log to
  `ZoneEngine_New.<instance>.Log.txt` beside it.

You do not tell an instance which playfields to run. World assigns each playfield to the least busy instance the first
time a player needs it. It keeps it there while anyone uses it; a playfield left empty long enough to unload may land
on another instance next time. A player crossing into a playfield on another instance is saved, then the client is
redirected there. Buffs keep their remaining time, pets are summoned again beside the player, and teams stay together.

Each instance gets its own block of NPC ids from World, so NPCs and pets on different instances never clash.

## The status page

`http://127.0.0.1:7601/status` shows, as JSON:

- the connected engines: name, connected or not, slot, player count, time since the last heartbeat;
- the zone engine that runs the database-wide sweeps (`dutyHolder`);
- every character lease: who owns it, its playfield, whether it is link-dead;
- which instance runs each playfield;
- pending login tickets and deletes;
- teams and pending invitations.

It answers only requests from the same machine.

## If World stops

- **World restarts quickly** (under about 12 seconds): nothing visible happens. Zone engines reconnect and report their
  characters, playfields and teams, and World takes them back. Logins in progress at that moment fail and the player
  simply logs in again. `.gametime` shifts and pending team invitations are lost.
- **World is down longer:** each zone engine saves every character and logs everyone out about 3 seconds before its
  lease would run out. It cannot be sure World has not given those characters to someone else after that. Players
  log back in once World is back.
- **A zone engine crashes:** after about 25 seconds (lease time plus grace) World frees its characters and playfields.
  Those characters can log in again, on any instance, from their last save.

## Deleting characters

Deleting a character that is still link-dead in the world (the player disconnected, then deleted it at character
select) now works: World asks the zone engine holding it to save and remove it, then the delete goes ahead. A
character someone is actively playing is not deleted; the request is refused without disconnecting the client.

## Linux

WorldEngine runs the same way on Linux; it is a plain .NET 10 executable. When packaging:

- add a service for WorldEngine. It reports readiness to systemd (`Type=notify` works) and should be ordered before
  the zone, login and chat units (`Before=` on World, or `After=`/`Requires=` on the others);
- set `AO_REBIRTH_WORLD_TOKEN` in the environment files of all units;
- for several zone engines, use one templated unit (for example `zoneengine@.service`) passing `--instance` and
  `--zone-port` per instance;
- keep ports 7600/7601 closed to the outside;
- `AO_REBIRTH_SESSION_OWNERSHIP_DIR` is no longer needed by the engines.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| An engine keeps printing "waiting for World at ..." | WorldEngine is not running, or `<World><Url>` / `<Port>` differ between configs. |
| "World refused instance ...: instance_in_use" and the zone engine exits | Another zone engine is already running under that name or port. |
| Logins fail with `world_recovering` | World started less than 25 seconds ago. |
| Logins fail with `no_zone_available` | No zone engine is connected to World (check the status page). |
| Everyone gets logged out with "World service unreachable" | World stopped or hung for more than about 12 seconds. |
| `Unauthenticated` errors in an engine's log | `AO_REBIRTH_WORLD_TOKEN` differs between World and that engine. |

## Where the code is

| Path | What |
|---|---|
| `AORebirth/Server/WorldEngine/` | The service: leases, tickets, playfield assignment, teams, status page |
| `AORebirth/Libraries/Source/AORebirth.World.Contracts/` | The gRPC contract (`Protos/world.proto`) and the clients Login and Chat use |
| `AORebirth/Server/ZoneEngine_New/Core/World/` | A zone engine's side: connection, leases, cross-instance transfer, World commands |
