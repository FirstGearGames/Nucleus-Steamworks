using System;
using System.Threading.Tasks;
using CodeBoost.Logging;
using Nucleus.Connections;
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
        /// <remarks>Read when the client connects. Zero, or this peer's own ID, connects a host's client to its own server.</remarks>
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
        /// The default for <see cref="SendRateKilobytesPerSecond"/>, four mebibytes a second, well inside an ordinary home or mobile line.
        /// </summary>
        public const uint DefaultSendRateKilobytesPerSecond = 4_096;

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
    }
}
