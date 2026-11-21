# Quick Start

The smallest Steam session. One player hosts, a friend pastes the host's Steam ID and joins through Valve's relay network, and each player moves a capsule with WASD or the arrow keys.

## Requirements

- Nucleus, this package, Steamworks.NET and the Input System package are installed.
- The Shared sample is imported beside this one.
- Steam is running and signed in, and `steam_appid.txt` (480 for testing) is in the project folder, or beside the executable in a build.

## Setup

1. Open `Scenes/SteamQuickStart.unity`.
2. Open **Tools > Nucleus**, and on its **Build** tab press **Rebuild** beside **Rebuild Network Prefab Collection** once, so `QuickStartPlayer` is registered.

## Testing with two accounts

1. On the first machine, press Play (or run a build) and press **Host**. Then press **Copy my Steam ID** and send it to the second account.
2. On the second machine, signed into another Steam account, paste the Steam ID and press **Join**.
3. Each player's capsule appears in its own colour. Move yours and watch it move on the other screen. **Leave** stops the session.

## What to look at

- The `NetworkManager` object holds the whole setup: `UnityCoreManager`, `UnityTransportManager`, `SteamTransport` with **Addressing** on **Steam ID**, `SteamBootstrap`, `NetworkPlayerSpawner` and `QuickStartPanel`.
- `QuickStartPanel.Join` is all a join takes. It sets `SteamTransport.RemoteSteamId` to the host's Steam ID and calls `StartClientAsync`.
- `PlayerMover`, in Shared, moves the capsule only on the peer that controls it, and `NetworkTransform` replicates it to everyone else. Walk off the ground and the capsule falls, then drops back in above the middle.
