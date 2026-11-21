# Lobby Browser

A list of public lobbies with a distance filter and an estimated ping for each one. Hosts open public lobbies, and players search, pick a lobby by its ping and join it.

## What it shows

- A host opens a public lobby that publishes a name, a sample tag and the host's Steam ping location as lobby data.
- A player searches with `RequestLobbyList`, filtered to this sample's tag and to a distance (Close, Default, Far or Worldwide), and the results are sorted by ping.
- Each lobby's ping is estimated without contacting its host. `EstimatePingTimeFromLocalHost` combines the host's ping location string (`GetLocalPingLocation` and `ConvertPingLocationToString`) with this player's own. A host whose relay measurement is not ready yet publishes its location once `SteamRelayNetworkStatus_t` reports the relay network available.
- A player joins a listed lobby, connects to the game server its host named, and leaves the lobby once the client stops.

## Requirements

- Nucleus, this package and Steamworks.NET are installed.
- The Shared sample is imported beside this one. Its `SteamBootstrap` starts relay network access.
- Steam is running and signed in, and `steam_appid.txt` (480 for testing) is in the project folder, or beside the executable in a build.

## Testing with two accounts

1. Open `Scenes/SteamLobbyBrowser.unity`.
2. On the first machine, set a lobby name and press **Host public lobby**.
3. On the second machine, signed into another Steam account, pick a distance and press **Search**. The lobby is listed with its member count and estimated ping. Press **Join**.
4. Press **Leave** on the host to stop its server. The player's client stops, and it leaves the lobby too.

The estimate runs through Valve's relays, so it is never zero, even for a lobby on the same machine. On App ID 480 every developer's public lobbies share one list, which is why searches filter to this sample's tag.

## What to look at

- `LobbyBrowserPanel.Search` adds the filters again before every `RequestLobbyList`, because Steam clears them after each request.
- `LobbyBrowserPanel.OnLobbyMatchListReceived` estimates the ping for each lobby.
- `LobbyBrowserPanel.TryPublishPingLocation` is the host's side of the estimate.
- `LobbyBrowserPanel.OnLobbyCreated` names the lobby's game server with `SteamNetworkingTransport.ServerSteamId`.

This sample is adapted from maxkratt's [nucleus-steamworksnet](https://github.com/maxkratt/nucleus-steamworksnet), under the MIT license.
