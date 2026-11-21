# Dedicated Server

A headless Steam game server that players reach by a fake IP address through Valve's relay network, with ticket authentication, listed in Steam's server browser. A second scene lists those servers and joins one.

## What it shows

- `Scenes/SteamDedicatedServer.unity` is the server. Its `SteamBootstrap` has **Game Server** on and logs on anonymously, or with a game server login token. Its **Steam Transport** has **Addressing** on **Fake IP**, **Is Game Server** on and **Ticket Authentication Enabled** on, and the `UnityTransportManager` starts the server by itself.
- Because the transport is on **Fake IP**, `SteamBootstrap` asks Steam for a fake address after `GameServer.Init` and before logging on. Steam grants an address only to a game server that asked at that moment.
- `DedicatedServerListing` sets the server's name, map, player limit and game tags. It adds the fake address Steam grants to the game tags, so a browsing client reads it straight from the server's entry.
- `Scenes/SteamServerBrowser.unity` is the client. `ServerBrowserPanel` lists this sample's servers from Steam's internet list, filtered by game tag, or from the LAN list, with name, map, players and ping, and joins one by its fake address. A fake address can also be pasted. Its **Steam Transport** is on **Fake IP** with **Is Game Server** off, and has **Ticket Authentication Enabled** on to match the server.
- A join sets `SteamTransport.RemoteAddress` to the fake address and calls `StartClientAsync`.

## Requirements

- Nucleus, this package and Steamworks.NET are installed.
- The Shared sample is imported beside this one.
- `steam_appid.txt` (480 for testing) is in the project folder, or beside the executable in a build. The server needs no Steam client; the browser does.

## Testing

1. Build `SteamDedicatedServer.unity` on its own as a Windows or Linux build, either for the Dedicated Server build target or as any build run with `-batchmode -nographics`. Put `steam_appid.txt` beside the executable and run it. Its log shows the game server logging on and the fake address Steam granted.
2. On a machine signed into Steam, open `SteamServerBrowser.unity`, press Play and press **Refresh internet**. Steam's master server can take a minute or two to list a new server. Press **Join** beside it.
3. Or paste the fake address from the server's log into the address field and press **Join address**.
4. Stop the server. The client returns to the browser.

A process here is either a Steam game server or a signed-in Steam player, never both, so run the server and the browser as two processes.

## Notes

- Steam grants a new fake address each time the server process starts, which is why the server publishes it in its listing rather than having it configured.
- If the server does not appear in the internet list, give it a login token in `SteamBootstrap`, made for the App ID on Steam's game server account page. Some setups list only servers that logged on with one. A pasted address works either way.
- The player count Steam lists comes from the auth sessions the server opens, and ticket authentication opens one for each player.

This sample is adapted from maxkratt's [nucleus-steamworksnet](https://github.com/maxkratt/nucleus-steamworksnet), under the MIT license.
