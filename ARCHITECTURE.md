# StandChillow LAN — project architecture

Goal: **no multi-thousand-line god-files**, clear homes for lobby / discovery / match / modes, so ~15 agents can work in parallel without stepping on each other.

## Top-level layout

| Path | Owns | Notes |
|------|------|--------|
| `server/` | Dedicated LAN server (C#) | Only place that binds UDP / answers clients |
| `server/Startup/` | Dedicated-host and ConnectAsClient startup paths | Entry-point orchestration only |
| `server/Dashboard/` | CLI rendering and console command routing | Shared game commands stay in `GameNetHost` |
| `server/Diagnostics/` | Verbose switch + async capture sink | **Never** do blocking disk I/O from a network hot path |
| `server/Bans/` | Account registry, ban registry, `banlist.json`/`accounts.json` stores, legacy id migration, duration/message parsing | Shared by lobby **and** match; no sockets, no wire |
| `tests/` | Socket-free protocol regression tests | xUnit; run in CI on all desktop OSes |
| `server/Lan/` | Discovery (Bonjour V2 / UDP 5056) | Stay out of match host |
| `server/Net/Lobby/` | Lobby LiteNetLib + codecs | Stay out of match host |
| `server/Net/Match/` | Match codec, world constants, flow rules, **modes** | |
| `server/Net/Match/Host/` | Match host partials (bootstrap, world relay, broadcast) | Split from `GameMatchHost` |
| `server/Net/Match/Kernel/` | Shared clock / future phase machine | Modes call kernel; modes never call modes |
| `server/Net/Match/Modes/<GameModeId>/` | **One folder per mode** | Agent ownership boundary |
| `server/Net/GameMatchHost.cs` | Thin orchestrator (~350 lines) | Partials elsewhere |
| `server/Net/GameMatchClient.cs` | ConnectAsClient learning | Do not paste captures into host TX |
| `decompiled/` | RE dumps, protocol docs, agent maps | Not compiled into server |
| `decompiled/tools/` | RE tooling (Ghidra etc.) | **RE agent owns** — do not fight installs |
| `server/**/captures/`, `logs/` | Runtime captures / run logs | Evidence only; never memcpy into replies |

## Bans and accounts (shared layer)

`server/Bans/` owns the persistent account registry (`accounts.json`), ban records
(`banlist.json`), the seen-player directory and the migration from the old nickname hash.
Lobby and match only **ask** it at three entry points — never on the packet path:

| Gate | Where | Keys |
|------|-------|------|
| Lobby join / lobby reconnect | `GameNetHost.HandleJoin` | nick + IP |
| Match connect | `GameMatchHost.ConnectionRequestEvent` | IP only (no nick yet) |
| Match join / mid-match reconnect | `GameMatchHost.HandleJoinRoom` (before `TryFindRejoinActor`) | userId + IP + account id + nick |

The last gate matters: a mid-match reconnect adopts the old actor and never runs a normal
join, so a ban checked only at the first connect could be bypassed (e.g. leave VPN →
reconnect from the banned address). Admins are exempt at every gate — the ban gate asks for
the same account id that `admin.ids` lists (`IsBanExemptNick`), and banning an admin is
rejected.

`AccountStore` issues permanent numbers at the first successful lobby join: `1_000_000`,
`1_000_001`, … and never reuses one. The account is stored in `accounts.json`; the lobby does
not receive a device key, so the first account is found by nick, and the `LB<md5>` from the
2.07 match handshake is bound on the first match join. The same device under a new nick keeps
the account (the old nick becomes an alias); a new device under a taken nick gets a new
account, so a namesake inherits neither the account nor its ban. `ProbeId(device)` returns an
account id only when that device is already bound — that is why the public id is a hard key
only together with proof of the device.

The host substitutes the account id for the client-authored actor property `uid` (players
send the `Offline` placeholder from the gold capture), so the in-game player list shows the
same number `/ids` and `/ban` use. The Server actor carries the hardcoded string id `Server`
instead — it is outside the account numbering, never bannable, and no client can claim it: the
host overwrites `uid` from session state on every SetProperty/SetProperties and drops the
property when the author cannot be resolved. The server nickname is reserved (lobby and match
reject it, derived from `LobbySession.ServerPlayerName` so a rename moves the reservation).

Admin rights are keyed on the **account number**, never on a configured nickname:
`ServerConfig.AdminConfig.Ids` is the only list, `IsAdmin(int)` is the only test, and the
number comes from session state (the nickname a client sends is untrusted, so
`admin.nicknames` is reported as an unknown key and ignored). Until a person has an account,
`IdOf(nick)` falls back to `LegacyNickHash.ForNick`, and `MigrateAdminIds` replaces the old
hash with the account number in memory on startup and after every new account; the config file
is not rewritten. Roles, permissions, mega IPs and password hashes live in `auth/admins.json`,
never in the config. The id alone is still not enough: an admin id gets a shadow lobby until
the address enters the **personal password** from `auth/<id>/password.txt` (only its SHA-256 is
stored, the file is 600). Authorized `(id, IP)` pairs live in memory, and a password change
bumps `authVersion`, killing them and every web session. The match host and the command
dispatcher both consult `IsUnauthorizedAdmin`, so a shadowed admin cannot drop or sneak into
7777 either. A new admin gets `dashboard` and `journal`; operational permissions are granted
by a mega admin — an admin whose request IP is in his `megaIps` — and the first mega admin is
added only from the owner's console.

`PlayerDirectory` persists `account ↔ nick ↔ IP ↔ device` in `banlist.json` so `/ban` still
resolves a player who already left. The device is the stable `LB<md5>` part of the 2.07
handshake token; it survives a nick change and VPN. Ban keys are hard (`userId`, IP, account
id with a bound device) and soft (`nick`): a hard match bans and learns the other keys, a
nick-only match bans that attempt and learns nothing, so an innocent player who picks a banned
nickname does not inherit the ban. `admin.ban.byDevice` turns the userId key off;
`admin.ban.bindAliases` turns learning off while bans keep working. `banlist.json` lives beside
`config.json` and is git-ignored; v1/v2 are backed up to `banlist.json.vN.bak` and reset to v3
on first start (old bans dropped, directory kept without old IPs/devices). v3 → v4 then keeps
every record but clears `MatchIds` once: account numbers are being renumbered, and an old
number may now belong to someone else. IP, devices, nicks and aliases stay, so a cheater is
still caught by hardware or address; bans issued after v4 store the account id again.
`accounts.json` is the separate account registry (`version`, `nextId`, `accounts`); its one-time
v1 → v2 renumber compacts ids from `1_000_000` with the owner/admin first and saves an old→new
map that drives directory, custom ids, bans, stats, history, reports and avatars. The map is
deleted after every step is written.

## Match host split (was 2568-line god-file)

`GameMatchHost` is a **partial class**:

| File | Role |
|------|------|
| `server/Net/GameMatchHost.cs` | Bind, poll, opcode dispatch, rate-limit |
| `Host/MatchRoom.cs`, `MatchPeerState.cs`, `MatchLivingPawn.cs` | Room / peer types |
| `Host/GameMatchHost.HandshakeJoin.cs` | Handshake + JoinRoom Found |
| `Host/GameMatchHost.Bootstrap.cs` | Joiner gate + managers + C2=10 + snapshots |
| `Host/GameMatchHost.Properties.cs` | SetProperty / SetProperties |
| `Host/GameMatchHost.WorldObjects.cs` | CWO / Destroy / Rpc / State (drop `exceptSender` lives here) |
| `Host/GameMatchHost.Broadcast.cs` | Broadcast / stime / Send / DumpCapture |
| `Modes/Ranked2v2/GameMatchHost.Ranked2v2*.cs` | Allies phase + RoundEnd gold path |

**Target size:** hundreds of lines per file, not thousands.

## Modes

Nine `GameModeId`s (probe `run-20260722_213859`) each have `server/Net/Match/Modes/<Id>/`:

- `Ranked2v2` — implemented (current LAN default / Allies)
- Others — stub `*Mode.cs` that log unimplemented and **refuse to drive unknown wire**

Registry: `Modes/MatchModeRegistry.cs`. Interface: `IMatchMode` — modes must not cross-import.

Bomb-family (similar phase skeleton later): RankedDefuse, Ranked2v2, Ranked2v2Alt, Defuse, Escalation — share **kernel interfaces**, separate implementations.

## Performance rules (hot paths)

These are the invariants that keep a 10+ player room smooth; break them and the host melts:

1. **No blocking I/O inside packet handlers.** No `File.*`, no `Console.WriteLine` on the
   per-packet path (it is a synchronous write to terminal + 2 logs). Use
   `Diagnostics.LogVerbose` or per-event logs only. Diagnostics go through
   `CaptureSink` / `RunLog`, which are async + bounded.
2. **Never let one peer's queue grow without bound.** All sends go through
   `GameMatchHost.Send*`, which checks `GetPacketsCountInReliableQueue` and drops game ticks
   for slow peers. Don't call `peer.Send` directly.
3. **A broadcast is a snapshot loop.** Iterate `_peers` once per payload; do not nest
   per-recipient loops inside per-payload loops.
4. **Health line is the first place to look:** `[health] peers=… reliableQueue=… slowPeers=…
   droppedSends=… rttMax=… captures=…` every 10 s.

## Protocol fidelity

- Do not invent opcodes, fields, auth strings, or discovery packets
- Do not paste capture blobs into dedicated host replies
- Evidence → decode → codec builders
- Ranked2v2 gold RoundEnd (`C2=101` + WinTeam) and WeaponDrop `exceptSender` must not regress

## Docs map

| Doc | Purpose |
|-----|---------|
| This file | Repo layout + conventions |
| `decompiled/AGENT_SWARM.md` | 15-agent ownership map |
| `decompiled/GAME_MODES.md` | Mode × map catalog |
| `decompiled/MATCH_WORLD.md` | Wire / init / RoundEnd evidence (behavioral) |
| `decompiled/LAN_DISCOVERY_PROTOCOL.md` | UDP 5056 |
| `decompiled/LAN_LOBBY_PROTOCOL.md` | Lobby |

## Convention: where does new code go?

1. New mode win rule / phase → `Modes/<ModeId>/` only  
2. Shared timer/deadline ownership → `Kernel/`  
3. New match opcode handler → `Host/` partial (not lobby/discovery)  
4. New lobby op → `Net/Lobby/`  
5. Discovery change → `Lan/`  
6. Decompile / Ghidra → `decompiled/` (+ tools tree for RE agent)
