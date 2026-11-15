# Nucleus Steamworks

**A Steam Networking Sockets transport for Nucleus: players connect by Steam ID through Valve's relay network, so nobody forwards a port or shares an address.** It can also connect by IP address and port, the way a dedicated server is usually reached. With Nucleus Pro, a session can survive losing its host.

## Requirements

- Unity 6.5 or newer, with Nucleus already in the project. This assembly references `Nucleus.dll`, `CodeBoost.dll` and `Newfarm.Client.dll`, which Nucleus installs in `Assets/Nucleus`.
- [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) installed as a package (`com.rlabrecque.steamworks.net`). Installing it defines `NUCLEUS_STEAMWORKS`, which is what makes these assemblies compile. If Steamworks.NET came into the project another way, add `NUCLEUS_STEAMWORKS` to the Scripting Define Symbols yourself.
- Something in your game that initialises Steam and runs its callbacks every frame, such as Heathen's Foundation for Steamworks, a `SteamManager`, or your own call to `SteamAPI.RunCallbacks`. The transport never initialises Steam itself, because a process holds one Steam session and your game owns it.
- A desktop platform: Windows, macOS or Linux, plus the Editor.

## Install

This folder is a Unity package (`com.firstgeargames.nucleus.steamworks`). Either:

- In the Package Manager, choose **Install package from git URL** and enter `https://github.com/FirstGearGames/Nucleus-Steamworks.git`. Add `#v1.0.0` to the end to pin a release.
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
| Addressing | Steam ID | **Steam ID** goes through Valve's relay. **IP Address** listens on a port and connects to an address. |
| Remote Steam Id | 0 | Who a client connects to by Steam ID. 0, or your own ID, joins your own server. |
| Remote Address | empty | Who a client connects to by IP, optionally with a port such as `203.0.113.7:7777`. Host names are not looked up. |
| Port | 7777 | The port the server listens on by IP, and the one a client uses when its address names none. |
| Bind Address | empty | The local address the server listens on by IP. Empty listens on every interface. |
| Is Game Server | off | Serve through Steam's game server interface. Turn it on for a dedicated server, leave it off when a player hosts. |
| Maximum Connections | 0 | The most clients the server accepts at once. 0 accepts any number. |
| Maximum Transmission Unit | 1200 | The largest datagram built before it is split. |
| Send Rate Kilobytes Per Second | 4096 | The rate Steam sends at on each connection. Steam's own is 256, slower than nearly any player's line, so a large join queues behind it. 0 keeps Steam's own. |

`RemoteSteamId` and `RemoteAddress` can be changed at any time from code and apply at the next client start.

## Host migration (Nucleus Pro)

With host migration on, a session outlives whoever is hosting it. When the host leaves or crashes, one of the remaining players takes over, the others reconnect to them, and play carries on with the world they already had.

It needs Nucleus Pro and a running [Newfarm](https://github.com/FirstGearGames/Newfarm) session directory, which every player can reach. The code compiles with Nucleus Free too, but there no session is opened.

1. On the **Steam Transport**, turn on **Is Host Migration Enabled** (under **Host Migration**) and set the **Directory Address** and **Directory Port** (default `127.0.0.1:47778`).
2. Start and join exactly as you would without it, as in the quick start above.

That is all. Once the server is running it opens a session with the directory, and every player that joins is told which session it is in, so nobody hands out anything new.

If the server cannot open its session, `steamTransport.Migration.SessionOpenFailed` is raised with a `SessionOpenResult` saying why, such as `DirectoryUnreachable` or `DirectoryAtCapacity`. The game carries on without host migration, and `await steamTransport.Migration.OpenSessionAsync()` tries again and returns the result.

To hand hosting to someone else on purpose, the host calls `steamTransport.TrySurrenderHostingSession()`. It stays in the game as an ordinary player.

`steamTransport.Migration` also raises `Promoted` when this player takes over, `Rejoined` when it follows the session to a new host, and `Abandoned` when the session could not be carried on. A player joining after a handover needs the new host's Steam ID, or can call `JoinSessionAsync(steamTransport.SessionId)` with the session id read on any player already in it.

When players connect by IP, the host advertises **Hosted Address**. Leave it empty to use the bind address, or this machine's own IPv4 address, which only reaches players on the same network. A host reached through a router sets it to its public address with a forwarded port.

## Things that look like bugs but are not

- **Connecting to your own Steam ID over the relay fails.** Steam does not route a player back to itself, so testing by Steam ID needs two Steam accounts on two machines. With one account, test by **IP Address** instead: several game instances on one machine can each host on their own port.
- **A host's own client never appears in Steam.** It is paired with the server inside the process, so its traffic never leaves the machine.
- **A client that cannot reach its server waits for Steam to give up.** That is Steam's own connect timeout unless `SteamNetworkingTransport.ConnectTimeoutMilliseconds` sets a shorter one.
- **The world stays put when the link drops** while host migration is on. That is deliberate: it is what the next host carries on from.

## Troubleshooting

- **Nothing in this folder compiles, and there are no errors.** Steamworks.NET is not installed as a package, so `NUCLEUS_STEAMWORKS` is not defined. Install it, or add the define.
- **"Steam refused to open a listen socket" or "Steam refused to connect".** Steam is not initialised yet. Make sure whatever starts Steam runs before the server or client starts.
- **A client connects by IP to nothing.** Check that the address is an IP address rather than a host name, and that the server's port is open to it.
- **Hosting a migratable session fails by IP with an address length error.** The address and port together must fit in 64 characters. Use an IPv4 address in **Hosted Address**.

## Checking a link

`Diagnostics/SteamLinkProbe` checks a Steam link both ways with nothing of your game in the way. Put it on the GameObject with the `UnityCoreManager`, `UnityTransportManager` and `SteamTransport`, set **Role** to **Host** on one machine and to **Client** with the host's Steam ID on the other, and enter play mode on both. The host logs its own Steam ID first.

Each second the client sends a numbered probe to the server on both channels, and the server echoes each one back. The host logs how many probes arrived from each client and how many bytes arrived; the client logs how many echoes came back. Both log every send Steam refused, with Steam's reason. It starts Steam itself, so turn off **Is Steam Started Here** if your game already does.

## Tests

The play tests in `Tests` run over a real Steam connection. They need the Steam client running and signed in, and a `steam_appid.txt` holding an app id (480, Spacewar, for testing) in the project folder. They are skipped when Steam cannot be initialised.

## License

MIT. See [LICENSE](LICENSE).
