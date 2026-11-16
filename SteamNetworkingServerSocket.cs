using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CodeBoost.Logging;
using CodeBoost.Performance;
using Nucleus.Connections;
using Nucleus.Packets;
using Nucleus.Transports;
using Nucleus.Transports.Sockets;
using Steamworks;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// The server side of a <see cref="SteamNetworkingTransport"/>. Listens on this peer's Steam identity and treats every connection Steam
    /// hands it as an ordinary Nucleus <see cref="Connection"/>.
    /// </summary>
    public class SteamNetworkingServerSocket : CommonSocket
    {
        /// <inheritdoc/>
        public override bool IsServer { get; protected set; } = true;

        /// <inheritdoc/>
        public override bool IsEmulated { get; protected set; } = false;

        /// <summary>
        /// Whether this socket's handles belong to the game server interface, fixed when the server starts.
        /// </summary>
        internal bool IsGameServer { get; private set; }

        /// <summary>
        /// Assigns the per-socket ids remote clients are known by.
        /// </summary>
        /// <remarks>Swept once a frame by the TransportManager through <see cref="SweepIdPools"/>, so it runs no timer of its own.</remarks>
        private readonly DelayedUIntPool _idBySocketPool = new(isOwnTimerEnabled: false);

        /// <summary>
        /// Maps each Steam connection onto the Connection the engine knows it as.
        /// </summary>
        private readonly Dictionary<HSteamNetConnection, Connection> _connectionsBySteamConnection = new();

        /// <summary>
        /// The reverse, so an outbound packet can be addressed to the right Steam connection.
        /// </summary>
        /// <remarks>
        /// Keyed by Connection rather than by <see cref="Connection.Id"/>, because a peer is registered before the engine numbers it. Both
        /// maps are only ever changed together.
        /// </remarks>
        private readonly Dictionary<Connection, HSteamNetConnection> _steamConnectionsByConnection = new();

        /// <summary>
        /// Steam connections that have sent a message while not registered, each already reported once.
        /// </summary>
        private readonly HashSet<HSteamNetConnection> _unknownSteamConnections = new();

        /// <summary>
        /// Clients that joined or left since the last receive pass, applied there so the engine's state changes never run inside a Steam
        /// callback.
        /// </summary>
        private readonly Queue<PendingClientEvent> _pendingClientEvents = new();

        /// <summary>
        /// Native message pointers filled by each receive call.
        /// </summary>
        private readonly IntPtr[] _messages = new IntPtr[SteamMessages.MaximumMessagesPerReceive];

        /// <summary>
        /// The socket peers connect to, or invalid while the server is stopped.
        /// </summary>
        private HSteamListenSocket _listenSocket = HSteamListenSocket.Invalid;

        /// <summary>
        /// Gathers every accepted connection so all of them are read with one call.
        /// </summary>
        private HSteamNetPollGroup _pollGroup = HSteamNetPollGroup.Invalid;

        /// <summary>
        /// Steam's report of connections opening and closing, held for as long as the server runs.
        /// </summary>
        private Callback<SteamNetConnectionStatusChangedCallback_t> _connectionStatusChangedCallback;

        /// <summary>
        /// This socket's entry for its own transport's client half while that half is paired in process, or null.
        /// </summary>
        /// <remarks>
        /// The client socket's own Connection, borrowed rather than minted, so every check asking whether a peer is this peer answers by
        /// identity. The client socket alone returns it to the pool; this socket only routes packets to it and releases it from the roster.
        /// </remarks>
        private Connection _pairedClientConnection;

        /// <summary>
        /// The socket id rented for <see cref="_pairedClientConnection"/>, held so it goes back to the pool when the pairing ends.
        /// </summary>
        private uint _pairedClientIdBySocket = Connection.UnsetId;

        /// <summary>
        /// Creates a new instance.
        /// </summary>
        public SteamNetworkingServerSocket() { }

        /// <inheritdoc/>
        public override void Initialize(Transport transport)
        {
            base.Initialize(transport);
            InitializeConnection();

            LocalConnectionStateChanged += TransportManager_OnConnectionLocalStateChanged;

            _idBySocketPool.OnRent();
            _idBySocketPool.Initialize(returnMinutes: 5, firstRentedValue: Connection.UnsetId + 1);
        }

        /// <inheritdoc/>
        protected override void SweepIdPools() => _idBySocketPool.Sweep();

        /// <summary>
        /// Opens a listen socket on this peer's Steam identity.
        /// </summary>
        /// <returns>The result of the state change.</returns>
        protected override Task<ConnectionStateChangeResult> ConnectAsync()
        {
            if (_listenSocket != HSteamListenSocket.Invalid)
                return GetResultTask(ConnectionStateChangeResult.AlreadyInState);

            SteamNetworkingTransport steamNetworkingTransport = (SteamNetworkingTransport)Transport;
            IsGameServer = steamNetworkingTransport.IsGameServer;

            if (steamNetworkingTransport.Addressing is SteamAddressing.IpAddress)
            {
                Configuration configuration = steamNetworkingTransport.Configuration;

                if (!SteamSockets.TryParseIpAddress(configuration.BindAddress, configuration.Port, out SteamNetworkingIPAddr bindAddress))
                {
                    Logger<SteamNetworkingServerSocket>.LogError($"Bind address [{configuration.BindAddress}] is not an IP address. Leave it empty to listen on every interface.");

                    return GetResultTask(ConnectionStateChangeResult.UnspecifiedError);
                }

                _listenSocket = SteamSockets.CreateListenSocketIP(IsGameServer, ref bindAddress, SteamSockets.CreateConnectionOptions(steamNetworkingTransport.SendRateKilobytesPerSecond));
            }
            else
            {
                _listenSocket = SteamSockets.CreateListenSocketP2P(IsGameServer, SteamSockets.CreateConnectionOptions(steamNetworkingTransport.SendRateKilobytesPerSecond));
            }

            if (_listenSocket == HSteamListenSocket.Invalid)
            {
                Logger<SteamNetworkingServerSocket>.LogError("Steam refused to open a listen socket. Make sure Steam is initialised before the server starts.");

                return GetResultTask(ConnectionStateChangeResult.UnspecifiedError);
            }

            _pollGroup = SteamSockets.CreatePollGroup(IsGameServer);
            _connectionStatusChangedCallback = SteamSockets.CreateStatusCallback(IsGameServer, OnConnectionStatusChanged);

            if (!TryDriveLocalStateTo(Invoker.Server, LocalConnectionState.Connected))
            {
                ReleaseSteam();
                TransportManager.ChangeLocalConnectionState(Invoker.Server, LocalConnectionState.Disconnected, Connection);

                return GetResultTask(ConnectionStateChangeResult.UnspecifiedError);
            }

            return GetResultTask(ConnectionStateChangeResult.Success);
        }

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> DisconnectAsync()
        {
            if (_listenSocket == HSteamListenSocket.Invalid)
                return GetResultTask(ConnectionStateChangeResult.AlreadyInState);

            /* The in-process pairing holds no Steam connection, so it appears in neither map the walk below reads and is dropped on its own.
             * Arrivals and departures still queued are applied next, so every client the engine was or is about to be told of leaves
             * through the walk rather than being admitted later onto a server that has stopped. */
            UnpairLocalClient();
            ApplyPendingClientEvents();
            DisconnectRemoteClients();
            ReleaseSteam();

            return GetResultTask(TryDriveLocalStateTo(Invoker.Server, LocalConnectionState.Disconnected) ? ConnectionStateChangeResult.Success : ConnectionStateChangeResult.UnspecifiedError);
        }

        /// <inheritdoc/>
        protected override Task<ConnectionStateChangeResult> DisconnectRemoteClientAsync(Connection connection) => DisconnectRemoteClientCoreAsync(connection, isLingerEnabled: false);

        /// <inheritdoc/>
        /// <remarks>Steam keeps sending the reliable data already queued, a kick's reason among it, before it closes the connection.</remarks>
        protected override Task<ConnectionStateChangeResult> DisconnectRemoteClientAfterDeliveryAsync(Connection connection) => DisconnectRemoteClientCoreAsync(connection, isLingerEnabled: true);

        /// <inheritdoc/>
        protected override void SendPacket(OutgoingPacket outgoingPacket)
        {
            /* Addressed to this transport's own client half, which holds no Steam connection while paired in process. The packet is queued
             * for the client socket's next receive, and its Writer travels with it and is returned there, so it is not offloaded here. */
            if (_pairedClientConnection is not null && ReferenceEquals(outgoingPacket.Receiver, _pairedClientConnection))
            {
                OutgoingPackets.Enqueue(outgoingPacket);

                return;
            }

            if (State is not LocalConnectionState.Connected || outgoingPacket.Receiver is null || !_steamConnectionsByConnection.TryGetValue(outgoingPacket.Receiver, out HSteamNetConnection steamConnection) || SteamSockets.IsShutDown(IsGameServer))
            {
                Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);

                return;
            }

            EResult sendResult = SteamMessages.Send(IsGameServer, steamConnection, outgoingPacket.Channel, outgoingPacket.Payload);

            if (sendResult is EResult.k_EResultOK)
            {
                Nucleus.Managers.Transports.TransportManager.PacketOffloaded(outgoingPacket);

                return;
            }

            Nucleus.Managers.Transports.TransportManager.PacketFailed(outgoingPacket);

            ((SteamNetworkingTransport)Transport).NotifySendFailed(Invoker.Server, outgoingPacket.Receiver, outgoingPacket.Channel, sendResult);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Called by the network loop from Unity's <c>Update</c>, so this is the main thread. Arrivals are applied before messages are read,
        /// so a client's first packets always find it registered.
        /// </remarks>
        protected override void ReceivePackets(ref List<IncomingPacket> receivedPackets)
        {
            ApplyPendingClientEvents();

            /* A Steam shut down while the server ran took every link with it, and Steamworks.NET throws on any further call, so the server
             * stops, forgetting its handles, rather than reading from a dead interface. */
            if (_pollGroup != HSteamNetPollGroup.Invalid && SteamSockets.IsShutDown(IsGameServer))
            {
                Logger<SteamNetworkingServerSocket>.LogWarning("Steam was shut down while the server was running, so the server stopped. Shut Steam down only after the transport stops.");

                _ = DisconnectAsync();

                return;
            }

            if (_pollGroup != HSteamNetPollGroup.Invalid)
            {
                int messageCount;

                do
                {
                    messageCount = SteamSockets.ReceiveMessagesOnPollGroup(IsGameServer, _pollGroup, _messages);

                    for (int i = 0; i < messageCount; i++)
                    {
                        ArraySegment<byte> payload = SteamMessages.TakePayload(_messages[i], out HSteamNetConnection steamConnection, out Channel channel);

                        if (_connectionsBySteamConnection.TryGetValue(steamConnection, out Connection senderConnection))
                        {
                            receivedPackets.Add(new(payload, channel, senderConnection));

                            continue;
                        }

                        TrackedArrayPool<byte>.Return(payload.Array);

                        /* Every connection is registered before it joins the poll group, so a message from one this server does not know is
                         * its own bookkeeping having gone wrong, never a peer misbehaving. Said once per Steam connection, as the rest of its
                         * messages would say it again. */
                        if (_unknownSteamConnections.Add(steamConnection))
                            Logger<SteamNetworkingServerSocket>.LogError($"Steam connection [{steamConnection.m_HSteamNetConnection}] sent a message, but no client is registered under it, so the message was dropped. Every message from it will be dropped the same way.");
                    }
                } while (messageCount == _messages.Length);
            }

            /* What this transport's own client half handed over in process, treated from here on exactly as a packet off Steam. Each payload
             * array belongs to the Reader the engine initializes with it. */
            while (IncomingPackets.TryDequeue(out IncomingPacket locallyPairedIncomingPacket))
                receivedPackets.Add(locallyPairedIncomingPacket);
        }

        /// <summary>
        /// Adopts this transport's own client half as a connected peer inside the process, without a Steam connection or a Connection of
        /// this socket's own.
        /// </summary>
        /// <returns>True when the pairing was made.</returns>
        /// <remarks>
        /// The roster entry is the client socket's own Connection, so the pairing is provable by identity. The roster only borrows it:
        /// leaving the roster releases the server's role and nothing else, and the client socket pools the instance once its link drops.
        /// </remarks>
        internal bool EnsurePairLocalClient()
        {
            if (_pairedClientConnection is not null || State is not LocalConnectionState.Connected || Transport is not SteamNetworkingTransport { ClientSocket: { Connection: { } pairedClientConnection } })
                return false;

            if (IsConnectionCapReached() || !_idBySocketPool.EnsureRent(out uint idBySocket))
            {
                Logger<SteamNetworkingServerSocket>.LogWarning("This transport's own client cannot be paired with its server: the server is full or has no id to give it.");

                return false;
            }

            // The peer's Steam ID is its address, as it is for a client that arrived over Steam.
            pairedClientConnection.SetRemoteAddress(SteamSockets.GetSteamId(IsGameServer).ToString());

            /* Recorded before the peer is announced. Announcing it runs the authentication owed a new peer synchronously, and that is where
             * the engine asks this transport whether the peer is its own client half, and where the reply admitting it is sent. */
            _pairedClientConnection = pairedClientConnection;
            _pairedClientIdBySocket = idBySocket;

            ConnectionStateChangeResult connectionStateChangeResult = TransportManager.SetRemoteConnectionStateAsConnected(idBySocket, pairedClientConnection);

            if (connectionStateChangeResult is ConnectionStateChangeResult.Success)
                return true;

            Logger<SteamNetworkingServerSocket>.LogError($"This transport's own client could not be registered: [{connectionStateChangeResult}].");

            _pairedClientConnection = null;
            _pairedClientIdBySocket = Connection.UnsetId;

            // Borrowed from the client socket, so it is released rather than pooled.
            _idBySocketPool.ReturnUnsafe(idBySocket);
            pairedClientConnection.ReleaseServerRole();

            return false;
        }

        /// <summary>
        /// Drops the in-process pairing, driving the departure an ordinary close would drive.
        /// </summary>
        /// <remarks>Idempotent, so a server stopping and a client disconnecting may both reach it.</remarks>
        internal void UnpairLocalClient()
        {
            if (_pairedClientConnection is null)
                return;

            Connection pairedClientConnection = _pairedClientConnection;

            // Cleared first, so a departure handler that sends reads this socket as no longer paired.
            _pairedClientConnection = null;

            if (_pairedClientIdBySocket != Connection.UnsetId)
            {
                _idBySocketPool.ReturnUnsafe(_pairedClientIdBySocket);
                _pairedClientIdBySocket = Connection.UnsetId;
            }

            /* The client socket is told before the departure rather than after, so a handler the departure raises cannot hand this socket
             * another packet for a pairing on its way out. */
            if (Transport is SteamNetworkingTransport steamNetworkingTransport)
                steamNetworkingTransport.ClientSocket?.NotifyLocalPairingEnded();

            SetRemoteConnectionStateAsDisconnected(pairedClientConnection);
        }

        /// <summary>
        /// Takes a packet this transport's own client half sent while paired in process.
        /// </summary>
        /// <param name="outgoingPacket">The packet the client half sent.</param>
        /// <remarks>
        /// Copied into a pooled array, because the sender returns the packet as soon as this comes back. The sender named is this socket's
        /// roster entry for the peer, which is what the receive pass attributes a server-inbound packet by.
        /// </remarks>
        internal void ReceiveLocalPacket(OutgoingPacket outgoingPacket)
        {
            if (State is not LocalConnectionState.Connected || _pairedClientConnection is null)
                return;

            ArraySegment<byte> outgoingPayload = outgoingPacket.Payload;

            if (outgoingPayload.Array is null)
                return;

            byte[] incomingPacketArray = TrackedArrayPool<byte>.Rent(outgoingPayload.Count);
            Array.Copy(outgoingPayload.Array, outgoingPayload.Offset, incomingPacketArray, 0, outgoingPayload.Count);

            IncomingPackets.Enqueue(new(new(incomingPacketArray, 0, outgoingPayload.Count), outgoingPacket.Channel, _pairedClientConnection));
        }

        /// <summary>
        /// Hands what this socket sent its paired client to that client's receive pass.
        /// </summary>
        /// <param name="receivedPackets">The client's received packets, which each handed-over packet is added to.</param>
        /// <remarks>Each packet is copied, the same hand-over an offline transport makes, so the Writer this socket still holds can go back.</remarks>
        internal void HandOverToPairedClient(List<IncomingPacket> receivedPackets)
        {
            while (OutgoingPackets.TryDequeue(out OutgoingPacket outgoingPacket))
            {
                if (TryCopyIntoIncomingPacket(outgoingPacket, out IncomingPacket incomingPacket))
                    receivedPackets.Add(incomingPacket);

                outgoingPacket.Return();
            }
        }

        /// <summary>
        /// Recycles this socket's Connection unless its server is running, for the client half starting or ending a link to another peer.
        /// </summary>
        /// <remarks>
        /// A socket that is not serving lends its Connection to the client half as the stand-in for the remote server, so each such link
        /// takes a fresh one. A running server's Connection describes this peer's own server and is kept.
        /// </remarks>
        internal void RecycleStandInUnlessHosting()
        {
            if (State is not LocalConnectionState.Connected)
                RecycleConnection();
        }

        /// <summary>
        /// Queues a client Steam has connected, after the cap and an id allow it.
        /// </summary>
        /// <param name="steamConnection">The connection, already connected.</param>
        /// <param name="remoteSteamId">The Steam ID of the peer on the other end.</param>
        /// <returns>True when the client was queued; otherwise the connection has been closed.</returns>
        private bool TryAdmit(HSteamNetConnection steamConnection, ulong remoteSteamId)
        {
            if (IsConnectionCapReached() || !_idBySocketPool.EnsureRent(out uint idBySocket))
            {
                Logger<SteamNetworkingServerSocket>.LogWarning($"Steam client [{remoteSteamId}] was refused: the server is full or has no id to give it.");

                SteamSockets.CloseConnection(IsGameServer, steamConnection, isLingerEnabled: false);

                return false;
            }

            // The peer's Steam ID is its address, so an authenticator can tell who it is.
            Connection connection = ResettableObjectPool<Connection>.Rent();
            connection.Initialize(idBySocket, isEmulated: false, Transport, remoteAddress: remoteSteamId.ToString());

            _connectionsBySteamConnection[steamConnection] = connection;
            _steamConnectionsByConnection[connection] = steamConnection;

            // Moved into the poll group only now, so nothing it sends is read before the engine knows it.
            SteamSockets.SetConnectionPollGroup(IsGameServer, steamConnection, _pollGroup);

            _pendingClientEvents.Enqueue(new(connection, idBySocket, hasJoined: true));

            return true;
        }

        /// <summary>
        /// Returns whether the server already holds as many clients as it allows.
        /// </summary>
        private bool IsConnectionCapReached()
        {
            uint maximumConnections = Transport.ServerConfiguration.MaximumConnections;

            if (maximumConnections is ServerConfiguration.UnsetMaximumConnections)
                return false;

            int heldClientCount = _connectionsBySteamConnection.Count + (_pairedClientConnection is null ? 0 : 1);

            return heldClientCount >= maximumConnections;
        }

        /// <summary>
        /// Closes one client's connection and queues its departure.
        /// </summary>
        /// <param name="connection">The client to drop.</param>
        /// <param name="isLingerEnabled">True to deliver the reliable data already queued before closing.</param>
        /// <returns>The result of the request.</returns>
        private Task<ConnectionStateChangeResult> DisconnectRemoteClientCoreAsync(Connection connection, bool isLingerEnabled)
        {
            if (State is not LocalConnectionState.Connected)
                return GetResultTask(ConnectionStateChangeResult.InvalidSocketState);

            // This transport's own client half has no Steam connection to close while paired; dropping the pairing is the whole of it.
            if (_pairedClientConnection is not null && ReferenceEquals(connection, _pairedClientConnection))
            {
                UnpairLocalClient();

                return GetResultTask(ConnectionStateChangeResult.Success);
            }

            if (connection is null || !_steamConnectionsByConnection.TryGetValue(connection, out HSteamNetConnection steamConnection))
                return GetResultTask(ConnectionStateChangeResult.InvalidConnection);

            // Steam posts no callback for a connection this side closes, so the departure is queued here.
            ForgetAndQueueDeparture(steamConnection, connection);

            if (!SteamSockets.IsShutDown(IsGameServer))
                SteamSockets.CloseConnection(IsGameServer, steamConnection, isLingerEnabled);

            return GetResultTask(ConnectionStateChangeResult.Success);
        }

        /// <summary>
        /// Handles Steam's report of a connection changing state. Raised from inside Steam's callback pump on the main thread.
        /// </summary>
        /// <param name="statusChanged">What changed.</param>
        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t statusChanged)
        {
            HSteamNetConnection steamConnection = statusChanged.m_hConn;

            switch (statusChanged.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                    // A connection this peer's own client is making elsewhere is reported here too; only ones to this socket are taken.
                    if (statusChanged.m_info.m_hListenSocket != _listenSocket)
                        return;

                    if (IsConnectionCapReached())
                    {
                        SteamSockets.CloseConnection(IsGameServer, steamConnection, isLingerEnabled: false);

                        return;
                    }

                    EResult acceptResult = SteamSockets.AcceptConnection(IsGameServer, steamConnection);

                    if (acceptResult is not EResult.k_EResultOK)
                    {
                        Logger<SteamNetworkingServerSocket>.LogError($"Steam could not accept a connection: [{acceptResult}].");

                        SteamSockets.CloseConnection(IsGameServer, steamConnection, isLingerEnabled: false);
                    }

                    return;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (statusChanged.m_info.m_hListenSocket != _listenSocket || _connectionsBySteamConnection.ContainsKey(steamConnection))
                        return;

                    TryAdmit(steamConnection, statusChanged.m_info.m_identityRemote.GetSteamID64());

                    return;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    // Steam keeps a closed connection's handle until it is closed here too.
                    if (_connectionsBySteamConnection.TryGetValue(steamConnection, out Connection connection))
                        ForgetAndQueueDeparture(steamConnection, connection);

                    SteamSockets.CloseConnection(IsGameServer, steamConnection, isLingerEnabled: false);

                    return;
            }
        }

        /// <summary>
        /// Releases the roster entry for this transport's own client half when that half's link drops first.
        /// </summary>
        /// <param name="connectionStateChange">The change being reported.</param>
        /// <param name="localConnectionState">The state being reported.</param>
        /// <param name="isServerStarted">Whether this peer was running a server when the change was reported.</param>
        /// <remarks>
        /// The roster finds the entry by the id the client link clears as it finishes dropping. This is raised before that clear, so the
        /// entry is released here while the id still names it, the client's link going down before the server lets it go, as for a remote
        /// client.
        /// </remarks>
        private void TransportManager_OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange, LocalConnectionState localConnectionState, bool isServerStarted)
        {
            if (connectionStateChange.Invoker is not Invoker.Client || localConnectionState is not LocalConnectionState.Disconnected)
                return;

            Connection connection = connectionStateChange.Connection;

            if (connection is null || connection.Transport != Transport || connection.RemoteState is not RemoteConnectionState.Connected)
                return;

            if (ReferenceEquals(connection, _pairedClientConnection))
                UnpairLocalClient();
        }

        /// <summary>
        /// Removes a client from both maps and queues its departure for the next receive pass.
        /// </summary>
        private void ForgetAndQueueDeparture(HSteamNetConnection steamConnection, Connection connection)
        {
            _connectionsBySteamConnection.Remove(steamConnection);
            _steamConnectionsByConnection.Remove(connection);

            _pendingClientEvents.Enqueue(new(connection, connection.IdBySocket, hasJoined: false));
        }

        /// <summary>
        /// Tells the engine of every client that joined or left since the last pass.
        /// </summary>
        /// <remarks>A handler raised here can stop the server, which drains this same queue, so each event is taken off before it is applied.</remarks>
        private void ApplyPendingClientEvents()
        {
            while (_pendingClientEvents.TryDequeue(out PendingClientEvent pendingClientEvent))
            {
                if (pendingClientEvent.HasJoined)
                    NotifyConnected(pendingClientEvent.Connection, pendingClientEvent.IdBySocket);
                else
                    NotifyDisconnected(pendingClientEvent.Connection, pendingClientEvent.IdBySocket);
            }
        }

        /// <summary>
        /// Registers a joined client with the engine, closing it again if the engine refuses it.
        /// </summary>
        private void NotifyConnected(Connection connection, uint idBySocket)
        {
            ConnectionStateChangeResult connectionStateChangeResult = TransportManager.SetRemoteConnectionStateAsConnected(idBySocket, connection);

            if (connectionStateChangeResult is ConnectionStateChangeResult.Success)
                return;

            Logger<SteamNetworkingServerSocket>.LogError($"A Steam client could not be registered: [{connectionStateChangeResult}].");

            if (_steamConnectionsByConnection.TryGetValue(connection, out HSteamNetConnection steamConnection))
            {
                _connectionsBySteamConnection.Remove(steamConnection);
                _steamConnectionsByConnection.Remove(connection);

                if (!SteamSockets.IsShutDown(IsGameServer))
                    SteamSockets.CloseConnection(IsGameServer, steamConnection, isLingerEnabled: false);
            }

            _idBySocketPool.ReturnUnsafe(idBySocket);
            ResettableObjectPool<Connection>.Return(connection);
        }

        /// <summary>
        /// Tells the engine a client has left and returns its id once the engine has let it go.
        /// </summary>
        /// <remarks>
        /// The engine refuses a client that was never registered or has already left, so no id is returned twice. A departure handler can
        /// remove this transport, which returns this socket, so the id is returned only while this socket still has its manager.
        /// </remarks>
        private void NotifyDisconnected(Connection connection, uint idBySocket)
        {
            if (SetRemoteConnectionStateAsDisconnected(connection) is ConnectionStateChangeResult.Success && TransportManager is not null)
                _idBySocketPool.ReturnUnsafe(idBySocket);
        }

        /// <summary>
        /// Closes every client connection and tells the engine each one has left.
        /// </summary>
        private void DisconnectRemoteClients()
        {
            if (_connectionsBySteamConnection.Count == 0)
                return;

            List<Connection> departingConnections = ListPool<Connection>.Rent();
            bool isShutDown = SteamSockets.IsShutDown(IsGameServer);

            foreach (KeyValuePair<HSteamNetConnection, Connection> connectionKeyValuePair in _connectionsBySteamConnection)
            {
                if (!isShutDown)
                    SteamSockets.CloseConnection(IsGameServer, connectionKeyValuePair.Key, isLingerEnabled: false);

                departingConnections.Add(connectionKeyValuePair.Value);
            }

            _connectionsBySteamConnection.Clear();
            _steamConnectionsByConnection.Clear();

            foreach (Connection departingConnection in departingConnections)
                NotifyDisconnected(departingConnection, departingConnection.IdBySocket);

            ListPool<Connection>.ReturnAndNullifyReference(ref departingConnections);
        }

        /// <summary>
        /// Closes the listen socket and poll group and stops listening for Steam's callbacks.
        /// </summary>
        /// <remarks>A Steam already shut down has closed both itself, so their handles are only forgotten then.</remarks>
        private void ReleaseSteam()
        {
            _connectionStatusChangedCallback?.Dispose();
            _connectionStatusChangedCallback = null;

            if (!SteamSockets.IsShutDown(IsGameServer))
            {
                if (_pollGroup != HSteamNetPollGroup.Invalid)
                    SteamSockets.DestroyPollGroup(IsGameServer, _pollGroup);

                if (_listenSocket != HSteamListenSocket.Invalid)
                    SteamSockets.CloseListenSocket(IsGameServer, _listenSocket);
            }

            _pollGroup = HSteamNetPollGroup.Invalid;
            _listenSocket = HSteamListenSocket.Invalid;
        }

        /// <inheritdoc/>
        public override void OnReturn()
        {
            if (TransportManager is not null)
                LocalConnectionStateChanged -= TransportManager_OnConnectionLocalStateChanged;

            _idBySocketPool.OnReturn();

            /* Held rather than owned: a client's Connection goes back to its pool through the TransportManager, so these are cleared and
             * not drained. The host's own client is held on the same terms and more so, its Connection belonging to the client socket. */
            _pairedClientConnection = null;
            _pairedClientIdBySocket = Connection.UnsetId;

            _connectionsBySteamConnection.Clear();
            _steamConnectionsByConnection.Clear();
            _unknownSteamConnections.Clear();
            _pendingClientEvents.Clear();

            base.OnReturn();
        }

        /// <summary>
        /// A client joining or leaving, applied on the next receive pass.
        /// </summary>
        private readonly struct PendingClientEvent
        {
            /// <summary>
            /// The client's Connection.
            /// </summary>
            public readonly Connection Connection;

            /// <summary>
            /// The id it was rented.
            /// </summary>
            public readonly uint IdBySocket;

            /// <summary>
            /// Whether it joined, as against left.
            /// </summary>
            public readonly bool HasJoined;

            /// <summary>
            /// Creates a new instance.
            /// </summary>
            /// <param name="connection">The client's Connection.</param>
            /// <param name="idBySocket">The id it was rented.</param>
            /// <param name="hasJoined">Whether it joined.</param>
            public PendingClientEvent(Connection connection, uint idBySocket, bool hasJoined)
            {
                Connection = connection;
                IdBySocket = idBySocket;
                HasJoined = hasJoined;
            }
        }
    }
}
