# Ban List

Keeping players out by Steam ID. A host kicks, bans or blacklists a connected player, and a refused player is told why.

## What it shows

- `SteamTransport.ClientAdmissionFilter` is set in `Awake`, before the server listens. It runs before the server waits for the player's ticket, so a refused player costs no auth session. It is only asked while **Ticket Authentication Enabled** is on.
- A ban lasts until the server stops.
- The blacklist is a file, `SteamBanListBlacklist.txt` under `Application.persistentDataPath`, with one Steam ID per line. An admin can edit it by hand, and it is read again each time hosting starts.
- The reason reaches the player before it is disconnected: through `ServerManager.DenyClient(connection, notice)` when it is refused at admission, and through `Connection.Kick(notice)` when it is already in.

## Requirements

- Nucleus, this package and Steamworks.NET are installed.
- The Shared sample is imported beside this one.
- Steam is running and signed in, and `steam_appid.txt` (480 for testing) is in the project folder, or beside the executable in a build.

## Testing with two accounts

1. Open `Scenes/SteamBanList.unity`, and build it for the second account.
2. On the first account, press **Host**, then **Copy my Steam ID**.
3. On the second account, paste it into **Host Steam ID** and press **Join**.
4. On the host, press **Kick**. The player sees the reason. Join again.
5. Press **Ban**, and try to join again. The player is refused with the reason. Press **Unban** and it can join again.
6. Press **Blacklist**, then stop and host again. The player is still refused.

## What to look at

- `BanListPanel.OnClientAdmission` holds the filter and the refusal.
- `BanNotice` has `IsAuthenticationRequired` set to false, because a refusal reaches a player that was never authenticated.
- `BanNotice` is a network type, so the sample's assembly has a `NetworkBundle` of its own in `Scripts/AssemblyInfo.cs`.

## Limits

- Steam lobbies have no kick, so in a lobby game a ban only closes the link to the server. An honest player leaves the lobby when its link drops.
- The filter runs before the ticket, so it sees only the player's Steam ID, not the owner of the game (`m_OwnerSteamID`). A banned owner can come back on another account that borrows the game through Family Sharing. A game that cares checks the owner in the ticket's result.
