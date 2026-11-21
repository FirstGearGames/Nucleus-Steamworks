# Lobbies

Finding the host through a Steam lobby. The host starts its server, creates a lobby and names itself the lobby's game server, and a player who enters the lobby connects to that Steam ID. The lobby only finds the host; the session itself runs over the Steam transport like any other.

## What it shows

- The host can create a public, friends-only or private lobby.
- Players can invite each other through the Steam overlay. An invite, or **Join game** in Steam, works whether the game is already running (`GameLobbyJoinRequested_t`) or Steam starts it with `+connect_lobby <lobby id>`.
- A player can join a friend who is in a lobby of this game with **Refresh friends**, or join a lobby by typing or pasting its ID.
- The member list shows Steam names and small avatars, and Steam's lobby callbacks keep it current.
- The host sets a game mode and map as lobby data, and every member reads them.
- When the host leaves, Steam hands the lobby to another member, but the server left with the host. Each member therefore leaves the lobby once its client stops.

## Requirements

- Nucleus, this package and Steamworks.NET are installed.
- The Shared sample is imported beside this one.
- Steam is running and signed in, and `steam_appid.txt` (480 for testing) is in the project folder, or beside the executable in a build.

## Testing with two accounts

1. Open `Scenes/SteamLobbies.unity`. Build it for the second machine, because invites and `+connect_lobby` start a build, not the editor.
2. On the first machine, pick a lobby type and press **Host lobby**.
3. Join from the second account in any of these ways: press **Invite friends** on the host and accept in Steam; press **Refresh friends** and then **Join** beside the host; or press **Copy lobby ID** on the host, paste it under **Join by lobby ID** and press **Join lobby**.
4. Change the game mode or map on the host and press **Apply lobby data**. The member sees the change.
5. Press **Leave** on the host. The member's client stops, and it leaves the lobby too.

App ID 480 (Spacewar) is shared by every developer testing on it, so anyone can see a public lobby. Use friends-only or private lobbies while testing.

## What to look at

- `LobbiesPanel.Host` calls `StartHostAsync` before `SteamMatchmaking.CreateLobby`, so nobody can dial a server that is not listening yet.
- `LobbiesPanel.OnLobbyCreated` names the lobby's game server with `SteamNetworkingTransport.ServerSteamId`, the Steam ID the running server is reached at.
- `LobbiesPanel.OnLobbyEntered` sets `SteamTransport.RemoteSteamId` to the lobby's game server, read with `GetLobbyGameServer` or from `LobbyGameCreated_t` while waiting, and then calls `StartClientAsync`.
- `LobbiesPanel.OnConnectionLocalStateChanged` leaves the lobby once the member's client stops.
