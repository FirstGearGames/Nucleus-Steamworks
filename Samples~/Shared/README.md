# Shared

Every other sample needs this one imported beside it. It holds `SteamBootstrap`, which starts Steam for a sample scene, because the Steam transport never starts Steam itself.

It also holds `PlayerNameTag`, which draws a player's Steam name over their object, and `PlayerMover`, which walks a player's object with WASD or the arrow keys and puts it back above the middle when it falls off the ground. `PlayerMover` compiles only when the Input System package is installed. `UI/SteamSampleSkin` is the GUI skin every sample panel draws with, and `Materials/` holds the URP Lit ground and player materials.

## What it shows

- `SteamBootstrap` starts the player's Steam before any other sample component wakes, or adopts the Steam an earlier scene started.
- When an App ID is set, a build started outside Steam quits and is relaunched through Steam (`RestartAppIfNecessary`).
- It starts relay network access at once, so the first connection and the ping estimates do not wait for it.
- With **Game Server** on, it starts a Steam game server instead, with a game port, a query port and an optional login token. Without a token the server logs on anonymously. The Dedicated Server sample uses this.
- When the scene's **Steam Transport** is addressed by **Fake IP**, the game server asks Steam for a fake address between `GameServer.Init` and logging on, which is the only moment Steam accepts the request.
- It runs Steam's callbacks every frame. When the application quits, it stops the transports first and then shuts Steam down, so every link closes while Steam can still send the close.
- `PlayerNameTag` shows who controls an object. The server writes the controller's Steam ID from the link Steam authenticated, so a client cannot claim another player's name. Each client turns the Steam ID into a name through its own Steam, and asks Steam to fetch the name with `RequestUserInformation` when it does not know it yet.

## Using it

Put a `SteamBootstrap` on an object in each scene that uses Steam, beside the **Steam Transport**. It belongs to its scene. Steam itself outlives a scene load, so the next scene's bootstrap adopts the Steam that is already running. Put `steam_appid.txt` (480 for testing) in the project folder, or beside the executable in a build.

There is nothing to test on its own. Every other sample's two-account test runs it.

This sample is adapted from maxkratt's [nucleus-steamworksnet](https://github.com/maxkratt/nucleus-steamworksnet), under the MIT license.
