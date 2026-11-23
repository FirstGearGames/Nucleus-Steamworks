---
name: nucleus-steamworks
description: Covers the Nucleus Steam transport from the Nucleus Steamworks package (com.firstgeargames.nucleus.steamworks), the SteamTransport component and the engine SteamNetworkingTransport built on Steamworks.NET. Use it when setting up or configuring the Steam transport; choosing an addressing (SteamAddressing SteamId, IpAddress, FakeIp) and setting RemoteSteamId or RemoteAddress; running a dedicated server on Steam's game server interface (IsGameServer, Fake IP, BeginAsyncRequestFakeIP, ServerFakeAddress); passing a host's Steam ID through a Steam lobby (ServerSteamId); reading players' Steam IDs (TryGetSteamId, TryGetConnection, SteamIdChanged, ShareSteamIdsEnabled); refusing players (ConnectingClientFilter, ClientAdmissionFilter, Maximum Connections); ticket authentication (SteamTicketAuthenticator); host migration over Steam (SteamHostMigration, Pro); checking a link (TryGetLinkStatus, SteamLinkProbe, SendFailed); and troubleshooting a Steam client that cannot connect, is dropped, or logs a Steam error.
---

# Nucleus over Steam

The general connection model (starting and stopping peers, `Connection`, authentication, kicks, host migration in depth) belongs to **nucleus-transports-and-connections**. This skill covers only what is specific to Steam. The package ships as readable C#, so confirm any API against its source before using it.

## Setup

1. Install Steamworks.NET as a package (`com.rlabrecque.steamworks.net`). That defines `NUCLEUS_STEAMWORKS`, which is what makes the transport compile. Until then only the **Steam Transport** component compiles, and its inspector has an **Install Steamworks.NET** button, which needs Git. If Steamworks.NET came in another way, add `NUCLEUS_STEAMWORKS` to the Scripting Define Symbols yourself.
2. Initialise Steam in the game and run its callbacks every frame, through a `SteamManager`, Heathen's Foundation for Steamworks, or your own call to `SteamAPI.RunCallbacks`. The transport never initialises Steam itself, because a process holds one Steam session and the game owns it. Shut Steam down only after the transport stops.
3. Add a **Steam Transport** component to the GameObject holding the `UnityCoreManager`, and choose its **Addressing** (below).
4. Start the server, client or host as usual. A host's own client is paired with its server inside the process and never touches Steam, so the host leaves **Remote Steam Id** at 0 and **Remote Address** empty.
5. To test, run the Steam client signed in, with a `steam_appid.txt` holding an app ID (480, Spacewar) in the project folder.

A start throws, with a message saying why, while Steamworks.NET is missing or compiled out, either by `DISABLESTEAMWORKS` or for a build target other than Windows, macOS, Linux or Android.

## Addressing

| Addressing | Use it for | The server is reached at | A client sets |
|---|---|---|---|
| **Steam ID** (`SteamAddressing.SteamId`, the default) | A player hosting from the game | Its Steam ID, `ServerSteamId`, through Valve's relay | `RemoteSteamId` to the host's Steam ID |
| **IP Address** (`IpAddress`) | A dedicated server on a public port, or several instances on one Steam account | **Bind Address** and **Port** (7777) | `RemoteAddress` to `ip` or `ip:port`; without a port, **Port** is used |
| **Fake IP** (`FakeIp`) | A dedicated server whose real address stays hidden behind Valve's relay | The address Steam grants it, `ServerFakeAddress` | `RemoteAddress` to that address |

- `RemoteAddress` must be an IP address. Host names are not looked up.
- An address says where a server is, not who it is. Set `ExpectedServerSteamId` to the Steam ID a server list shows beside the address, and a client refuses any server Steam authenticates as another account. 0 accepts whichever server answers; leave it at 0 when hosts migrate.
- `SteamTransport.RemoteSteamId` and `SteamTransport.RemoteAddress` can be set from code at any time and apply at the next client start. A client that is already connected keeps the server it dialled until it stops and starts again.
- **Is Relay Only Enabled** (on by default) keeps Steam ID and fake IP links on Valve's relay, so no player learns another's IP address: Steam shares no ICE candidates. Off lets Steam connect two players directly when their Steam settings allow it, which can lower ping, since a relayed route between two players near each other costs about twice their ping to the nearest relay. It has no effect under IP Address. Scenes saved before it became the default keep their saved value.
- While a server runs, the inspector shows where clients reach it, its Steam ID, fake IP address or port, with a **Copy** button.

## Dedicated server and Fake IP

Turn on **Is Game Server** so the server runs on Steam's game server interface rather than a signed-in player's. The machine needs no Steam client. A client always uses the player's interface.

1. Call `GameServer.Init(...)`.
2. Under **Fake IP**, call `SteamGameServerNetworkingSockets.BeginAsyncRequestFakeIP(1)` now. Steam must know before the server logs on that it will use a fake IP address.
3. Log on with `SteamGameServer.LogOnAnonymous()` or a login token, and call `GameServer.RunCallbacks()` every frame.
4. Start the server. Under **Fake IP**, once Steam grants the address the server logs it and `SteamNetworkingTransport.ServerFakeAddress` holds it, such as `169.254.12.34:27015`. It stays the same for as long as the process runs, across restarts of the server.
5. Hand that address to players, through a server browser or a lobby, and set it as `RemoteAddress` on each of them.

## Lobbies and Steam IDs

Nucleus ships no lobby code. A Steam lobby only has to carry the host's Steam ID: once its server runs, the host reads `ServerSteamId` and stores it in the lobby, for example as the lobby's game server, and each joiner sets `RemoteSteamId` from it and starts its client.

- `SteamTransport.SteamNetworkingTransport` is the engine transport, once it is added. Its `TryGetSteamId(connection, out ulong steamId)` reads a client's Steam ID as Steam authenticated its link, and also takes a connection id, such as a system's controlling connection. `TryGetConnection(steamId, out Connection connection)` goes the other way. A client's `Connection.RemoteAddress` on the server is that Steam ID on every addressing, but read it through `TryGetSteamId` rather than parsing it.
- A server knows every client's Steam ID and a client knows its own. Turn on **Share Steam Ids Enabled** on the server to let every client look up the others, for names, avatars and friend checks. It is off by default because a Steam ID leads to the player's public Steam profile. Only the server's value counts.
- Shared IDs can arrive before or after the engine reports the same client, so subscribe to `SteamIdChanged` (`uint connectionId, ulong steamId`) as well as reading them. It reports `SteamNetworkingTransport.UnsetSteamId` (0) when a client leaves.
- Only the server may say who a client is. Never trust a Steam ID a client reports about itself; it could claim someone else's.

## Refusing players

- Set `ConnectingClientFilter` on the **Steam Transport** to a method taking a `ulong steamId` and returning false to refuse it, for a ban list or lobby members only. It runs on the main thread inside Steam's callback pump, before the engine hears of the client. A handler that throws refuses the client, and the host's own client never asks. The refused client logs that it was refused, but cannot be told why; use `ClientAdmissionFilter` (below) for that.
- **Maximum Connections** (0 accepts any number) refuses at accept once the server is full. A host's own client takes one place.
- **Maximum Connections Per Steam Id** (`MaximumConnectionsPerSteamId`, 1 by default, 0 accepts any number) caps the connections one Steam account holds, counted by the Steam ID Steam authenticated for each link. A newcomer past it is refused and no connection already held is closed, so nobody can push another player off. While a cap is set, a link whose Steam ID Steam could not authenticate is refused. The host's own client is not counted. A crashed client holds its place until Steam times its link out, 10 seconds. Raise it to test several clients signed in to one Steam account against one server.

## Ticket authentication

Turn on **Ticket Authentication Enabled** on the server and on every client. Each client then sends a Steam auth session ticket, and the server admits it only once Steam confirms that the account owns the game and is not banned. The ticket is bound to the Steam ID of the link it arrives on, so a forged or borrowed one is refused. If Steam withdraws its approval later, such as for a VAC ban, the server kicks the player and tells it why.

- A client with it off is dropped by a server with it on once `ServerManager.AuthenticationTimeoutSeconds` (30 by default) passes, because it never sends a ticket. A client with it on joins a server with it off unchecked.
- Set `ClientAdmissionFilter` on the **Steam Transport**, a method taking `(Connection connection, ulong steamId)`, to refuse a player before its ticket is waited for, so a refused player costs no auth session. To tell the player why, call `ServerManager.DenyClient(connection, notice)` and return false. A handler that throws refuses the client. It runs only while Ticket Authentication is on.
- `SteamNetworkingTransport.TryGetOwnerSteamId(connection, out ulong ownerSteamId)`, or by connection id, reads the account that owns the game license the client plays on, as Steam named it when approving the ticket: the client's own Steam ID, or under Family Sharing the lender's. It is readable from `ServerManager.ClientAuthenticated` on, so check a ban list against both IDs there; a ban on Steam IDs alone lets a banned account return by lending the game to another account. The host's own client has none.
- A transport set up from code makes a `SteamTicketAuthenticator`, calls `Initialize(coreManager, transport)` once the transport is added, and `Deinitialize()` before it is removed.

## Packet limit

A server accepts a message of up to 255 times **Maximum Transmission Unit** from a client, 306,000 bytes at the default 1200, the same as Synapse. Steam closes a client that sends a larger one before the server reads any of it. The limit applies only to what a server receives.

## Host migration (Nucleus Pro)

Add a **Steam Host Migration** component beside the **Steam Transport**; having it there is what turns host migration on. It registers sessions with a Newfarm directory, by default the free public one at `newfarm.nucleus-networking.com:47778`. Start and join exactly as without it: when the host leaves or crashes, a remaining player takes over, the others reconnect to it, and play carries on with the world they had. When peers connect by IP, set **Hosted Address** to the address others reach this host at, such as a public address with a forwarded port. Under Fake IP, a player taking over advertises the fake IP address Steam grants it, and **Hosted Address** is not used. With a Free `Nucleus.dll` the component compiles, but `ServerManager.IsHostMigrationAvailable` reads false and no session is opened. Session events, `TrySurrenderHostingSession()` and `JoinSessionAsync(sessionId)` are covered in **nucleus-transports-and-connections**.

## Checking a link

- `SteamNetworkingTransport.TryGetLinkStatus(connection, out SteamLinkStatus linkStatus)` reads Steam's own view of a link: ping, quality each way, rates, and the bytes still queued. On a server pass a remote client's Connection; on a client pass its server Connection. It returns false for a host's own client. Each read allocates a little in Steamworks.NET, so read it a few times a second rather than every frame.
- `Diagnostics/SteamLinkProbe` checks a link both ways with nothing of the game in the way. Put it on the GameObject with the `UnityCoreManager`, `UnityTransportManager` and `SteamTransport`, set **Role** to **Host** on one machine and to **Client** with the host's Steam ID on the other, and enter play mode on both. It starts Steam itself unless **Is Steam Started Here** is off.
- `SetNetworkConditions` on the transport, or the network conditions on the `UnityTransportManager`, has Steam simulate latency, jitter, loss, reordering and duplication. Steam keeps these for the whole process, so they reach every Steam connection in it. A host's own client never crosses Steam, so test from a remote client.

## Things that look like bugs

- **Connecting to your own Steam ID fails.** Steam does not route a player back to itself. Test by Steam ID with two Steam accounts on two machines, or by IP address with one account, each instance hosting on its own port.
- **A host's own client never appears in Steam.** It is paired with the server inside the process.
- **A client that cannot reach its server waits 10 seconds.** That is Steam's own connect timeout. `SteamNetworkingTransport.ConnectTimeoutMilliseconds` sets a shorter one.
- **Every dropped link logs a line saying why it closed**, with Steam's reason and the text the other side gave. That is information, not an error.
- **The world stays put when the link drops while host migration is on.** It is what the next host carries on from.
- **"Steam was shut down while the server was running" or "while the client was connected".** The game shut Steam down before stopping the transport. Stop the transport first so its links close cleanly.

## Common errors

- **Only the Steam Transport component compiles, and its inspector asks to install Steamworks.NET.** `NUCLEUS_STEAMWORKS` is not defined, or `DISABLESTEAMWORKS` is, or the build target is not Windows, macOS, Linux or Android.
- **"Steam refused to open a listen socket" or "Steam refused to connect".** Steam is not initialised yet. Start it before the server or client.
- **"Steam's network config did not arrive in time".** A client waits for Steam's network config before it dials, and `ConnectTimeoutMilliseconds` ran out first. Check that Steam is online, or allow longer.
- **"Steam did not grant this server a fake IP address".** A game server must ask for one between `GameServer.Init` and logging on, as in step 2 above. Steam retries for a while before it gives up, so this can take minutes to appear.
- **Sends refused with `k_EResultLimitExceeded`.** Steam's queue for that connection is full, usually because **Send Rate Kilobytes Per Second** is below what the game sends. `SteamNetworkingTransport.SendFailed` reports each refused packet with Steam's reason.

Every field, engine member, default and launch argument: [references/settings.md](references/settings.md).
