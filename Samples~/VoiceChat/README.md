# Voice Chat

Push to talk over Steam voice. Hold V, and Steam records and compresses your voice. Each chunk goes out as an unreliable Nucleus remote call on your player's object, the server relays it to the other players, and each of them decompresses it and plays it on your capsule's `AudioSource`. A capsule lights up green, and a speaking icon shows over its name tag, while its player talks. Move your capsule with WASD or the arrow keys.

## Requirements

- Nucleus, this package, Steamworks.NET and the Input System package are installed.
- The Shared sample is imported beside this one.
- Steam is running and signed in with a microphone it can use (**Steam > Settings > Voice**), and `steam_appid.txt` (480 for testing) is in the project folder, or beside the executable in a build.

## Setup

1. Open `Scenes/SteamVoiceChat.unity`.
2. Open **Tools > Nucleus**, and on its **Build** tab press **Rebuild** beside **Rebuild Network Prefab Collection** once, so `VoiceChatPlayer` is registered.

## Testing with two accounts

1. On the first machine, press Play (or run a build) and press **Host**. Then press **Copy my Steam ID** and send it to the second account.
2. On the second machine, signed into another Steam account, paste the Steam ID and press **Join**.
3. Hold V on either machine and talk. The other machine hears you, and sees your capsule light up and the speaking icon over your name. You never hear yourself.

## What to look at

- `VoiceChatPlayer.SendRecordedVoice` reads Steam's voice with `SteamUser.GetAvailableVoice` and `GetVoice`, then sends it with `TrySendRpc(RpcTarget.Observers, Channel.Unreliable, ...)`. It is unreliable because a lost chunk is only a short gap, while a resent one would arrive too late to play.
- `VoiceChatPlayer.OnVoiceReceived` decompresses each chunk with `SteamUser.DecompressVoice` at Steam's best sample rate into a one-second ring that a streamed `AudioClip` plays from. It gathers 0.1 seconds before it starts, so uneven arrival does not stutter. A dedicated server only relays.
- Voice is not positional. Set the spatial blend of the `AudioSource` on the prefab to make it so.
- `VoiceChatPanel` is the Quick Start panel (host, join by Steam ID, leave), so this sample needs nothing but Shared.
- The sample's voice call, `VoiceChatRpc`, is a network type, so the sample's assembly has a `NetworkBundle` of its own in `Scripts/AssemblyInfo.cs`.

This sample is adapted from maxkratt's [nucleus-steamworksnet](https://github.com/maxkratt/nucleus-steamworksnet), under the MIT license.
