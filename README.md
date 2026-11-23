# Nucleus Steamworks

**A Steam Networking Sockets transport for Nucleus: players connect by Steam ID through Valve's relay network, so nobody forwards a port or shares an address.** It can also connect by IP address and port, the way a dedicated server is usually reached, or by a fake IP address Steam grants a dedicated server so its real address stays hidden. With Nucleus Pro, a session can survive losing its host.

## Requirements

- Unity 6.5 or newer, with Nucleus already in the project. This assembly references `Nucleus.dll`, `CodeBoost.dll` and `Newfarm.Client.dll`, which Nucleus installs in `Assets/Nucleus`.
- [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) installed as a package (`com.rlabrecque.steamworks.net`). Installing it defines `NUCLEUS_STEAMWORKS`, which is what makes the transport compile. Until then only the **Steam Transport** component compiles, and its inspector has an **Install Steamworks.NET** button, which needs Git. If Steamworks.NET came into the project another way, add `NUCLEUS_STEAMWORKS` to the Scripting Define Symbols yourself.
- Something in your game that initialises Steam and runs its callbacks every frame, such as Heathen's Foundation for Steamworks, a `SteamManager`, or your own call to `SteamAPI.RunCallbacks`. The transport never initialises Steam itself, because a process holds one Steam session and your game owns it.
- A desktop platform: Windows, macOS or Linux, plus the Editor.

## Install

This folder is a Unity package (`com.firstgeargames.nucleus.steamworks`). Either:

- In the Package Manager, choose **Install package from git URL** and enter `https://github.com/FirstGearGames/Nucleus-Steamworks.git`. Add `#v1.4.0` to the end to pin a release.
- Or, in the Package Manager, choose **Install package from disk** and pick this folder's `package.json`, or add `"com.firstgeargames.nucleus.steamworks": "file:<path to this folder>"` to `Packages/manifest.json`. Edits made in Unity are saved straight back to this folder.
- Or copy the folder into your project's `Packages` or `Assets` folder.

## Quick start

1. Add a **Steam Transport** component to the GameObject holding your `UnityCoreManager`.
2. Leave **Addressing** on **Steam ID**.
3. On the host, start the server and the client as you normally would. Leave **Remote Steam Id** at 0: a host's own client joins its server inside the process and never touches Steam.
4. Give the host's Steam ID to the players joining. On each of them, set `SteamTransport.RemoteSteamId` to that ID, then start the client.

## Settings

| Setting | Default | What it does |
|---|---|---|
| Addressing | Steam ID | **Steam ID** goes through Valve's relay. **IP Address** listens on a port and connects to an address. **Fake IP** connects to an address Steam grants the server, over Valve's relay. |
| Remote Steam Id | 0 | Who a client connects to by Steam ID. 0, or your own ID, joins your own server. |
| Is Relay Only Enabled | on | Carry Steam ID and fake IP links over Valve's relay only, so no player learns another's IP address. Off lets Steam connect two players directly when their Steam settings allow it, which can lower ping. See **Relay only** below for what it costs. |
| Remote Address | empty | Who a client connects to by IP or fake IP, optionally with a port such as `203.0.113.7:7777`. Host names are not looked up. |
| Expected Server Steam Id | 0 | The Steam ID the server a client reaches by IP or fake IP must have, such as the one your server list shows beside the address. A server Steam authenticates as any other account is refused, so an address that now leads somewhere else cannot pass for your server. 0 accepts whichever server answers. Leave it at 0 when hosts migrate. |
| Port | 7777 | The port the server listens on by IP, and the one a client uses when its address names none. |
| Bind Address | empty | The local address the server listens on by IP. Empty listens on every interface. |
| Is Game Server | off | Serve through Steam's game server interface. Turn it on for a dedicated server, leave it off when a player hosts. |
| Maximum Connections | 0 | The most clients the server accepts at once. 0 accepts any number. |
| Maximum Connections Per Steam Id | 1 | The most connections one Steam account may hold on the server at once, so one account cannot fill it. A connection past this is refused, and no connection already held is closed to make room. 0 accepts any number. The host's own client is not counted. |
| Maximum Transmission Unit | 1200 | The largest datagram built before it is split. |
| Send Rate Kilobytes Per Second | 4096 | The rate Steam sends at on each connection. Steam's own is 256, slower than nearly any player's line, so a large join queues behind it. 0 keeps Steam's own. |
| Ticket Authentication Enabled | off | Admit a client only once Steam confirms its auth session ticket: the account owns the game and is not banned. Server and clients both need it on. |
| Share Steam Ids Enabled | off | Tell every client the Steam ID of every other client, for names, avatars and friend checks. Only the server's value counts. |

`RemoteSteamId`, `RemoteAddress` and `ExpectedServerSteamId` can be changed at any time from code and apply at the next client start. While a server runs, the inspector shows where clients reach it, its Steam ID, fake IP address or port, with a **Copy** button.

The transport also reads three launch arguments, when your game calls `ApplyCommandLineArguments()` on it: `-address` sets `RemoteAddress`, `-steamid` sets `RemoteSteamId`, and `-port` sets the port.

**Relay only.** Links addressed by Steam ID or by fake IP are peer-to-peer links, and Steam can move one to a direct route between two players, which tells each of them the other's IP address. **Is Relay Only Enabled** is on by default, so Steam never looks for a direct route and every packet goes by way of Valve's relays. That costs latency when the players are close to each other but far from a relay: each packet travels to the nearest relay and back out, so their ping becomes about twice their ping to that relay, where a direct route between neighbours could take a few milliseconds. Between players far apart, Valve's network often matches or beats the direct route. Turn it off where players may learn each other's addresses, such as a game among friends. Scenes saved before relay only became the default keep the value they were saved with.

## Dedicated servers

A dedicated server turns on **Is Game Server** and runs as a Steam game server: your game calls `GameServer.Init`, logs on with `SteamGameServer.LogOnAnonymous()` or a login token, and calls `GameServer.RunCallbacks()` every frame. It needs no Steam client on the machine.

With **Addressing** on **Fake IP**, players reach the server at an address Steam grants it rather than its real one, over Valve's relay, so nobody learns where the server is and no port is opened.

1. Call `SteamGameServerNetworkingSockets.BeginAsyncRequestFakeIP(1)` after `GameServer.Init` and before logging on. Steam needs to know before the log on that the server will use a fake IP address.
2. Start the server. Once Steam grants the address, the server logs it and `SteamNetworkingTransport.ServerFakeAddress` holds it, such as `169.254.12.34:27015`.
3. Hand that address to players, through a server browser or a lobby, and set it as **Remote Address** on each of them.

A server addressed by fake IP keeps the same address for as long as the process runs, across restarts of the server.

## Players

**Steam IDs.** `SteamNetworkingTransport.TryGetSteamId(connection, out ulong steamId)` reads a client's Steam ID, as Steam authenticated its link. It also takes a connection id, such as a system's controlling connection. `TryGetConnection(steamId, out Connection connection)` goes the other way. A server knows every client and a client knows itself. Turn on **Share Steam Ids Enabled** on the server to let clients look each other up as well; it is off by default because a Steam ID leads to the player's public Steam profile. Shared IDs arrive a moment after a client joins, so subscribe to `SteamIdChanged` as well as reading them. Its Steam ID is `SteamNetworkingTransport.UnsetSteamId` when a client leaves.

**Refusing players.** Set `ConnectingClientFilter` on the **Steam Transport** to a method that takes a Steam ID and returns false to refuse it, for a ban list or lobby members only. A refused player is closed before your game hears of it, and its log says it was refused by the server.

**One connection per account.** By default a Steam account holds one connection to the server, so one account cannot fill it. A second connection from the same account is refused before your game hears of it, and its log says why; the connection already held is never closed to make room. A player whose game crashes keeps their place until Steam times the link out, ten seconds with Steam's own timeout, and can join again after that. While a cap is set, a connection whose Steam ID Steam could not authenticate is refused, because it cannot be counted. Set **Maximum Connections Per Steam Id** higher to allow more, or to 0 for any number.

**Ticket authentication.** With **Ticket Authentication Enabled**, a client sends the server a Steam auth session ticket, and the server admits it only once Steam confirms that the account owns the game and is not banned. Set `ClientAdmissionFilter` on the **Steam Transport** to refuse a player before its ticket is checked; unlike `ConnectingClientFilter`, it can tell the player why, through `ServerManager.DenyClient(connection, notice)`. If Steam withdraws its approval later, such as for a VAC ban, the server kicks the player and tells it why. A transport set up from code makes a `SteamTicketAuthenticator`, calls `Initialize(coreManager, transport)` once the transport is added, and `Deinitialize()` before it is removed.

**License owners.** A player can borrow the game from another account through Family Sharing, and plays under their own Steam ID, so a ban list that checks Steam IDs alone lets a banned account come back by lending the game to another account. With **Ticket Authentication Enabled**, `SteamNetworkingTransport.TryGetOwnerSteamId(connection, out ulong ownerSteamId)` reads the account that owns the license the player is using: their own Steam ID, or the lender's when the game is borrowed. Steam names it when it approves the ticket, and it can be read from the moment `ServerManager.ClientAuthenticated` is raised for the player, which is the place to check both IDs against a ban list and kick the player. The host's own client is admitted without a ticket, so it has none.

**Packet size.** A server accepts a message of up to 255 times the transmission unit from a client, 306,000 bytes by default, the same as Synapse. Steam closes a client that sends a larger one before the server reads any of it.

## Host migration (Nucleus Pro)

With host migration on, a session outlives whoever is hosting it. When the host leaves or crashes, one of the remaining players takes over, the others reconnect to them, and play carries on with the world they already had.

It needs Nucleus Pro and a [Newfarm](https://github.com/FirstGearGames/Newfarm) session directory that every player can reach. The public directory at `newfarm.nucleus-networking.com` is free to use and is the default. The code compiles with Nucleus Free too, but there no session is opened.

1. Add a **Steam Host Migration** component beside the **Steam Transport**. Having it there is what turns host migration on.
2. Leave **Directory Address** and **Directory Port** at the public directory (`newfarm.nucleus-networking.com:47778`), or point them at your own. A host name is looked up through DNS each time a session is opened or joined, and a lookup that fails is logged and treated as an unreachable directory, so `OpenSessionAsync()` tries it again.
3. Start and join exactly as you would without it, as in the quick start above.

That is all. Once the server is running it opens a session with the directory, and every player that joins is told which session it is in, so nobody hands out anything new. A host that stops its server or quits hands the session on at once.

If the server cannot open its session, `steamHostMigration.Migration.SessionOpenFailed` is raised with a `SessionOpenResult` saying why, such as `DirectoryUnreachable` or `DirectoryAtCapacity`. The game carries on without host migration, and `await steamHostMigration.Migration.OpenSessionAsync()` tries again and returns the result.

To hand hosting to someone else on purpose, the host calls `steamHostMigration.TrySurrenderHostingSession()`. It stays in the game as an ordinary player.

`steamHostMigration.Migration` also raises `Promoted` when this player takes over, `Rejoined` when it follows the session to a new host, and `Abandoned` when the session could not be carried on. A player joining after a handover needs the new host's Steam ID, or can call `steamHostMigration.JoinSessionAsync(sessionId)` with the `SessionId` read on any player already in it.

When players connect by IP, the host advertises **Hosted Address** on the **Steam Host Migration**. Leave it empty to use the bind address, or this machine's own IPv4 address, which only reaches players on the same network. A host reached through a router sets it to its public address with a forwarded port. When players connect by fake IP, a player taking over asks Steam for a fake IP address of its own and advertises that, so **Hosted Address** is not used.

### Upgrading from 1.0.0

Host migration moved off the **Steam Transport** onto its own **Steam Host Migration** component, built on the integration's `NetworkHostMigration`. Unity cannot carry the old settings across, so add the component and set it up again: there is no **Is Host Migration Enabled** any more, since the component being there is the switch. `Migration`, `SessionId`, `StartHostingSessionAsync`, `JoinSessionAsync` and `TrySurrenderHostingSession` moved with it. It needs a Nucleus Unity integration that includes `NetworkHostMigration`.

## Things that look like bugs but are not

- **Connecting to your own Steam ID over the relay fails.** Steam does not route a player back to itself, so testing by Steam ID needs two Steam accounts on two machines. With one account, test by **IP Address** instead: several game instances on one machine can each host on their own port.
- **A second game instance signed in to the same Steam account cannot join.** The server holds one connection per Steam account by default. To test several clients on one account, raise **Maximum Connections Per Steam Id** on the server, or set it to 0.
- **A player whose game crashed cannot join again for a few seconds.** Their old link holds their place until Steam times it out, ten seconds with Steam's own timeout.
- **A host's own client never appears in Steam.** It is paired with the server inside the process, so its traffic never leaves the machine.
- **A client that cannot reach its server waits for Steam to give up.** That is Steam's own connect timeout, 10 seconds, unless `SteamNetworkingTransport.ConnectTimeoutMilliseconds` sets a shorter one.
- **The world stays put when the link drops** while host migration is on. That is deliberate: it is what the next host carries on from.
- **Every dropped link logs a line saying why it closed.** It names Steam's reason, such as a timeout or a refusal, and the text the other side gave. That is information, not an error.
- **A player with Ticket Authentication off is dropped by a server with it on** once the authentication timeout passes, 30 seconds by default, because it never sends a ticket. A player with it on joins a server with it off unchecked.
- **"Steam was shut down while the server was running" or "while the client was connected".** Your game shut Steam down before stopping the transport. Steam closed every link as it went, so the transport stops and forgets them rather than throwing; stop the transport first to close them cleanly.

## Troubleshooting

- **Nothing in this folder compiles, and there are no errors.** Steamworks.NET is not installed as a package, so `NUCLEUS_STEAMWORKS` is not defined. Install it, or add the define. `DISABLESTEAMWORKS`, Steamworks.NET's own switch, compiles these out too.
- **"Steam refused to open a listen socket" or "Steam refused to connect".** Steam is not initialised yet. Make sure whatever starts Steam runs before the server or client starts.
- **"Could not connect: Steam's network config did not arrive in time".** A client waits for Steam to fetch its network config before it dials, since a dial made before it arrives fails at once, and `ConnectTimeoutMilliseconds` ran out first. Check that Steam is online, or give it longer. With no `ConnectTimeoutMilliseconds` the wait lasts at most Steam's own connect timeout, 10 seconds, then the client dials anyway.
- **A client connects by IP to nothing.** Check that the address is an IP address rather than a host name, and that the server's port is open to it.
- **"Steam did not grant this server a fake IP address".** A game server must ask for one between `GameServer.Init` and logging on, as in **Dedicated servers** above. Steam retries for a while before it gives up, so this can take minutes to appear.
- **Hosting a migratable session fails by IP with an address length error.** The address and port together must fit in 64 characters. Use an IPv4 address in **Hosted Address**.

## Checking a link

`Diagnostics/SteamLinkProbe` checks a Steam link both ways with nothing of your game in the way. Put it on the GameObject with the `UnityCoreManager`, `UnityTransportManager` and `SteamTransport`, set **Role** to **Host** on one machine and to **Client** with the host's Steam ID on the other, and enter play mode on both. The host logs its own Steam ID first.

Each second the client sends a numbered probe to the server on both channels, and the server echoes each one back. The host logs how many probes arrived from each client and how many bytes arrived; the client logs how many echoes came back. Both log every send Steam refused, with Steam's reason. It starts Steam itself, so turn off **Is Steam Started Here** if your game already does.

From code, `SteamNetworkingTransport.TryGetLinkStatus(connection, out SteamLinkStatus linkStatus)` reads Steam's own view of a link: ping, quality each way, send and receive rates, Steam's send rate, and the reliable and unreliable bytes still queued. On a server pass a remote client's Connection; on a client pass its own or its server Connection. It returns false for a host's own client, which never crosses Steam. Each read allocates a little in Steamworks.NET, so read it a few times a second rather than every frame.

`SetNetworkConditions` on the transport, or the network conditions on the `UnityTransportManager`, has Steam simulate latency, jitter, loss, reordering and duplication on its own sends. Steam keeps these for the whole process, so they apply to every Steam connection in it, and latency set where both peers share a process counts twice in the round trip. A host's own client never crosses Steam, so test from a remote client. A transport that was never given conditions leaves Steam's simulation settings as the game set them, and one that was clears them when it shuts down.

## Samples

Import them from the package's **Samples** tab in the Package Manager. Import **Shared** first, since every other sample needs it. Each sample has a README with its scene and a test for two Steam accounts.

| Sample | What it shows |
|---|---|
| Shared | Starting and running Steam for a sample scene, as a player or a game server, plus name tags, the player mover and the sample skin. |
| Quick Start | Hosting, or joining a host by Steam ID, and moving a capsule. |
| Lobbies | Creating, joining and leaving Steam lobbies, with invites, members, friends and lobby data. |
| Lobby Browser | Searching public lobbies by distance and estimated ping. |
| Dedicated Server | A Steam game server reached by fake IP, with a LAN and internet server browser. |
| Voice Chat | Push to talk Steam voice, relayed through the server. |
| Ban List | Kicking, banning and blacklisting players by Steam ID, and telling a refused player why. |
| Quick Match | One-button matchmaking through invisible Steam lobbies, then a session for lobby members only. |

Quick Start and Voice Chat need the Input System package, and spawn networked prefabs: after importing them, open **Tools > Nucleus** and press **Rebuild** beside Rebuild Network Prefab Collection. Quick Match's two scenes must be in the Build Profile; its panel's inspector offers to add them.

## Agent skill

`Skills~/nucleus-steamworks` teaches an AI coding agent this transport: setup, addressing, dedicated servers, Steam IDs, refusing players, tickets and every setting. Copy the folder into your project's `.claude/skills/`, or your agent's equivalent, beside the Nucleus skills.

## Tests

The play tests in `Tests` run over a real Steam connection. They need the Steam client running and signed in, and a `steam_appid.txt` holding an app id (480, Spacewar, for testing) in the project folder. They are skipped when Steam cannot be initialised. The dedicated server cases also log a game server on anonymously in the same process, and are skipped when it cannot. In a project that installs this as a package, list `com.firstgeargames.nucleus.steamworks` under `testables` in `Packages/manifest.json` so the Test Runner finds them.

## License

MIT. See [LICENSE](LICENSE).
