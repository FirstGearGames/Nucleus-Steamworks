// Matches Steamworks.NET's own switch, so this compiles out wherever Steamworks.NET does, not only where it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CodeBoost.Logging;
using Nucleus.Connections;
using Nucleus.Managers.Core;
using Nucleus.Managers.Messages;
using Nucleus.Transports;
using Steamworks;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// A <see cref="Transport"/> over Steam Networking Sockets, which reaches peers by Steam identity through Valve's relay network so
    /// nobody has to open a port or know an address.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Steam has to be initialised, and its callbacks run every frame, by whatever already does that in the game: Heathen's Foundation for
    /// Steamworks, a <c>SteamManager</c>, or the game's own code calling <c>SteamAPI.RunCallbacks</c>. This transport never initialises
    /// Steam itself, because a process holds one Steam session and the game owns it.
    /// </para>
    /// <para>
    /// Every read from Steam happens inside <see cref="Transport.ReceivePackets"/>, which the network loop calls from Unity's
    /// <c>Update</c>, so packets are polled on the main thread. Connection changes arrive through Steam's callback pump, which also runs on
    /// the main thread, and are only queued there; the engine hears of them from the same receive pass.
    /// </para>
    /// <para>
    /// A host's own client is paired with its server inside the process and never touches Steam: each packet passes between the two
    /// sockets as one pooled copy, and the server's roster entry for that client is the client socket's own Connection. The client does
    /// this on its own when its server is running and the client's target, <see cref="RemoteSteamId"/> or <see cref="RemoteAddress"/>,
    /// is left empty or names this peer.
    /// </para>
    /// </remarks>
    public class SteamNetworkingTransport : SocketPairTransport<SteamNetworkingServerSocket, SteamNetworkingClientSocket>
    {
        /// <summary>
        /// Raised when Steam refuses a packet either side of this transport hands it, carrying Steam's reason.
        /// </summary>
        /// <remarks>
        /// The packet is dropped, and the engine is told so, whatever a handler does. Raised on the main thread, from inside the send.
        /// <c>k_EResultLimitExceeded</c> means Steam's queue for that connection is full, usually because the send rate is below what the
        /// game sends; <c>k_EResultNoConnection</c> and <c>k_EResultInvalidState</c> mean the connection has closed or is not open yet.
        /// </remarks>
        public event SendFailedHandler SendFailed;
        /// <summary>
        /// Handler for the <see cref="SendFailed"/> event.
        /// </summary>
        /// <param name="invoker">Which side of this transport was sending.</param>
        /// <param name="connection">Who the packet was for.</param>
        /// <param name="channel">The channel it was sent on.</param>
        /// <param name="result">Steam's reason for refusing it.</param>
        public delegate void SendFailedHandler(Invoker invoker, Connection connection, Channel channel, EResult result);
        /// <summary>
        /// Raised when this peer learns a client's Steam ID, or learns that the client has left.
        /// </summary>
        /// <remarks>
        /// Raised on the main thread. A server hears of every client; a client hears of itself, and of every other client while the server
        /// has <see cref="ShareSteamIdsEnabled"/> on. It can come before or after the engine reports the same client, so read
        /// <see cref="TryGetSteamId(uint, out ulong)"/> from both places. It is not raised for the IDs dropped when a session ends.
        /// </remarks>
        public event SteamIdChangedHandler SteamIdChanged;
        /// <summary>
        /// Handler for the <see cref="SteamIdChanged"/> event.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>.</param>
        /// <param name="steamId">The client's Steam ID, or <see cref="UnsetSteamId"/> when the client has left.</param>
        public delegate void SteamIdChangedHandler(uint connectionId, ulong steamId);
        /// <summary>
        /// Decides whether a client asking to connect may go on, before the engine hears of it.
        /// </summary>
        /// <param name="steamId">Who is asking, as Steam authenticated it, or <see cref="UnsetSteamId"/> when Steam has no Steam ID for it.</param>
        /// <returns>True to accept the client.</returns>
        public delegate bool ConnectingClientFilterHandler(ulong steamId);

        /// <summary>
        /// How peers find each other: by Steam ID through Valve's relay network, or by IP address and port.
        /// </summary>
        /// <remarks>
        /// Read when the server starts and when the client connects. Under <see cref="SteamAddressing.IpAddress"/> the server listens on
        /// <see cref="Transport.Configuration"/>'s bind address and port.
        /// </remarks>
        public SteamAddressing Addressing { get; set; }

        /// <summary>
        /// The Steam ID of the peer a client connects to under <see cref="SteamAddressing.SteamId"/>: a player's own ID when that player
        /// hosts, or a game server's ID.
        /// </summary>
        /// <remarks>Read when the client connects. <see cref="UnsetSteamId"/>, or this peer's own ID, connects a host's client to its own server.</remarks>
        public ulong RemoteSteamId { get; set; }

        /// <summary>
        /// The IP address a client connects to under <see cref="SteamAddressing.IpAddress"/>, with an optional port such as
        /// <c>203.0.113.7:7777</c>.
        /// </summary>
        /// <remarks>
        /// Read when the client connects. Without a port the configured port is used. Empty, <c>localhost</c> or a loopback address on that
        /// port connects a host's client to its own server. Host names are not looked up.
        /// </remarks>
        public string RemoteAddress { get; set; } = string.Empty;

        /// <summary>
        /// Whether the server side uses Steam's game server interface rather than the signed-in player's.
        /// </summary>
        /// <remarks>
        /// Set it for a dedicated server that signs in as a Steam game server. A player hosting from the game leaves it off. Read when the
        /// server starts. A client always connects through the player's interface.
        /// </remarks>
        public bool IsGameServer { get; set; }

        /// <summary>
        /// The rate Steam sends at on every connection this transport opens, in kilobytes of 1,024 bytes a second, or zero to keep Steam's
        /// own.
        /// </summary>
        /// <remarks>
        /// Read when the server starts and when the client connects. Steam holds each connection to a fixed rate whatever the line beneath
        /// it could carry, and its own is 256 KiB a second, slower than nearly any player's connection: a join wider than a few dozen
        /// kilobytes a tick then queues behind it, and an object wider than that takes longer to arrive than a client waits for it.
        /// </remarks>
        public uint SendRateKilobytesPerSecond { get; set; } = DefaultSendRateKilobytesPerSecond;

        /// <summary>
        /// The longest a client waits for Steam to open its link before giving up, in milliseconds, or zero to wait on Steam's own
        /// timeout.
        /// </summary>
        /// <remarks>Read when the client connects. Has no effect on a host's own client, which pairs inside the process at once.</remarks>
        public uint ConnectTimeoutMilliseconds { get; set; }

        /// <summary>
        /// Whether a link addressed by Steam ID is carried over Valve's relay network only, so no peer learns another's IP address.
        /// </summary>
        /// <remarks>
        /// Off lets Steam try a direct route between two players when each player's Steam settings allow it, which can lower ping. Read
        /// when the server starts and when the client connects, and used only under <see cref="SteamAddressing.SteamId"/>.
        /// </remarks>
        public bool IsRelayOnlyEnabled { get; set; }

        /// <summary>
        /// Whether the server tells every client the Steam ID of every other client, so each client's
        /// <see cref="TryGetSteamId(uint, out ulong)"/> answers for all of them.
        /// </summary>
        /// <remarks>
        /// Only the server's setting counts. Off, a client knows only its own Steam ID. A Steam ID leads to the player's public Steam profile,
        /// so leave this off where players should not learn who the others are. Applied to clients as they arrive and leave.
        /// </remarks>
        public bool ShareSteamIdsEnabled;

        /// <summary>
        /// The most connections one Steam account may hold on the server at once, or <see cref="UnsetMaximumConnectionsPerSteamId"/> to
        /// accept any number.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Counted by the Steam ID Steam authenticated for each link, so one account cannot fill the server. A connection past the cap is
        /// refused, and no connection already held is ever closed to make room, so nobody can push another player off. While a cap is set,
        /// a connection whose Steam ID Steam could not authenticate is refused too, since it cannot be counted. The host's own client,
        /// paired in process, is not counted.
        /// </para>
        /// <para>
        /// Read as each client connects, so a change applies to a running server. A client that crashes keeps its place until Steam times
        /// its link out, ten seconds with Steam's own timeout, and the same account joins again after that. Several clients signed in to one
        /// Steam account, as when testing on one machine, need this raised.
        /// </para>
        /// </remarks>
        public uint MaximumConnectionsPerSteamId = DefaultMaximumConnectionsPerSteamId;

        /// <summary>
        /// A check every client asking to connect passes before the engine hears of it, such as a ban list or lobby members only, or
        /// <see langword="null"/> to accept anyone while the server has room.
        /// </summary>
        /// <remarks>
        /// Called on the main thread, from inside Steam's callback pump. A refused client is closed with a reason it logs. A handler that
        /// throws refuses the client. The host's own client, paired in process, never asks.
        /// </remarks>
        public ConnectingClientFilterHandler ConnectingClientFilter;

        /// <summary>
        /// The fake IP address and port Steam granted this server under <see cref="SteamAddressing.FakeIp"/>, which a client sets as
        /// <see cref="RemoteAddress"/>, or <see langword="null"/> until Steam grants one.
        /// </summary>
        /// <remarks>Steam grants it a moment after the server starts, and logs it then. It stays with this process across restarts.</remarks>
        public string ServerFakeAddress { get; internal set; }

        /// <summary>
        /// The Steam ID this peer's running server is reached at under <see cref="SteamAddressing.SteamId"/>, which a client sets as
        /// <see cref="RemoteSteamId"/>, or <see cref="UnsetSteamId"/> while no server runs.
        /// </summary>
        public ulong ServerSteamId => ServerSocket is { State: LocalConnectionState.Connected } serverSocket && !SteamSockets.IsShutDown(serverSocket.IsGameServer) ? SteamSockets.GetSteamId(serverSocket.IsGameServer) : UnsetSteamId;

        /// <summary>
        /// The default for <see cref="SendRateKilobytesPerSecond"/>, four mebibytes a second, well inside an ordinary home or mobile line.
        /// </summary>
        public const uint DefaultSendRateKilobytesPerSecond = 4_096;

        /// <summary>
        /// The default for <see cref="MaximumConnectionsPerSteamId"/>: one connection for each Steam account.
        /// </summary>
        public const uint DefaultMaximumConnectionsPerSteamId = 1;

        /// <summary>
        /// The value of <see cref="MaximumConnectionsPerSteamId"/> that lets one Steam account hold any number of connections.
        /// </summary>
        public const uint UnsetMaximumConnectionsPerSteamId = 0;

        /// <summary>
        /// The Steam ID that names no account: on <see cref="RemoteSteamId"/> it means this peer's own server, and from
        /// <see cref="SteamIdChanged"/> it means a client has left.
        /// </summary>
        public const ulong UnsetSteamId = 0;

        /// <summary>
        /// The launch-argument flag which sets <see cref="RemoteAddress"/>.
        /// </summary>
        public const string AddressArgument = "address";

        /// <summary>
        /// The launch-argument flag which sets <see cref="RemoteSteamId"/>.
        /// </summary>
        public const string SteamIdArgument = "steamid";

        /// <summary>
        /// Whether the client's last connect was closed by the far end before it opened, which is a peer saying it is not there or will
        /// not have this one, as opposed to a peer that never answered.
        /// </summary>
        internal bool IsLastConnectRefused => ClientSocket is not null && ClientSocket.IsLastConnectRefused;

        /// <summary>
        /// The conditions Steam is asked to simulate, or <see langword="null"/> for none.
        /// </summary>
        private NetworkConditions? _networkConditions;

        /// <summary>
        /// Whether Steam's simulation holds conditions this transport set, so clearing them has something to undo.
        /// </summary>
        /// <remarks>Steam's simulation is process-wide, so a transport that never set any leaves whatever the game set alone.</remarks>
        private bool _isNetworkSimulationApplied;

        /// <summary>
        /// The Steam ID of each client this peer knows of, by <see cref="Connection.Id"/>.
        /// </summary>
        private readonly Dictionary<uint, ulong> _steamIdsByConnectionId = new();

        /// <summary>
        /// The <see cref="Connection.Id"/> of each client this peer knows of, by Steam ID.
        /// </summary>
        /// <remarks>One account connected twice, which IP addressing allows, is held under its latest connection.</remarks>
        private readonly Dictionary<ulong, uint> _connectionIdsBySteamId = new();

        /// <summary>
        /// The Steam ID of the account owning the game license each client plays on, by <see cref="Connection.Id"/>, for every client
        /// Steam approved a ticket for on this server.
        /// </summary>
        private readonly Dictionary<uint, ulong> _ownerSteamIdsByConnectionId = new();

        /// <summary>
        /// Whether this transport is subscribed to the engine's events, so a second shutdown leaves them alone.
        /// </summary>
        private bool _isSubscribed;

        /// <inheritdoc/>
        /// <remarks>Also starts keeping the Steam ID of every client this peer learns of.</remarks>
        public override async Task InitializeAsync(CoreManager coreManager, uint transportId)
        {
            await base.InitializeAsync(coreManager, transportId);

            coreManager.ServerManager.ClientAuthenticated += ServerManager_OnClientAuthenticated;
            coreManager.ServerManager.ClientDisconnecting += ServerManager_OnClientDisconnecting;
            coreManager.ClientManager.LocalClientAuthenticated += ClientManager_OnLocalClientAuthenticated;
            coreManager.TransportManager.ConnectionLocalStateChanged += TransportManager_OnConnectionLocalStateChanged;
            coreManager.MessageManager.RegisterMessageHandler<SteamIdNotice>(OnSteamIdNoticeReceived);

            _isSubscribed = true;
        }

        /// <summary>
        /// Reads the Steam ID of a connected client.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>, such as a system's controlling connection.</param>
        /// <param name="steamId">The client's Steam ID, as Steam authenticated its link.</param>
        /// <returns>
        /// True when this peer knows it: on a server for every authenticated client, and on a client for itself, or for every client while
        /// the server has <see cref="ShareSteamIdsEnabled"/> on and has said so.
        /// </returns>
        public bool TryGetSteamId(uint connectionId, out ulong steamId) => _steamIdsByConnectionId.TryGetValue(connectionId, out steamId);

        /// <summary>
        /// Reads the Steam ID of a connected client.
        /// </summary>
        /// <param name="connection">The client's Connection, on any peer.</param>
        /// <param name="steamId">The client's Steam ID, as Steam authenticated its link.</param>
        /// <returns>True when this peer knows it, as for <see cref="TryGetSteamId(uint, out ulong)"/>.</returns>
        public bool TryGetSteamId(Connection connection, out ulong steamId)
        {
            steamId = UnsetSteamId;

            return connection is not null && TryGetSteamId(connection.Id, out steamId);
        }

        /// <summary>
        /// Finds the Connection of the client playing as a Steam ID.
        /// </summary>
        /// <param name="steamId">The Steam ID to find.</param>
        /// <param name="connection">This peer's Connection for that client.</param>
        /// <returns>True when this peer knows the Steam ID and holds a Connection for its client.</returns>
        public bool TryGetConnection(ulong steamId, out Connection connection)
        {
            connection = null;

            return _connectionIdsBySteamId.TryGetValue(steamId, out uint connectionId) && CoreManager.TransportManager.TryGetConnection(connectionId, out connection);
        }

        /// <summary>
        /// Reads the Steam ID of the account that owns the game license a connected client plays on, which a ban list can check as well
        /// as the client's own Steam ID.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>.</param>
        /// <param name="ownerSteamId">
        /// The license owner's Steam ID: the client's own, or under Family Sharing the account that lent it the game.
        /// </param>
        /// <returns>
        /// True on a server running a <see cref="SteamTicketAuthenticator"/>, for each client Steam approved a ticket for. The host's own
        /// client, admitted without a ticket, has none.
        /// </returns>
        /// <remarks>
        /// Steam names the owner with its verdict on the ticket, and it is recorded before the client is approved, so a
        /// <c>ServerManager.ClientAuthenticated</c> handler can already read it. A player borrowing the game plays under its own Steam ID, so
        /// a ban list that checks Steam IDs alone lets a banned account come back by lending the game to another account.
        /// </remarks>
        public bool TryGetOwnerSteamId(uint connectionId, out ulong ownerSteamId) => _ownerSteamIdsByConnectionId.TryGetValue(connectionId, out ownerSteamId);

        /// <summary>
        /// Reads the Steam ID of the account that owns the game license a connected client plays on.
        /// </summary>
        /// <param name="connection">The client's Connection on this server.</param>
        /// <param name="ownerSteamId">The license owner's Steam ID.</param>
        /// <returns>True when this server knows it, as for <see cref="TryGetOwnerSteamId(uint, out ulong)"/>.</returns>
        public bool TryGetOwnerSteamId(Connection connection, out ulong ownerSteamId)
        {
            ownerSteamId = UnsetSteamId;

            return connection is not null && TryGetOwnerSteamId(connection.Id, out ownerSteamId);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// Simulated by Steam itself, through its fake lag, loss, reordering and duplication, and can change mid-session. Steam applies them
        /// on send, so latency set on two peers adds to the round trip twice. Jitter averages half of
        /// <see cref="NetworkConditions.JitterMilliseconds"/> and never exceeds it, a reordered packet is held back 100 ms, and
        /// <see cref="NetworkConditions.Seed"/> is ignored.
        /// </para>
        /// <para>
        /// Steam keeps these for the whole process, so they apply to every Steam connection it has open, this transport's or not. A host's
        /// own client, paired in process, never crosses Steam and is unaffected; test from a remote client.
        /// </para>
        /// </remarks>
        public override void SetNetworkConditions(NetworkConditions? networkConditions)
        {
            _networkConditions = networkConditions;

            ApplyNetworkConditions();
        }

        /// <inheritdoc/>
        public override NetworkConditions? GetNetworkConditions() => _networkConditions;

        /// <inheritdoc/>
        /// <remarks>
        /// Also clears Steam's simulated conditions when this transport set any, since Steam keeps them for the whole process, which in the
        /// editor outlives the play session.
        /// </remarks>
        public override Task ShutdownAsync()
        {
            if (_isNetworkSimulationApplied)
            {
                SteamSockets.TryApplyNetworkConditions(isGameServer: false, networkConditions: default);
                SteamSockets.TryApplyNetworkConditions(isGameServer: true, networkConditions: default);

                _isNetworkSimulationApplied = false;
            }

            if (_isSubscribed)
            {
                CoreManager.ServerManager.ClientAuthenticated -= ServerManager_OnClientAuthenticated;
                CoreManager.ServerManager.ClientDisconnecting -= ServerManager_OnClientDisconnecting;
                CoreManager.ClientManager.LocalClientAuthenticated -= ClientManager_OnLocalClientAuthenticated;
                CoreManager.TransportManager.ConnectionLocalStateChanged -= TransportManager_OnConnectionLocalStateChanged;
                CoreManager.MessageManager.UnregisterMessageHandler<SteamIdNotice>(OnSteamIdNoticeReceived);

                _isSubscribed = false;
            }

            ForgetAllSteamIds();

            return base.ShutdownAsync();
        }

        /// <inheritdoc/>
        /// <remarks>Steam traffic is always a real network path, even between two clients on one machine, so the security checks stay on.</remarks>
        public override bool IsLocalTransport() => false;

        /// <inheritdoc/>
        /// <remarks>Takes effect for connections accepted after the call, including on a server that is already running.</remarks>
        public override void SetMaximumConnections(uint maximumConnections) => ServerConfiguration.MaximumConnections = maximumConnections;

        /// <inheritdoc/>
        /// <remarks>
        /// Used only under <see cref="SteamAddressing.IpAddress"/>, and read when the server starts. Empty listens on every interface; a
        /// value that is not an IP address fails the server start rather than this call.
        /// </remarks>
        public override bool SetBindAddress(string bindAddress)
        {
            Configuration.BindAddress = bindAddress;

            return true;
        }

        /// <summary>
        /// Reads Steam's live status of the link carrying a Connection: its ping, quality, rates and what is queued on it.
        /// </summary>
        /// <param name="connection">A remote client on this peer's server, or, on a client, its link to the server.</param>
        /// <param name="linkStatus">The status, when there is a Steam link to read.</param>
        /// <returns>True when the Connection rides a Steam link. A host's own client, paired in process, has none.</returns>
        /// <remarks>
        /// Steamworks.NET allocates a little marshalling each call, so read it at a display's pace, such as a few times a second, rather
        /// than every frame.
        /// </remarks>
        public bool TryGetLinkStatus(Connection connection, out SteamLinkStatus linkStatus)
        {
            if (ClientSocket is not null && ClientSocket.TryGetLinkStatus(connection, out linkStatus))
                return true;

            if (ServerSocket is not null && ServerSocket.TryGetLinkStatus(connection, out linkStatus))
                return true;

            linkStatus = default;

            return false;
        }

        /// <summary>
        /// Asks <see cref="ConnectingClientFilter"/> whether a client asking to connect may go on.
        /// </summary>
        /// <param name="steamId">Who is asking.</param>
        /// <returns>True when there is no filter or it accepted the client; false when it refused or threw.</returns>
        internal bool IsConnectingClientAllowed(ulong steamId)
        {
            ConnectingClientFilterHandler connectingClientFilter = ConnectingClientFilter;

            if (connectingClientFilter is null)
                return true;

            try
            {
                return connectingClientFilter(steamId);
            }
            catch (Exception exception)
            {
                Logger<SteamNetworkingTransport>.LogError($"The [{nameof(ConnectingClientFilter)}] threw, so Steam client [{steamId}] was refused: [{exception}].");

                return false;
            }
        }

        /// <summary>
        /// Raises <see cref="SendFailed"/>, keeping a handler that throws from breaking the send it was raised from.
        /// </summary>
        /// <param name="invoker">Which side of this transport was sending.</param>
        /// <param name="connection">Who the packet was for.</param>
        /// <param name="channel">The channel it was sent on.</param>
        /// <param name="result">Steam's reason for refusing it.</param>
        internal void NotifySendFailed(Invoker invoker, Connection connection, Channel channel, EResult result)
        {
            try
            {
                SendFailed?.Invoke(invoker, connection, channel, result);
            }
            catch (Exception exception)
            {
                Logger<SteamNetworkingTransport>.LogError($"A [{nameof(SendFailed)}] handler threw: [{exception}].");
            }
        }

        /// <summary>
        /// Records the account Steam named as owning the game license of a client whose ticket it approved.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>.</param>
        /// <param name="ownerSteamId">The license owner's Steam ID, from Steam's verdict on the client's ticket.</param>
        internal void RecordOwnerSteamId(uint connectionId, ulong ownerSteamId) => _ownerSteamIdsByConnectionId[connectionId] = ownerSteamId;

        /// <summary>
        /// Writes the conditions set through <see cref="SetNetworkConditions"/> into Steam's simulation on each interface that is up.
        /// </summary>
        /// <remarks>
        /// Called again at each server and client start, since an interface can come up after the conditions were set. Does nothing while
        /// no conditions were ever set, so a game's own simulation settings are kept.
        /// </remarks>
        internal void ApplyNetworkConditions()
        {
            if (_networkConditions is null && !_isNetworkSimulationApplied)
                return;

            NetworkConditions networkConditions = _networkConditions ?? default;
            bool isClientApplied = SteamSockets.TryApplyNetworkConditions(isGameServer: false, networkConditions);
            bool isGameServerApplied = SteamSockets.TryApplyNetworkConditions(isGameServer: true, networkConditions);

            if (isClientApplied || isGameServerApplied)
                _isNetworkSimulationApplied = _networkConditions is not null;
        }

        /// <inheritdoc/>
        /// <remarks>Adds <see cref="AddressArgument"/> and <see cref="SteamIdArgument"/>, which set where the client connects.</remarks>
        protected override void OnApplyCommandLineArguments(CommandLineArguments commandLineArguments)
        {
            base.OnApplyCommandLineArguments(commandLineArguments);

            if (commandLineArguments.TryGetValue(AddressArgument, out string address))
                RemoteAddress = address;

            if (!commandLineArguments.TryGetValue(SteamIdArgument, out string steamIdText))
                return;

            if (ulong.TryParse(steamIdText, out ulong steamId))
                RemoteSteamId = steamId;
            else
                Logger<SteamNetworkingTransport>.LogError($"The launch argument [{SteamIdArgument}] value [{steamIdText}] is not a Steam ID.");
        }

        /// <summary>
        /// Records an authenticated client's Steam ID on the server, and tells the other clients when <see cref="ShareSteamIdsEnabled"/> is on.
        /// </summary>
        /// <param name="connection">The client.</param>
        /// <remarks>A client's address on this transport is the Steam ID Steam authenticated for its link.</remarks>
        private void ServerManager_OnClientAuthenticated(Connection connection)
        {
            if (connection.Transport != this || !ulong.TryParse(connection.RemoteAddress, out ulong steamId) || steamId == UnsetSteamId)
                return;

            uint connectionId = connection.Id;

            RecordSteamId(connectionId, steamId);

            if (!ShareSteamIdsEnabled)
                return;

            CoreManager.ServerManager.SendMessageExcept(Channel.Reliable, new SteamIdNotice { ConnectionId = connectionId, SteamId = steamId }, connection);

            // The host's own client reads this peer's records directly.
            if (IsOwnClientConnection(connection))
                return;

            foreach (KeyValuePair<uint, ulong> steamIdByConnectionId in _steamIdsByConnectionId)
                connection.SendMessage(Channel.Reliable, new SteamIdNotice { ConnectionId = steamIdByConnectionId.Key, SteamId = steamIdByConnectionId.Value });
        }

        /// <summary>
        /// Forgets a departing client's Steam ID and license owner on the server, and tells the remaining clients it left.
        /// </summary>
        /// <param name="connection">The departing client.</param>
        private void ServerManager_OnClientDisconnecting(Connection connection)
        {
            // Forgotten on every departure: a client whose approval arrived while a kick was pending was never announced, so it holds an owner here and no Steam ID.
            _ownerSteamIdsByConnectionId.Remove(connection.Id);

            if (!TryForgetSteamId(connection.Id))
                return;

            // The server's stop walks its clients too, and by then there is nobody left to tell.
            if (ShareSteamIdsEnabled && CoreManager.TransportManager.IsServerStarted)
                CoreManager.ServerManager.SendMessageExcept(Channel.Reliable, new SteamIdNotice { ConnectionId = connection.Id, SteamId = UnsetSteamId }, connection);
        }

        /// <summary>
        /// Records this client's own Steam ID once the server has given its Connection an id.
        /// </summary>
        /// <param name="localClientConnection">This peer's own client Connection.</param>
        /// <remarks>A host skips it, since its server recorded the same client when it authenticated.</remarks>
        private void ClientManager_OnLocalClientAuthenticated(Connection localClientConnection)
        {
            if (localClientConnection.Transport == this && !CoreManager.TransportManager.IsServerStarted)
                RecordSteamId(localClientConnection.Id, SteamSockets.GetSteamId(isGameServer: false));
        }

        /// <summary>
        /// Applies the server's word about a client's Steam ID.
        /// </summary>
        /// <param name="messageContext">The context the notice arrived in.</param>
        /// <param name="steamIdNotice">The notice.</param>
        /// <remarks>Only the server may say who a client is; a notice from a client would let it claim another player's account.</remarks>
        private void OnSteamIdNoticeReceived(in MessageContext messageContext, SteamIdNotice steamIdNotice)
        {
            if (!messageContext.IsSenderServer || CoreManager.TransportManager.IsServerStarted)
                return;

            if (steamIdNotice.SteamId == UnsetSteamId)
                TryForgetSteamId(steamIdNotice.ConnectionId);
            else
                RecordSteamId(steamIdNotice.ConnectionId, steamIdNotice.SteamId);
        }

        /// <summary>
        /// Forgets every Steam ID when this peer's session ends.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        /// <remarks>A host whose client half drops keeps its server's records.</remarks>
        private void TransportManager_OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (connectionStateChange.IsRemoteStateChange || connectionStateChange.Connection.LocalState is not LocalConnectionState.Disconnected)
                return;

            if (connectionStateChange.Invoker is Invoker.Server || !CoreManager.TransportManager.IsServerStarted)
                ForgetAllSteamIds();
        }

        /// <summary>
        /// Records a client's Steam ID and reports it, unless this peer already holds that value.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>.</param>
        /// <param name="steamId">The client's Steam ID.</param>
        private void RecordSteamId(uint connectionId, ulong steamId)
        {
            if (_steamIdsByConnectionId.TryGetValue(connectionId, out ulong recordedSteamId) && recordedSteamId == steamId)
                return;

            _steamIdsByConnectionId[connectionId] = steamId;
            _connectionIdsBySteamId[steamId] = connectionId;

            InvokeSteamIdChanged(connectionId, steamId);
        }

        /// <summary>
        /// Forgets a client's Steam ID and reports that it left.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>.</param>
        /// <returns>True when this peer held a Steam ID for the client.</returns>
        private bool TryForgetSteamId(uint connectionId)
        {
            if (!_steamIdsByConnectionId.Remove(connectionId, out ulong steamId))
                return false;

            // One account connected twice is held under its latest connection, which an earlier one leaving must not remove.
            if (_connectionIdsBySteamId.TryGetValue(steamId, out uint recordedConnectionId) && recordedConnectionId == connectionId)
                _connectionIdsBySteamId.Remove(steamId);

            InvokeSteamIdChanged(connectionId, UnsetSteamId);

            return true;
        }

        /// <summary>
        /// Drops every Steam ID this peer holds, without reporting them.
        /// </summary>
        private void ForgetAllSteamIds()
        {
            _steamIdsByConnectionId.Clear();
            _connectionIdsBySteamId.Clear();
            _ownerSteamIdsByConnectionId.Clear();
        }

        /// <summary>
        /// Raises <see cref="SteamIdChanged"/>, keeping a handler that throws from breaking the engine event it was raised from.
        /// </summary>
        /// <param name="connectionId">The client's <see cref="Connection.Id"/>.</param>
        /// <param name="steamId">The client's Steam ID, or <see cref="UnsetSteamId"/> when it left.</param>
        private void InvokeSteamIdChanged(uint connectionId, ulong steamId)
        {
            try
            {
                SteamIdChanged?.Invoke(connectionId, steamId);
            }
            catch (Exception exception)
            {
                Logger<SteamNetworkingTransport>.LogError($"A [{nameof(SteamIdChanged)}] handler threw: [{exception}].");
            }
        }
    }
}
#endif
