# Quick Match

One **Find Match** button. It searches for an open lobby, widening the distance until it finds one, or creates a lobby and waits. When the lobby is full, every member loads the game scene and the session starts.

## What it shows

- Lobby searches are filtered by a sample key, the game version, an open state and one free slot, at Close, Default, Far and then Worldwide distance.
- The lobbies are invisible: Steam's searches return them, but friends lists do not show them.
- No server runs until the lobby is full, so matchmaking costs no session.
- Two players who search at the same moment both create a lobby. A lone owner keeps searching, and moves into any waiting lobby with a lower Steam ID.
- The lobby is carried across a scene load. Then the host calls `SetLobbyGameServer`, and members wait for `LobbyGameCreated_t`, so nobody dials a server that is not listening.
- Only lobby members get in, through `SteamTransport.ClientAdmissionFilter`.
- The lobby closes itself: a member leaves once it is admitted, and the host leaves once it is alone.

## Requirements

- Nucleus, this package and Steamworks.NET are installed.
- The Shared sample is imported beside this one.
- Steam is running and signed in, and `steam_appid.txt` (480 for testing) is in the project folder, or beside the executable in a build.
- **Both scenes are in the build profile.** Importing a sample does not add them. Select the `QuickMatchPanel` in `Scenes/QuickMatch.unity` and press **Add both scenes to the build**.

## Testing with two accounts

1. Open `Scenes/QuickMatch.unity`, add the scenes as above, and build it for the second account.
2. Press **Find Match** on one account, then on the other. Both load the game and connect.
3. Also try both pressing it at the same moment, **Cancel** while waiting, the owner leaving while waiting and while loading, and a second match after **Leave match**.

Distance widening cannot be tested on one machine.

## What to look at

- `QuickMatchPanel.ReadLobby` is read by the owner on every update, and starts a full lobby.
- `QuickMatchPanel.ApplyMergeResults` holds the rule that lets exactly one of two waiting players move.
- `QuickMatchGame.TryStartClientFromLobby` reads the game server for a member that loaded after the host named it.
- `QuickMatchGame.OnClientAdmission` is the lobby member check.
