// Matches Steamworks.NET's own switch, so this compiles out wherever Steamworks.NET does, not only where it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
using System;
using System.Collections.Generic;
using CodeBoost.Logging;
using CodeBoost.Performance;
using Nucleus.Connections;
using Nucleus.Managers.Core;
using Nucleus.Managers.Messages;
using Nucleus.Managers.NetworkLoop;
using Nucleus.Transports;
using Steamworks;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// An authenticator that admits a client only once Steam has confirmed that its auth session ticket belongs to the Steam ID its link
    /// was authenticated as, that the account owns the game, and that it is not banned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Steam already proves the Steam ID behind every link, which the engine's default authenticator trusts. A ticket adds what that
    /// proof does not: the account owns the app, is not VAC banned, and is not borrowing a game it is barred from. A dedicated server wants
    /// this; a player hosting may.
    /// </para>
    /// <para>
    /// Both sides run it. A client asks Steam for a ticket as soon as its link is up, and sends it once Steam says it is ready. The server
    /// begins an auth session bound to the link's Steam ID, so a forged ticket, or someone else's, is refused, and approves the client
    /// with that Steam ID as its identity once Steam answers. The host's own client, paired in process, is approved at once. Steam's answers
    /// arrive inside its callback pump and are applied on <see cref="NetworkLoopSteps.LateVariableUpdate"/>. An approving answer also names
    /// the account that owns the game license, which differs from the player's under Family Sharing, and the transport keeps it for
    /// <see cref="SteamNetworkingTransport.TryGetOwnerSteamId(Connection, out ulong)"/> before the client is approved.
    /// </para>
    /// <para>
    /// The <see cref="SteamTransport"/> component makes one when its Ticket Authentication is on. A transport set up from code makes one,
    /// calls <see cref="Initialize"/> once the transport is added, and <see cref="Deinitialize"/> before it is removed.
    /// </para>
    /// </remarks>
    public class SteamTicketAuthenticator : Nucleus.Managers.Server.IClientAuthenticator, INetworkLoopStepCallback
    {
        /// <summary>
        /// Decides whether a connecting client may go on to the ticket check.
        /// </summary>
        /// <param name="connection">The client, which may be refused with a reason through <c>ServerManager.DenyClient(connection, notice)</c>.</param>
        /// <param name="steamId">
        /// The Steam ID its link was authenticated as: the player, not the game's owner under Family Sharing, which
        /// <see cref="SteamNetworkingTransport.TryGetOwnerSteamId(Connection, out ulong)"/> reads once Steam has approved the ticket.
        /// </param>
        /// <returns>True to go on, or false to refuse the client.</returns>
        public delegate bool ClientAdmissionFilterHandler(Connection connection, ulong steamId);

        /// <summary>
        /// Asked on the main thread for each connecting client before it waits for a ticket, so a refused client costs no auth session, or
        /// <see langword="null"/> to send every client on to the ticket check. The host's own client skips it.
        /// </summary>
        /// <remarks>A handler that throws refuses the client.</remarks>
        public ClientAdmissionFilterHandler ClientAdmissionFilter;

        /// <summary>
        /// The engine this authenticator is registered with, or <see langword="null"/> while it is not.
        /// </summary>
        private CoreManager _coreManager;
        /// <summary>
        /// The transport whose links this authenticator vouches for.
        /// </summary>
        private SteamNetworkingTransport _steamNetworkingTransport;
        /// <summary>
        /// Steam's verdicts on tickets on the player's interface.
        /// </summary>
        private Callback<ValidateAuthTicketResponse_t> _clientTicketResponseCallback;
        /// <summary>
        /// Steam's verdicts on tickets on the game server interface.
        /// </summary>
        private Callback<ValidateAuthTicketResponse_t> _gameServerTicketResponseCallback;
        /// <summary>
        /// Steam's report that this peer's own ticket is ready to send.
        /// </summary>
        private Callback<GetAuthSessionTicketResponse_t> _ownTicketReadyCallback;
        /// <summary>
        /// Whether the open auth sessions were begun on the game server interface.
        /// </summary>
        private bool _isSessionOnGameServer;
        /// <summary>
        /// Connecting clients whose ticket has not arrived yet, with the Steam ID their link was authenticated as.
        /// </summary>
        private readonly Dictionary<Connection, ulong> _awaitingTicketSteamIdsByConnection = new();
        /// <summary>
        /// Clients with an open auth session, waiting for Steam's verdict or already approved, by Connection.
        /// </summary>
        private readonly Dictionary<Connection, ulong> _sessionSteamIdsByConnection = new();
        /// <summary>
        /// Clients with an open auth session, by Steam ID, which is all a verdict names.
        /// </summary>
        private readonly Dictionary<ulong, Connection> _sessionConnectionsBySteamId = new();
        /// <summary>
        /// Steam's verdicts, held from its callback pump until the next loop step applies them.
        /// </summary>
        private readonly Queue<ValidateAuthTicketResponse_t> _ticketResponses = new();
        /// <summary>
        /// Steam's reports that this peer's own ticket is ready, held from its callback pump until the next loop step sends it.
        /// </summary>
        private readonly Queue<GetAuthSessionTicketResponse_t> _ownTicketReadyResponses = new();
        /// <summary>
        /// The buffer this peer's own ticket is written into.
        /// </summary>
        private readonly byte[] _ticketBuffer = new byte[MaximumTicketBytes];
        /// <summary>
        /// The ticket this peer's client last asked for, or invalid while it has none outstanding.
        /// </summary>
        private HAuthTicket _ownTicket = HAuthTicket.Invalid;
        /// <summary>
        /// How many bytes of <see cref="_ticketBuffer"/> hold <see cref="_ownTicket"/>.
        /// </summary>
        private int _ownTicketByteCount;
        /// <summary>
        /// The most bytes a Steam auth session ticket takes; Valve's own samples size the buffer at this.
        /// </summary>
        private const int MaximumTicketBytes = 1024;

        /// <summary>
        /// Registers this as the server's authenticator, and starts sending this peer's own ticket whenever its client links up.
        /// </summary>
        /// <param name="coreManager">The engine to register with.</param>
        /// <param name="steamNetworkingTransport">The transport whose links are vouched for.</param>
        public void Initialize(CoreManager coreManager, SteamNetworkingTransport steamNetworkingTransport)
        {
            _coreManager = coreManager;
            _steamNetworkingTransport = steamNetworkingTransport;

            coreManager.ServerManager.SetAuthenticator(this);
            coreManager.ServerManager.ClientDisconnecting += ServerManager_OnClientDisconnecting;
            coreManager.TransportManager.ConnectionLocalStateChanged += TransportManager_OnConnectionLocalStateChanged;
            coreManager.MessageManager.RegisterMessageHandler<SteamAuthTicketMessage>(OnTicketReceived);
            coreManager.MessageManager.RegisterMessageHandler<SteamAuthRevokedNotice>(OnRevokedNoticeReceived);
            coreManager.NetworkLoopManager.RegisterNetworkLoopStepCallbacks(this);

            // Registering touches no native code, so it works before the game has initialised Steam.
            _clientTicketResponseCallback = Callback<ValidateAuthTicketResponse_t>.Create(OnTicketResponse);
            _gameServerTicketResponseCallback = Callback<ValidateAuthTicketResponse_t>.CreateGameServer(OnTicketResponse);
            _ownTicketReadyCallback = Callback<GetAuthSessionTicketResponse_t>.Create(OnOwnTicketReady);
        }

        /// <inheritdoc/>
        public void OnClientConnecting(Connection connection)
        {
            if (_steamNetworkingTransport.IsOwnClientConnection(connection))
            {
                _coreManager.ServerManager.ApproveClient(connection, connection.RemoteAddress);

                return;
            }

            if (!ulong.TryParse(connection.RemoteAddress, out ulong steamId) || steamId == SteamNetworkingTransport.UnsetSteamId)
            {
                Logger<SteamTicketAuthenticator>.LogWarning($"A client with no Steam ID [{connection.RemoteAddress}] was refused.");
                _coreManager.ServerManager.DenyClient(connection);

                return;
            }

            if (!IsAdmitted(connection, steamId))
            {
                // A filter that already refused the client with a notice leaves nothing for this call to do.
                _coreManager.ServerManager.DenyClient(connection);

                return;
            }

            _awaitingTicketSteamIdsByConnection[connection] = steamId;
        }

        /// <summary>
        /// Ends every auth session, cancels this peer's ticket, and restores the engine's default authenticator.
        /// </summary>
        public void Deinitialize()
        {
            if (_coreManager is null)
                return;

            List<Connection> sessionConnections = ListPool<Connection>.Rent();
            sessionConnections.AddRange(_sessionSteamIdsByConnection.Keys);

            foreach (Connection sessionConnection in sessionConnections)
                EndSession(sessionConnection);

            ListPool<Connection>.ReturnAndNullifyReference(ref sessionConnections);

            _awaitingTicketSteamIdsByConnection.Clear();
            _ticketResponses.Clear();
            _ownTicketReadyResponses.Clear();
            CancelOwnTicket();

            _coreManager.ServerManager.SetAuthenticator(clientAuthenticator: null);
            _coreManager.ServerManager.ClientDisconnecting -= ServerManager_OnClientDisconnecting;
            _coreManager.TransportManager.ConnectionLocalStateChanged -= TransportManager_OnConnectionLocalStateChanged;
            _coreManager.MessageManager.UnregisterMessageHandler<SteamAuthTicketMessage>(OnTicketReceived);
            _coreManager.MessageManager.UnregisterMessageHandler<SteamAuthRevokedNotice>(OnRevokedNoticeReceived);
            _coreManager.NetworkLoopManager.UnregisterNetworkLoopStepCallbacks(this);

            _clientTicketResponseCallback.Dispose();
            _gameServerTicketResponseCallback.Dispose();
            _ownTicketReadyCallback.Dispose();

            _coreManager = null;
            _steamNetworkingTransport = null;
        }

        /// <inheritdoc/>
        NetworkLoopSteps INetworkLoopStepCallback.GetNetworkLoopSteps() => NetworkLoopSteps.LateVariableUpdate;

        /// <summary>
        /// Applies Steam's held verdicts and sends this peer's own ticket once it is ready.
        /// </summary>
        /// <param name="networkLoopStep">The step being performed.</param>
        /// <param name="stepDelta">Delta information about the step.</param>
        void INetworkLoopStepCallback.OnNetworkLoopStep(NetworkLoopSteps networkLoopStep, StepDelta stepDelta)
        {
            while (_ticketResponses.TryDequeue(out ValidateAuthTicketResponse_t ticketResponse))
                ApplyTicketResponse(ticketResponse);

            while (_ownTicketReadyResponses.TryDequeue(out GetAuthSessionTicketResponse_t ownTicketReadyResponse))
                ApplyOwnTicketReady(ownTicketReadyResponse);
        }

        /// <summary>
        /// Asks <see cref="ClientAdmissionFilter"/> whether a connecting client may go on to the ticket check.
        /// </summary>
        /// <param name="connection">The client.</param>
        /// <param name="steamId">The Steam ID its link was authenticated as.</param>
        /// <returns>True when there is no filter or it let the client on; false when it refused or threw.</returns>
        private bool IsAdmitted(Connection connection, ulong steamId)
        {
            ClientAdmissionFilterHandler clientAdmissionFilter = ClientAdmissionFilter;

            if (clientAdmissionFilter is null)
                return true;

            try
            {
                return clientAdmissionFilter(connection, steamId);
            }
            catch (Exception exception)
            {
                Logger<SteamTicketAuthenticator>.LogError($"The [{nameof(ClientAdmissionFilter)}] threw, so client [{steamId}] was refused: [{exception}].");

                return false;
            }
        }

        /// <summary>
        /// Checks a connecting client's ticket against the Steam ID its link was authenticated as, then returns its pooled array.
        /// </summary>
        /// <param name="messageContext">The context the ticket arrived in.</param>
        /// <param name="steamAuthTicketMessage">The ticket.</param>
        private void OnTicketReceived(in MessageContext messageContext, SteamAuthTicketMessage steamAuthTicketMessage)
        {
            ArraySegment<byte> ticketSegment = steamAuthTicketMessage.Ticket;
            Connection connection = messageContext.SenderConnection;

            // A ticket from a client already past this step, or from one this authenticator never saw connect, is ignored.
            if (connection is not null && _awaitingTicketSteamIdsByConnection.Remove(connection, out ulong steamId))
                BeginSession(connection, steamId, ticketSegment);

            // Steam copies the ticket when the session begins, so the pooled array can go back now.
            if (messageContext.IsDeserialized && ticketSegment.Array is not null)
                TrackedArrayPool<byte>.Return(ticketSegment.Array);
        }

        /// <summary>
        /// Begins an auth session for a connecting client's ticket, bound to the Steam ID its link was authenticated as.
        /// </summary>
        /// <param name="connection">The client.</param>
        /// <param name="steamId">The Steam ID its link was authenticated as.</param>
        /// <param name="ticketSegment">The ticket it sent, valid only for this call.</param>
        private void BeginSession(Connection connection, ulong steamId, ArraySegment<byte> ticketSegment)
        {
            if (ticketSegment.Array is null || ticketSegment.Count == 0)
            {
                Deny(connection, steamId, "sent an empty ticket");

                return;
            }

            // Steam reads a ticket from the start of its array, which a pooled one is.
            byte[] ticket = ticketSegment.Offset == 0 ? ticketSegment.Array : ticketSegment.ToArray();
            bool isGameServer = _steamNetworkingTransport.ServerSocket.IsGameServer;
            CSteamID clientSteamId = new(steamId);

            EBeginAuthSessionResult beginAuthSessionResult = isGameServer ? SteamGameServer.BeginAuthSession(ticket, ticketSegment.Count, clientSteamId) : SteamUser.BeginAuthSession(ticket, ticketSegment.Count, clientSteamId);

            if (beginAuthSessionResult is not EBeginAuthSessionResult.k_EBeginAuthSessionResultOK)
            {
                Deny(connection, steamId, $"sent a ticket Steam refused: [{beginAuthSessionResult}]");

                return;
            }

            _isSessionOnGameServer = isGameServer;
            _sessionSteamIdsByConnection[connection] = steamId;
            _sessionConnectionsBySteamId[steamId] = connection;
        }

        /// <summary>
        /// Logs the reason a server gave for kicking this client after Steam withdrew its approval.
        /// </summary>
        /// <param name="messageContext">The context the notice arrived in.</param>
        /// <param name="steamAuthRevokedNotice">The notice.</param>
        /// <remarks>Only a server sends it; one from a client is ignored, since logging it would let any admitted client fill the server's log.</remarks>
        private void OnRevokedNoticeReceived(in MessageContext messageContext, SteamAuthRevokedNotice steamAuthRevokedNotice)
        {
            if (!messageContext.IsSenderServer)
                return;

            Logger<SteamTicketAuthenticator>.LogWarning($"The server kicked this client because Steam withdrew its approval: [{steamAuthRevokedNotice.Response}].");
        }

        /// <summary>
        /// Approves or refuses a client as Steam judged its ticket.
        /// </summary>
        /// <param name="ticketResponse">Steam's verdict.</param>
        private void ApplyTicketResponse(ValidateAuthTicketResponse_t ticketResponse)
        {
            ulong steamId = ticketResponse.m_SteamID.m_SteamID;

            if (!_sessionConnectionsBySteamId.TryGetValue(steamId, out Connection connection))
                return;

            EAuthSessionResponse authSessionResponse = ticketResponse.m_eAuthSessionResponse;

            if (authSessionResponse is EAuthSessionResponse.k_EAuthSessionResponseOK)
            {
                if (connection.IsAuthenticated)
                    return;

                // Recorded ahead of the approval, so a handler told the client has authenticated can already ban by the license owner.
                _steamNetworkingTransport.RecordOwnerSteamId(connection.Id, ticketResponse.m_OwnerSteamID.m_SteamID);
                _coreManager.ServerManager.ApproveClient(connection, steamId.ToString());

                return;
            }

            // A verdict withdrawn after approval, such as a VAC ban mid-session, kicks the client and tells it why.
            if (connection.IsAuthenticated)
            {
                Logger<SteamTicketAuthenticator>.LogWarning($"Steam withdrew its approval of client [{steamId}]: [{authSessionResponse}], so it was kicked.");
                connection.Kick(new SteamAuthRevokedNotice { Response = authSessionResponse.ToString() });

                return;
            }

            EndSession(connection);
            Deny(connection, steamId, $"was refused by Steam: [{authSessionResponse}]");
        }

        /// <summary>
        /// Asks Steam for this peer's ticket when its client links up to another peer's server, and cancels it when the link drops.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void TransportManager_OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (connectionStateChange.IsRemoteStateChange || connectionStateChange.Invoker is not Invoker.Client)
                return;

            LocalConnectionState localConnectionState = connectionStateChange.Connection.LocalState;

            if (localConnectionState is LocalConnectionState.Disconnected)
                CancelOwnTicket();
            else if (localConnectionState is LocalConnectionState.Connected && !_coreManager.TransportManager.IsServerStarted)
                RequestOwnTicket();
        }

        /// <summary>
        /// Ends a departing client's auth session, which Steam would otherwise keep open.
        /// </summary>
        /// <param name="connection">The departing client.</param>
        private void ServerManager_OnClientDisconnecting(Connection connection)
        {
            _awaitingTicketSteamIdsByConnection.Remove(connection);
            EndSession(connection);
        }

        /// <summary>
        /// Writes this peer's ticket, bound to the server it is linked to; <see cref="ApplyOwnTicketReady"/> sends it once Steam says it is
        /// ready.
        /// </summary>
        private void RequestOwnTicket()
        {
            CancelOwnTicket();

            /* The ticket is bound to the server's identity, so a server handed it cannot replay it to another. Without a Steam link to read
             * that identity from, it stays invalid and the ticket is unbound. */
            SteamNetworkingIdentity serverIdentity = default;
            _steamNetworkingTransport.ClientSocket?.TryGetServerIdentity(out serverIdentity);

            _ownTicket = SteamUser.GetAuthSessionTicket(_ticketBuffer, MaximumTicketBytes, out uint ticketSize, ref serverIdentity);

            if (_ownTicket == HAuthTicket.Invalid)
            {
                Logger<SteamTicketAuthenticator>.LogError("Steam did not issue an auth session ticket, so the server will refuse this client.");

                return;
            }

            _ownTicketByteCount = (int)ticketSize;
        }

        /// <summary>
        /// Sends this peer's ticket to its server once Steam says it is ready, or logs why Steam could not ready it.
        /// </summary>
        /// <param name="ownTicketReadyResponse">Steam's report.</param>
        private void ApplyOwnTicketReady(GetAuthSessionTicketResponse_t ownTicketReadyResponse)
        {
            // A report for a ticket already cancelled, or replaced by a newer connect, has nothing left to send.
            if (ownTicketReadyResponse.m_hAuthTicket != _ownTicket || _ownTicket == HAuthTicket.Invalid)
                return;

            if (ownTicketReadyResponse.m_eResult is not EResult.k_EResultOK)
            {
                Logger<SteamTicketAuthenticator>.LogError($"Steam could not ready an auth session ticket: [{ownTicketReadyResponse.m_eResult}], so the server will refuse this client.");

                return;
            }

            if (_coreManager.TransportManager.TryGetServerConnection(out Connection serverConnection))
                serverConnection.SendMessage(Channel.Reliable, new SteamAuthTicketMessage { Ticket = new(_ticketBuffer, 0, _ownTicketByteCount) });
        }

        /// <summary>
        /// Cancels this peer's outstanding ticket, so it can no longer be used.
        /// </summary>
        private void CancelOwnTicket()
        {
            if (_ownTicket == HAuthTicket.Invalid)
                return;

            if (!SteamSockets.IsShutDown(isGameServer: false))
                SteamUser.CancelAuthTicket(_ownTicket);

            _ownTicket = HAuthTicket.Invalid;
        }

        /// <summary>
        /// Ends a client's auth session, if it has one.
        /// </summary>
        /// <param name="connection">The client.</param>
        private void EndSession(Connection connection)
        {
            if (!_sessionSteamIdsByConnection.Remove(connection, out ulong steamId))
                return;

            _sessionConnectionsBySteamId.Remove(steamId);

            if (SteamSockets.IsShutDown(_isSessionOnGameServer))
                return;

            if (_isSessionOnGameServer)
                SteamGameServer.EndAuthSession(new(steamId));
            else
                SteamUser.EndAuthSession(new(steamId));
        }

        /// <summary>
        /// Refuses a connecting client and logs why.
        /// </summary>
        /// <param name="connection">The client.</param>
        /// <param name="steamId">Its Steam ID.</param>
        /// <param name="reason">Why, as the end of a sentence.</param>
        private void Deny(Connection connection, ulong steamId, string reason)
        {
            Logger<SteamTicketAuthenticator>.LogWarning($"Client [{steamId}] {reason}, so it was refused.");
            _coreManager.ServerManager.DenyClient(connection);
        }

        /// <summary>
        /// Holds one of Steam's verdicts for the next loop step. Raised from inside Steam's callback pump.
        /// </summary>
        /// <param name="ticketResponse">Steam's verdict.</param>
        private void OnTicketResponse(ValidateAuthTicketResponse_t ticketResponse) => _ticketResponses.Enqueue(ticketResponse);

        /// <summary>
        /// Holds Steam's report that this peer's own ticket is ready for the next loop step. Raised from inside Steam's callback pump.
        /// </summary>
        /// <param name="ownTicketReadyResponse">Steam's report.</param>
        private void OnOwnTicketReady(GetAuthSessionTicketResponse_t ownTicketReadyResponse) => _ownTicketReadyResponses.Enqueue(ownTicketReadyResponse);
    }
}
#endif
