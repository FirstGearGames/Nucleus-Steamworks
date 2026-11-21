# Steam transport settings

## SteamTransport component (Unity, `Nucleus.Integrations.Unity.Transports`)

Values are copied into the engine `SteamNetworkingTransport` each time the `UnityTransportManager` adds it: at the first start, and at the next start after `ShutdownAll()`, which removes it. An inspector edit made while playing applies then. Set from code, `RemoteSteamId`, `RemoteAddress`, `ConnectingClientFilter` and `ClientAdmissionFilter` also write through to the transport already added.

| Inspector field (serialized field) | Engine member | Default | Meaning |
|---|---|---|---|
| Addressing (`_addressing`) | `Addressing` | `SteamAddressing.SteamId` | `SteamId` reaches the server by Steam ID through Valve's relay. `IpAddress` listens on a port and connects to an address. `FakeIp` connects to an address Steam grants the server, over Valve's relay. Read when the server starts and when the client connects. |
| Remote Steam Id (`_remoteSteamId`) | `RemoteSteamId` | `0` | The Steam ID a client connects to under Steam ID. 0, or this peer's own ID, joins its own server. Read when the client connects. Shown for Steam ID. |
| Is Relay Only Enabled (`_isRelayOnlyEnabled`) | `IsRelayOnlyEnabled` | `false` | Carries Steam ID links over Valve's relay only, so no player learns another's IP address. Off lets Steam connect two players directly when their Steam settings allow it. Used only under Steam ID. |
| Remote Address (`_remoteAddress`) | `RemoteAddress` | empty | The address a client connects to under IP Address or Fake IP, with an optional port, such as `203.0.113.7:7777`. Empty, `localhost` or a loopback address joins its own server. Host names are not looked up. Read when the client connects. |
| Port (`_port`) | `Configuration.Port` | `7777` | The port the server listens on under IP Address, and the port a client uses when its address names none. Shown for IP Address. |
| Bind Address (`_bindAddress`) | `SetBindAddress(string)` | empty | The local address the server listens on under IP Address. Empty listens on every interface. A value that is not an IP address fails the server start. Shown for IP Address. |
| Is Game Server (`_isGameServer`) | `IsGameServer` | `false` | Serves through Steam's game server interface, for a dedicated server. Leave it off when a player hosts. A client always uses the player's interface. Read when the server starts. |
| Maximum Connections (`_maximumConnections`) | `SetMaximumConnections(uint)` | `0` | The most clients the server accepts at once. 0 accepts any number. A host's own client takes one place. Applies to connections accepted after the call, on a running server too. |
| Maximum Transmission Unit (`_maximumTransmissionUnit`) | `Configuration.MaximumTransmissionUnit` | `1200` | The largest datagram built before it is split. A server accepts a message of up to 255 times this from a client. |
| Send Rate Kilobytes Per Second (`_sendRateKilobytesPerSecond`) | `SendRateKilobytesPerSecond` | `4096` (`DefaultSendRateKilobytesPerSecond`) | The rate Steam sends at on each connection, in kilobytes of 1,024 bytes a second. Steam's own is 256, slower than nearly any player's line, so a large join queues behind it. 0 keeps Steam's own. Read when the server starts and when the client connects. |
| Ticket Authentication Enabled (`_ticketAuthenticationEnabled`) | makes a `SteamTicketAuthenticator` | `false` | Admits a client only once Steam confirms its auth session ticket: the account owns the game and is not banned. Server and clients both need it on. |
| Share Steam Ids Enabled (`_shareSteamIdsEnabled`) | `ShareSteamIdsEnabled` | `false` | The server tells every client the Steam ID of every other client. Only the server's value counts. |

Script members on the component: `SteamNetworkingTransport` (the engine transport, `null` until it is added), `ConnectingClientFilter` (`SteamNetworkingTransport.ConnectingClientFilterHandler`, `bool (ulong steamId)`), `ClientAdmissionFilter` (`SteamTicketAuthenticator.ClientAdmissionFilterHandler`, `bool (Connection connection, ulong steamId)`, used only while Ticket Authentication is on), and the start failure texts `SteamworksMissingText` and `SteamworksDisabledText`.

## SteamNetworkingTransport (engine, `Nucleus.Integrations.Unity.Transports`)

Members with no inspector field:

| Member | Default | Meaning |
|---|---|---|
| `ConnectTimeoutMilliseconds` | `0` | The longest a client waits for Steam to open its link. 0 waits on Steam's own connect timeout, 10 seconds. No effect on a host's own client. |
| `ConnectingClientFilter` | `null` | Asked for every client before the engine hears of it; false refuses it. Runs on the main thread inside Steam's callback pump. A handler that throws refuses the client. |
| `ServerSteamId` | `UnsetSteamId` | Read only. The Steam ID this peer's running server is reached at under Steam ID. |
| `ServerFakeAddress` | `null` | Read only. The fake IP address and port Steam granted this server under Fake IP. It stays with the process across server restarts. |
| `UnsetSteamId` | `0` | The Steam ID that names no account. |

Methods and events:

- `TryGetSteamId(uint connectionId, out ulong steamId)` and `TryGetSteamId(Connection connection, out ulong steamId)` read a client's Steam ID. True on a server for every authenticated client, and on a client for itself, or for every client while the server has `ShareSteamIdsEnabled` on.
- `TryGetConnection(ulong steamId, out Connection connection)` finds this peer's Connection for a Steam ID.
- `SteamIdChanged`, `(uint connectionId, ulong steamId)`, is raised on the main thread when this peer learns a client's Steam ID, or with `UnsetSteamId` when the client leaves. It is not raised for the IDs dropped when a session ends.
- `SendFailed`, `(Invoker invoker, Connection connection, Channel channel, EResult result)`, is raised on the main thread when Steam refuses a packet. The packet is dropped whatever a handler does. `k_EResultLimitExceeded` means Steam's queue for that connection is full; `k_EResultNoConnection` and `k_EResultInvalidState` mean the connection has closed or is not open yet.
- `TryGetLinkStatus(Connection connection, out SteamLinkStatus linkStatus)` reads Steam's live status of a link. `SteamLinkStatus` holds `PingMilliseconds`, `LocalQuality`, `RemoteQuality`, `OutBytesPerSecond`, `InBytesPerSecond`, `SendRateBytesPerSecond`, `PendingReliableBytes`, `PendingUnreliableBytes` and `SentUnacknowledgedReliableBytes`. False for a host's own client.
- `SetNetworkConditions(NetworkConditions?)` has Steam simulate the conditions on its sends, and can change mid-session. Latency set on two peers adds to the round trip twice. Jitter averages half of `JitterMilliseconds` and never exceeds it, a reordered packet is held back 100 ms, and `Seed` is ignored. Steam keeps the conditions for the whole process; a transport that set any clears them when it shuts down.

## SteamHostMigration component (Nucleus Pro)

| Inspector field (serialized field) | Default | Meaning |
|---|---|---|
| Directory Address (`_directoryAddress`) | `newfarm.nucleus-networking.com` (`NetworkHostMigration.PublicDirectoryAddress`) | The Newfarm directory sessions register with: an IP address or a host name, looked up through DNS each time a session is opened or joined. |
| Directory Port (`_directoryPort`) | `47778` (`NetworkHostMigration.DefaultDirectoryPort`) | The port the directory listens on. |
| Hosted Address (`_hostedAddress`) | empty | The address other peers reach this peer at when it hosts by IP address. Empty uses the bind address, or this machine's own IPv4 address, which reaches only the same network. Read when the transport is added. Used only under IP Address. |

Script members: `HostedAddress`, plus those of `NetworkHostMigration`: `Migration`, `SessionId`, `DirectoryAddress`, `DirectoryPort`, `StartHostingSessionAsync()`, `JoinSessionAsync(ulong sessionId)` and `TrySurrenderHostingSession()`.

## SteamLinkProbe component (`Diagnostics`)

| Inspector field (serialized field) | Default | Meaning |
|---|---|---|
| Role (`_role`) | `SteamLinkProbeRole.Host` | `Host` starts a server with its own client and echoes each probe. `Client` connects to the host's Steam ID and sends the probes. |
| Host Steam Id (`_hostSteamId`) | `0` | Client only: the host's Steam ID, as the host logs it when it starts. |
| Interval Seconds (`_intervalSeconds`) | `1` | Seconds between probes and between log lines. |
| Is Steam Started Here (`_isSteamStartedHere`) | `true` | Starts Steam and runs its callbacks here. Turn it off when something else in the game already does. |

## Launch arguments

Read only when the game calls `ApplyCommandLineArguments()` on the transport.

| Flag | Constant | Sets |
|---|---|---|
| `-port` | `Transport.PortArgument` | `Configuration.Port` |
| `-address` | `SteamNetworkingTransport.AddressArgument` | `RemoteAddress` |
| `-steamid` | `SteamNetworkingTransport.SteamIdArgument` | `RemoteSteamId`; a value that is not a Steam ID logs an error |
