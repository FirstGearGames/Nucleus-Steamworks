using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using CodeBoost.Logging;
using Nucleus.Connections;
using Nucleus.Packets;
using Nucleus.Transports;
using Nucleus.Transports.Sockets;
using Steamworks;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// The client side of a <see cref="SteamNetworkingTransport"/>. Connects to <see cref="SteamNetworkingTransport.RemoteSteamId"/> or
    /// <see cref="SteamNetworkingTransport.RemoteAddress"/>, or pairs with this peer's own server inside the process when it hosts.
    /// </summary>
    public class SteamNetworkingClientSocket : CommonSocket
    {
        /// <inheritdoc/>
        public override bool IsServer { get; protected set; } = false;

        /// <inheritdoc/>
        public override bool IsEmulated { get; protected set; } = false;

        /// <summary>
        /// Whether the last connect was closed by the far end before it opened, rather than timing out or failing on this peer.
        /// </summary>
        /// <remarks>Set by a connect and kept after it fails, so whoever asked can tell a peer that refused from one that never answered.</remarks>
        internal bool IsLastConnectRefused { get; private set; }

        /// <summary>
        /// Native message pointers filled by each receive call.
        /// </summary>
        private readonly IntPtr[] _messages = new IntPtr[SteamMessages.MaximumMessagesPerReceive];

        /// <summary>
        /// The connection to a remote server, or invalid while disconnected or paired in process.
        /// </summary>
        private HSteamNetConnection _steamConnection = HSteamNetConnection.Invalid;

        /// <summary>
        /// This transport's own server socket while this client is paired with it in process, or null.
        /// </summary>
        /// <remarks>A paired client never touches Steam: packets pass between the two sockets as pooled copies.</remarks>
        private SteamNetworkingServerSocket _pairedServerSocket;

        /// <summary>
        /// Steam's report of the connection opening and closing.
        /// </summary>
        private Callback<SteamNetConnectionStatusChangedCallback_t> _connectionStatusChangedCallback;

        /// <summary>
        /// True once Steam reports the connection open.
        /// </summary>
        private bool _isSteamConnected;

        /// <summary>
        /// True once Steam reports the connection closed, until the engine is told.
        /// </summary>
        private bool _isSteamClosed;

        /// <summary>
        /// True when the link drop is due on the next receive pass.
        /// </summary>
        /// <remarks>
        /// A close is applied one pass after it is seen, so the messages read in the pass that saw it, a kick's reason among them, reach
        /// the game before it hears the link has gone.
        /// </remarks>
        private bool _isLinkDropDue;

        /// <summary>
        /// Stands in as the sender on inbound packets.
        /// </summary>
        /// <remarks>
        /// The engine attributes a client's inbound packet through the transport, so the server socket's own Connection serves. That
        /// Connection holds this link's applied-tick watermarks, so it is recycled with the link unless this peer is hosting.
        /// </remarks>
        private Connection _serverStandIn;

        /// <summary>
        /// How often a connect checks whether Steam has answered.
        /// </summary>
        private const int ConnectPollIntervalMilliseconds = 5;

        /// <summary>
        /// Creates a new instance.
        /// </summary>
        public SteamNetworkingClientSocket() { }

        /// <inheritdoc/>
        public override void Initialize(Transport transport)
        {
            base.Initialize(transport);
            InitializeConnection();
        }

        /// <summary>
        /// Pairs with this peer's own server when it hosts, or otherwise connects to the configured Steam ID or IP address and reports
        /// connected once Steam has.
        /// </summary>
        /// <returns>The result of the state change.</returns>
        /// <remarks>
        /// Gives up after <see cref="SteamNetworkingTransport.ConnectTimeoutMilliseconds"/>, or when Steam does if that is zero.
        /// </remarks>
        protected override async Task<ConnectionStateChangeResult> ConnectAsync()
        {
            // A link already up, or on its way up, is left as it is.
            if (State is LocalConnectionState.Connected or LocalConnectionState.Connecting || _steamConnection != HSteamNetConnection.Invalid)
                return ConnectionStateChangeResult.AlreadyInState;

            SteamNetworkingTransport steamNetworkingTransport = (SteamNetworkingTransport)Transport;
            SteamNetworkingServerSocket serverSocket = steamNetworkingTransport.ServerSocket;
            ulong remoteSteamId = steamNetworkingTransport.RemoteSteamId;
            uint connectTimeoutMilliseconds = steamNetworkingTransport.ConnectTimeoutMilliseconds;

            IsLastConnectRefused = false;

            // A link the server closed is released here before the next one is made.
            ReleaseSteam();

            /* Recycled rather than reused, so a new link carries none of the last one's identity, authentication or watermarks. The
             * stand-in is kept only by a host, whose server socket is live and whose Connection describes the server it is running. */
            RecycleConnection();

            bool isHosting = serverSocket.State is LocalConnectionState.Connected;

            serverSocket.RecycleStandInUnlessHosting();

            _serverStandIn = serverSocket.Connection;

            bool isIpAddressing = steamNetworkingTransport.Addressing is SteamAddressing.IpAddress;
            string remoteAddress = steamNetworkingTransport.RemoteAddress;
            ushort port = steamNetworkingTransport.Configuration.Port;
            SteamNetworkingIPAddr remoteIpAddress = default;
            bool isRemoteIpAddressRead = isIpAddressing && SteamSockets.TryParseIpAddress(remoteAddress, port, out remoteIpAddress);

            if (isHosting)
            {
                bool isOwnServerNamed = isIpAddressing
                    ? string.IsNullOrEmpty(remoteAddress) || isRemoteIpAddressRead && remoteIpAddress.IsLocalHost() && remoteIpAddress.m_port == port
                    : remoteSteamId == 0 || remoteSteamId == SteamSockets.GetSteamId(serverSocket.IsGameServer);

                if (isOwnServerNamed)
                    return ConnectLocallyPaired(serverSocket);
            }

            if (isIpAddressing ? !isRemoteIpAddressRead || string.IsNullOrEmpty(remoteAddress) : remoteSteamId == 0)
            {
                Logger<SteamNetworkingClientSocket>.LogError(isIpAddressing
                    ? $"[{remoteAddress}] is not a server address. Set [{nameof(SteamNetworkingTransport)}.{nameof(SteamNetworkingTransport.RemoteAddress)}] to the server's IP address before connecting."
                    : $"No server to connect to. Set [{nameof(SteamNetworkingTransport)}.{nameof(SteamNetworkingTransport.RemoteSteamId)}] to the host's Steam ID before connecting.");

                return ConnectionStateChangeResult.UnspecifiedError;
            }

            _connectionStatusChangedCallback = SteamSockets.CreateStatusCallback(isGameServer: false, OnConnectionStatusChanged);
            SteamNetworkingConfigValue_t[] connectionOptions = SteamSockets.CreateConnectionOptions(steamNetworkingTransport.SendRateKilobytesPerSecond);
            _steamConnection = isIpAddressing ? SteamSockets.ConnectByIPAddress(ref remoteIpAddress, connectionOptions) : SteamSockets.ConnectP2P(remoteSteamId, connectionOptions);

            if (_steamConnection == HSteamNetConnection.Invalid)
            {
                Logger<SteamNetworkingClientSocket>.LogError($"Steam refused to connect to [{(isIpAddressing ? remoteAddress : remoteSteamId.ToString())}]. Make sure Steam is initialised before the client starts.");

                return Fail();
            }

            long deadlineTimestamp = connectTimeoutMilliseconds == 0 ? long.MaxValue : Stopwatch.GetTimestamp() + connectTimeoutMilliseconds * Stopwatch.Frequency / 1000;

            // Read again after every wait: Steam's callback pump, which sets these, runs on this thread between them.
            while (!_isSteamConnected && !_isSteamClosed && Stopwatch.GetTimestamp() < deadlineTimestamp)
                await Task.Delay(ConnectPollIntervalMilliseconds);

            if (!_isSteamConnected)
            {
                Logger<SteamNetworkingClientSocket>.LogError($"Could not connect to [{(isIpAddressing ? remoteAddress : remoteSteamId.ToString())}].");

                return Fail();
            }

            if (!TryDriveLocalStateTo(Invoker.Client, LocalConnectionState.Connected))
                return Fail();

            return ConnectionStateChangeResult.Success;
        }

        /// <summary>
        /// Joins this peer's own server inside the process, without Steam.
        /// </summary>
        /// <param name="serverSocket">This transport's server socket, already listening.</param>
        /// <returns>The result of the state change.</returns>
        /// <remarks>
        /// This socket's own Connection becomes the server's roster entry for it rather than a second one being minted, so every check
        /// asking whether the peer over there is this peer answers by identity. This socket stays that Connection's only owner.
        /// </remarks>
        private ConnectionStateChangeResult ConnectLocallyPaired(SteamNetworkingServerSocket serverSocket)
        {
            if (!TryDriveLocalStateTo(Invoker.Client, LocalConnectionState.Connected))
            {
                TransportManager.ChangeLocalConnectionState(Invoker.Client, LocalConnectionState.Disconnected, Connection);

                return ConnectionStateChangeResult.UnspecifiedError;
            }

            /* Set before the pairing is made: making it runs this peer's whole authentication synchronously, and anything sent from inside
             * that must find this socket already paired. */
            _pairedServerSocket = serverSocket;

            if (serverSocket.EnsurePairLocalClient())
                return ConnectionStateChangeResult.Success;

            _pairedServerSocket = null;

            TransportManager.ChangeLocalConnectionState(Invoker.Client, LocalConnectionState.Disconnected, Connection);

            return ConnectionStateChangeResult.InvalidSocketState;
        }

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> DisconnectAsync()
        {
            /* A paired link has no Steam connection to close and no far end to tell: the peer is this process. The server socket hears the
             * drop and unpairs while the link's id still names its roster entry, and the unpair below is a backstop. */
            if (_pairedServerSocket is not null)
            {
                SteamNetworkingServerSocket pairedServerSocket = _pairedServerSocket;

                if (!TryDriveLocalStateTo(Invoker.Client, LocalConnectionState.Disconnected))
                    return GetResultTask(ConnectionStateChangeResult.UnspecifiedError);

                pairedServerSocket.UnpairLocalClient();

                // Only while still down: a handler raised by the drop may already have started the next link.
                if (State is LocalConnectionState.Disconnected)
                    RecycleConnection();

                return GetResultTask(ConnectionStateChangeResult.Success);
            }

            if (_steamConnection == HSteamNetConnection.Invalid)
                return GetResultTask(ConnectionStateChangeResult.AlreadyInState);

            ReleaseSteam();

            ConnectionStateChangeResult connectionStateChangeResult = TryDriveLocalStateTo(Invoker.Client, LocalConnectionState.Disconnected) ? ConnectionStateChangeResult.Success : ConnectionStateChangeResult.UnspecifiedError;

            /* Recycled at the stop, after the walk, and only while still down, so nothing between two links reads a Connection carrying the
             * last one's state. The stand-in goes with it unless this peer is still hosting. */
            if (State is not LocalConnectionState.Disconnected)
                return GetResultTask(connectionStateChangeResult);

            RecycleConnection();

            ((SteamNetworkingTransport)Transport).ServerSocket.RecycleStandInUnlessHosting();

            return GetResultTask(connectionStateChangeResult);
        }

        /// <inheritdoc/>
        protected override void SendPacket(OutgoingPacket outgoingPacket)
        {
            if (_pairedServerSocket is not null)
            {
                if (State is not LocalConnectionState.Connected)
                {
                    Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);

                    return;
                }

                _pairedServerSocket.ReceiveLocalPacket(outgoingPacket);
                Nucleus.Managers.Transports.TransportManager.PacketOffloaded(outgoingPacket);

                return;
            }

            if (State is not LocalConnectionState.Connected || _steamConnection == HSteamNetConnection.Invalid)
            {
                Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);

                return;
            }

            if (SteamMessages.TrySend(isGameServer: false, _steamConnection, outgoingPacket.Channel, outgoingPacket.Payload))
                Nucleus.Managers.Transports.TransportManager.PacketOffloaded(outgoingPacket);
            else
                Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);
        }

        /// <inheritdoc/>
        /// <remarks>Called by the network loop from Unity's <c>Update</c>, so this is the main thread.</remarks>
        protected override void ReceivePackets(ref List<IncomingPacket> receivedPackets)
        {
            if (_isLinkDropDue)
            {
                _isLinkDropDue = false;

                _ = DisconnectAsync();

                return;
            }

            /* A paired link takes what its server socket queued for it, copying each packet so the Writer the server still holds can go
             * back, the same hand-over an offline transport makes. */
            if (_pairedServerSocket is not null)
            {
                if (State is LocalConnectionState.Connected)
                    _pairedServerSocket.HandOverToPairedClient(receivedPackets);

                return;
            }

            if (_steamConnection == HSteamNetConnection.Invalid || _serverStandIn is null)
                return;

            int messageCount;

            do
            {
                messageCount = SteamSockets.ReceiveMessagesOnConnection(isGameServer: false, _steamConnection, _messages);

                for (int i = 0; i < messageCount; i++)
                {
                    ArraySegment<byte> payload = SteamMessages.TakePayload(_messages[i], out _, out Channel channel);
                    receivedPackets.Add(new(payload, channel, _serverStandIn));
                }
            } while (messageCount == _messages.Length);

            // Steam still hands over what arrived before a close, so the drop waits until those messages have been read.
            if (_isSteamClosed && State is LocalConnectionState.Connected)
                _isLinkDropDue = true;
        }

        /// <summary>
        /// Called by the server socket when it ends the in-process pairing, by a kick, a refusal or the server stopping.
        /// </summary>
        /// <remarks>
        /// Nothing else would drop this link, so the drop is held for the loop as a close read off a socket is. Idempotent, so a client
        /// disconnect and a server stop that both reach it leave this socket as either alone would.
        /// </remarks>
        internal void NotifyLocalPairingEnded()
        {
            _pairedServerSocket = null;

            if (State is LocalConnectionState.Connected)
                DeferLocalClientLinkDrop();
        }

        /// <summary>
        /// Handles Steam's report of the connection changing state. Raised from inside Steam's callback pump on the main thread.
        /// </summary>
        /// <param name="statusChanged">What changed.</param>
        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t statusChanged)
        {
            if (statusChanged.m_hConn != _steamConnection)
                return;

            switch (statusChanged.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    _isSteamConnected = true;

                    return;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    if (!_isSteamConnected)
                        IsLastConnectRefused = statusChanged.m_info.m_eState is ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer;

                    _isSteamClosed = true;

                    return;
            }
        }

        /// <summary>
        /// Unwinds a failed connect, leaving the socket as it was before it was tried.
        /// </summary>
        /// <returns>An unspecified failure, for the caller to return.</returns>
        private ConnectionStateChangeResult Fail()
        {
            ReleaseSteam();

            TransportManager.ChangeLocalConnectionState(Invoker.Client, LocalConnectionState.Disconnected, Connection);

            return ConnectionStateChangeResult.UnspecifiedError;
        }

        /// <summary>
        /// Closes the connection and stops listening for Steam's callbacks.
        /// </summary>
        private void ReleaseSteam()
        {
            _connectionStatusChangedCallback?.Dispose();
            _connectionStatusChangedCallback = null;

            if (_steamConnection != HSteamNetConnection.Invalid)
            {
                SteamSockets.CloseConnection(isGameServer: false, _steamConnection, isLingerEnabled: false);
                _steamConnection = HSteamNetConnection.Invalid;
            }

            _isSteamConnected = false;
            _isSteamClosed = false;
            _isLinkDropDue = false;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// A paired link has already been dropped by the time a socket is returned, its disconnect being what ends the pairing, so it is
        /// only forgotten here.
        /// </remarks>
        public override void OnReturn()
        {
            ReleaseSteam();

            _pairedServerSocket = null;
            _serverStandIn = null;

            base.OnReturn();
        }
    }
}
