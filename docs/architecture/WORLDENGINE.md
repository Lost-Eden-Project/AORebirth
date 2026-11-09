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
  <!-- Admin API for adding and removing zone engines; omitted or 0 leaves it off. See "Adding and removing zone
       engines while World runs". -->
  <AdminPort>7602</AdminPort>
  <!-- Where the admin API listens. Defaults to ListenIP. Anything but loopback requires AO_REBIRTH_WORLD_ADMIN_TOKEN. -->
  <AdminListenIP>127.0.0.1</AdminListenIP>
  <ZoneHosts>
    <!-- One per host the admin API may start zone engines on. -->
  </ZoneHosts>
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
| `AO_REBIRTH_WORLD_ADMIN_TOKEN` | none | Bearer token for World's admin API, at least 24 characters. Required when the admin API is not on loopback. Separate from `AO_REBIRTH_WORLD_TOKEN`: it can start and stop zone engines. |
| `AO_REBIRTH_ZONE_STATE_DIR` | temp folder | Where a zone engine keeps its GameData fingerprint cache (`gamedata-fingerprint-*.cache`). |

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

Every instance must run the same build and the same GameData. Each zone engine reports both when it registers: its
build (the version plus a hash of every AORebirth assembly it loads) and a fingerprint of its GameData (a SHA-256
over every file's path, size and content, plus the `AO_REBIRTH_GAMEDATA_OVERRIDE_PATH` and `AO_REBIRTH_KNUBOT_PATH`
folders, skipping `.git`, caches, logs and the other directories the release manifest leaves out). World refuses a
**freshly started** zone whose build (`build_mismatch`) or GameData (`gamedata_mismatch`) differs from a zone already
running, and that zone exits. A zone that was already running and is only re-registering, after a World restart or a
dropped stream, is warned about instead, because refusing it would log its players out.

The first start after a GameData change reads the whole tree (about 3 seconds for 9 GB from a warm disk cache, longer
from a cold disk). Later starts reuse per-file hashes cached by size and modification time, which takes a fraction of
a second. A file edited without changing its size or modification time is not noticed until the cache is deleted. To
change the build or GameData of a running set, stop every zone engine, then start the new ones; World can stay up.

## Adding and removing zone engines while World runs

World does not need a restart to change how many zone engines run. A zone engine started at any time registers, gets
the next free slot and its own block of NPC ids, and World places new playfields on it from then on: it starts with
nothing, so it wins every placement until it is as busy as the others. Playfields already running elsewhere stay where
they are until they empty and unload.

You can start one by hand as in [Running more than one zone engine](#running-more-than-one-zone-engine), or through
World's admin API.

### The admin API

It is plain HTTP/1.1 with JSON, on its own port, and only answers on that port.

```xml
<World>
  <AdminPort>7602</AdminPort>
  <!-- This host's WireGuard address: reachable over the tunnel, not from the internet. -->
  <AdminListenIP>10.8.0.1</AdminListenIP>
  <ZoneHosts>
    <!-- Host 1, where World runs: the zone engine is started through its systemd template unit. -->
    <ZoneHost>
      <Name>host1</Name>
      <Address>203.0.113.10</Address>
      <FirstPort>7502</FirstPort>
      <LastPort>7510</LastPort>
      <Command>sudo</Command>
      <Arguments>-n systemctl start zoneengine@{port}</Arguments>
    </ZoneHost>
    <!-- Host 2: the same unit, started over SSH through the tunnel. -->
    <ZoneHost>
      <Name>host2</Name>
      <Address>203.0.113.20</Address>
      <FirstPort>7501</FirstPort>
      <LastPort>7510</LastPort>
      <Command>ssh</Command>
      <Arguments>-o BatchMode=yes aorebirth@10.8.0.2 sudo -n systemctl start zoneengine@{port}</Arguments>
      <TimeoutSeconds>30</TimeoutSeconds>
    </ZoneHost>
  </ZoneHosts>
</World>
```

- **Token.** Every request carries `Authorization: Bearer <AO_REBIRTH_WORLD_ADMIN_TOKEN>`. World refuses to start
  with a non-loopback `AdminListenIP` and no token. Use a different value from `AO_REBIRTH_WORLD_TOKEN`.
- **Network.** The token travels in plain text, as the World link's does. Bind the admin API to a WireGuard (or other
  private) address and firewall its port to the hosts and operators that need it.
- **ZoneHost fields.**
  - `Name` is what you call the host in requests.
  - `Address` is the `ZoneIP` that host's zones advertise; World uses it to see which ports are taken.
  - `FirstPort`..`LastPort` are the game ports it may use.
  - `FirstDebugMcpPort` is the debug MCP port for `FirstPort`, plus one per later port.
  - `Command`, `Arguments` and `WorkingDirectory` may use `{instance}`, `{port}` and `{debugMcpPort}`. World fills
    them only with the validated instance name and port numbers, never with request text.
  - `Detached` set to `true` means `Command` is the zone engine itself: World starts it and does not wait for it.
    Otherwise `Command` is something that starts the zone and exits (`systemctl`, `ssh`): exit code 0 is success, and
    it is stopped after `TimeoutSeconds` (default 60).
- **What a started zone gets.** It inherits World's environment when World starts it directly. When started by
  systemd or over SSH, it gets that unit's environment and config, which must point at this World with the same
  token, binaries and GameData.
- **Linux.** Do not use `Detached` under systemd: a zone World starts itself lives in World's service group, and
  stopping World stops it. Start the template unit instead. The `systemctl` user needs a sudoers or polkit rule
  limited to `systemctl start zoneengine@*`.

| Request | What it does |
|---|---|
| `GET /admin/zones` | Every zone instance (slot, endpoint, build, GameData, players, playfields, draining, duty holder), recent launches, and the configured hosts. |
| `GET /admin/zones/{name}` | One zone and, when the API started it, its launch. |
| `POST /admin/zones` with `{"host": "host2", "port": 7503, "instance": "host2-7503"}` | Starts a zone engine. Every field is optional: the host when only one is configured, the port (lowest free in the host's range) and the name (`<host>-<port>`). Answers `202` at once. The launch `status` goes `started`, then `registered` once the zone is in World's list. It is `failed`, `exited` or `timed_out` when it did not come up, and `not_registered` after 5 minutes without registering. |
| `POST /admin/zones/{name}/drain` | Takes the zone out of rotation: no new playfields are placed on it; what it already runs keeps running there. The database sweeps move to another zone if there is one. |
| `POST /admin/zones/{name}/resume` | Puts it back in rotation, and cancels a shutdown the API asked for. |
| `DELETE /admin/zones/{name}?seconds=60&message=...` | Removes the zone. It is drained at once, then runs the GM `.shutdown` countdown (`seconds`, 1–86400, default 60; `message` follows each countdown line). It saves everyone and exits. World frees its characters and playfields as for any stopped instance (about 25 s with the default lease); its players log in again on another instance. |

```
curl -H "Authorization: Bearer $AO_REBIRTH_WORLD_ADMIN_TOKEN" http://10.8.0.1:7602/admin/zones
curl -X POST -H "Authorization: Bearer $AO_REBIRTH_WORLD_ADMIN_TOKEN" -H "Content-Type: application/json" \
     -d '{"host":"host2"}' http://10.8.0.1:7602/admin/zones
curl -X DELETE -H "Authorization: Bearer $AO_REBIRTH_WORLD_ADMIN_TOKEN" \
     "http://10.8.0.1:7602/admin/zones/host2-7501?seconds=300&message=Moving%20this%20area%20to%20another%20server"
```

A systemd unit for zones the API removes should use `Restart=on-failure` (not `always`), or systemd starts the zone
again after its requested exit.

## Running zone engines on more than one host

The usual layout is host 1 with WorldEngine, LoginEngine, ChatEngine, MySQL and one zone engine, and host 2 with a
second zone engine. Everything in [Running more than one zone engine](#running-more-than-one-zone-engine) still
applies; this section is what changes when the instances are not on the same machine.

### Network

- World, the database and the status page must only be reachable from the hosts themselves. The World link is
  HTTP/2 without TLS and the token travels as a plain header, so **put the hosts on a private network or a WireGuard
  tunnel** and firewall 7600, 7601 and 3306 to the other host's address. Anyone who can reach 7600 with the token can
  log in as any character.
- World must listen on that private address (`<World><ListenIP>`), not loopback. It refuses to start that way
  without `AO_REBIRTH_WORLD_TOKEN`; generate a long random token and set it identically on both hosts.
- Players must be able to reach **both** hosts' game ports. Login routes a character to whichever instance runs its
  stored playfield, so a player who cannot reach host 2 cannot log in at all while their playfield is there.
- Keep the hosts in the same datacenter. Host 2's zone engine commits to host 1's MySQL synchronously on the
  playfield tick when a player zones, logs out or trades; tens of milliseconds of latency per statement add up.
- Sync both clocks with NTP. Buffs cross instances with absolute UTC expiry times, and skill, nano-line and perk
  locks persist absolute due times; clock skew shortens or lengthens them on arrival.

### Host 2's Config.xml

A copied Config.xml is the most common mistake. These settings differ on host 2:

| Setting | Host 2 value |
|---|---|
| `ZoneIP` | Host 2's **public** address. World hands it to clients and to host 1's zone for transfers. |
| `<World><Url>` | `http://<host 1 private address>:7600` |
| `ChatIP` / `ChatPort` | ChatEngine's **public** address on host 1. Every zone sends its own value to clients. |
| `MysqlConnection` | Host 1's private database address. Add `SslMode=Required` unless the link is already tunnelled. |
| `<World><InstanceName>` or `--instance` | Unique per instance. |
| `AO_REBIRTH_BIND_MODE` | `Public`, otherwise the game port binds loopback. |
| `AO_REBIRTH_WORLD_TOKEN` | The same value as host 1. |
| `AO_REBIRTH_GAMEDATA_PATH` | A byte-identical copy of the accepted GameData. |

World now refuses a zone whose advertised endpoint is `0.0.0.0` or is already advertised by another live zone
(`endpoint_in_use`). It also refuses a freshly started zone whose build or GameData differs from the running zones
(`build_mismatch`, `gamedata_mismatch`). It warns when a zone advertises a loopback endpoint to a World that serves
other hosts. The zone engine itself refuses `ZoneIP` of `0.0.0.0` at startup and warns when its `ZoneIP` is public but
its `ChatIP` is loopback or empty. The GameData check compares hosts with each other, not with the release manifest:
both hosts must still run the accepted GameData manifest, exactly as the Linux and live manifests must match the
accepted Windows one.

### Lease timing

The defaults (15 s lease, 10 s grace, 2 s heartbeat) were tuned for one machine. Between hosts, a network
interruption longer than about 12 s logs every player out of host 2. For a cross-host deployment consider
`LeaseTtlSeconds` 30 and `LeaseGraceSeconds` 15, identical in every engine's config. The costs are a 45 s recovery
window after a World restart, during which logins are refused, and 45 s before a crashed instance's characters can
log in again.

### What is still single-host

- Host 1 is a single point of failure: World, Login, Chat and the database live there. World down for longer than
  the lease fence logs everyone out of host 2. Start World first and stop it last.
- Daily Login claim files are read from the local filesystem (`AO_REBIRTH_DAILY_LOGIN_CLAIMS_ROOTS`). Host 2 only
  sees web claim requests if that setting points at a shared mount. Committed progress lives in the database, so
  nothing is claimed twice; claims made while the character is on host 2 are simply delivered later.
- Each zone engine's debug MCP port is local to its host. Host 2 needs its own tunnel.
- A suspension or ban issued through a zone's GM command is broadcast to the other instances at once. One written
  by LoginEngine, the account broker or directly to the database reaches players on other hosts at the next sweep,
  within 60 s.

### Gameplay differences players will notice

- A move to a playfield on the other instance is a full save, a redirect and a client reconnect: a loading screen
  where the same-instance move is seamless. With two hosts and least-loaded placement, adjacent outdoor playfields
  often sit on different hosts.
- Pets are summoned again beside the player with their remaining time and a proportional share of their health.
  Towers stay behind in both cases, and charms never zone in either case.
- While a cross-instance move is pending (up to 30 s if the client never arrives), logging that character in is
  refused with `character_busy`.
- A login whose dungeon ended, or whose proxy door no longer leads anywhere, is saved at the fallback location and
  disconnected; the player logs in again and is routed to the right instance.

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
| "World refused instance ...: build_mismatch" or "gamedata_mismatch" and the zone engine exits | That zone runs other binaries or GameData than the zones already running. Deploy the same build and GameData, or stop the running zones first. |
| An admin API launch ends `exited` within seconds | The zone failed at startup; its console or log says why. A port it needs (game or debug MCP) is often taken. |
| `401` from the admin API | Missing `Authorization: Bearer` header, or it differs from `AO_REBIRTH_WORLD_ADMIN_TOKEN`. |

## Where the code is

| Path | What |
|---|---|
| `AORebirth/Server/WorldEngine/` | The service: leases, tickets, playfield assignment, teams, status page, admin API (`Services/ZoneAdmin.cs`) |
| `AORebirth/Libraries/Source/AORebirth.World.Contracts/` | The gRPC contract (`Protos/world.proto`) and the clients Login and Chat use |
| `AORebirth/Server/ZoneEngine_New/Core/World/` | A zone engine's side: connection, leases, cross-instance transfer, World commands, build and GameData fingerprint |
