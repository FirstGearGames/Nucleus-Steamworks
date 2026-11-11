using System.Collections.Generic;
using Nucleus.Connections;
using Nucleus.Integrations.Unity.Managers.Core;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Messages;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// Checks a Steam link both ways with nothing of a game in the way: a client sends a numbered probe to the server every interval on
    /// both channels, the server echoes each one back, and both sides log what they sent, what arrived, and every send Steam refused.
    /// </summary>
    /// <remarks>
    /// Put it on the GameObject holding the <see cref="UnityCoreManager"/>, the <see cref="UnityTransportManager"/> and the
    /// <see cref="SteamTransport"/>, pick <see cref="SteamLinkProbeRole.Host"/> on one machine and <see cref="SteamLinkProbeRole.Client"/>
    /// with the host's Steam ID on the other, and enter play mode on both. The host logs its own Steam ID first.
    /// Read the host's log for whether probes arrive, and the client's for whether echoes come back and whether Steam refused a send. A
    /// probe that never arrives while the server's received byte count still climbs points at the engine; one that leaves no trace on
    /// the host at all points at the transport or at Steam.
    /// Starts Steam itself unless told something else in the game does.
    /// </remarks>
    [DisallowMultipleComponent]
    [NucleusHelpUrl(typeof(SteamLinkProbe))]
    public class SteamLinkProbe : MonoBehaviour
    {
        /// <summary>
        /// Which side of the link this machine plays.
        /// </summary>
        [SerializeField]
        [Tooltip("Host starts a server with its own client. Client connects to the host's Steam ID and sends the probes.")]
        private SteamLinkProbeRole _role = SteamLinkProbeRole.Host;
        /// <summary>
        /// The Steam ID a client connects to.
        /// </summary>
        [SerializeField]
        [Tooltip("Client only: the host's Steam ID, as the host logs it when it starts.")]
        private ulong _hostSteamId;
        /// <summary>
        /// How often a client sends a probe on each channel, and how often both sides log.
        /// </summary>
        [SerializeField]
        [Tooltip("Seconds between probes and between log lines.")]
        private float _intervalSeconds = 1f;
        /// <summary>
        /// Whether this component starts Steam and runs its callbacks.
        /// </summary>
        [SerializeField]
        [Tooltip("Start Steam and run its callbacks here. Turn off when something else in the game already does.")]
        private bool _isSteamStartedHere = true;

        /// <summary>
        /// The transport carrying the link, once added.
        /// </summary>
        private SteamNetworkingTransport _steamNetworkingTransport;
        /// <summary>
        /// The engine this probes through.
        /// </summary>
        private UnityCoreManager _unityCoreManager;
        /// <summary>
        /// Probes the host has had from each client, by connection identifier.
        /// </summary>
        private readonly Dictionary<uint, uint> _probeCountsByConnectionId = new();
        /// <summary>
        /// Probes a client has sent.
        /// </summary>
        private uint _sentProbeCount;
        /// <summary>
        /// Echoes a client has had back from the host.
        /// </summary>
        private uint _echoCount;
        /// <summary>
        /// Sends Steam refused, on either side.
        /// </summary>
        private uint _sendFailedCount;
        /// <summary>
        /// The received byte total at the last log line, so each line shows what arrived since.
        /// </summary>
        private long _lastTotalBytesReceived;
        /// <summary>
        /// The time of the next probe and log line.
        /// </summary>
        private float _nextIntervalTime;
        /// <summary>
        /// Whether the probe is running, set once the transport has been added and started.
        /// </summary>
        private bool _isRunning;
        /// <summary>
        /// Whether this component started Steam, so it shuts down only what it started.
        /// </summary>
        private bool _isSteamStarted;
        /// <summary>
        /// The line shown on screen.
        /// </summary>
        private string _statusLine = "Starting.";

        /// <summary>
        /// Starts Steam before anything asks it for a socket.
        /// </summary>
        private void Awake()
        {
            if (!_isSteamStartedHere)
                return;

            _isSteamStarted = SteamAPI.Init();

            if (!_isSteamStarted)
                Debug.LogError("[SteamLinkProbe] Steam could not be started. Run the Steam client, sign in, and put a steam_appid.txt holding an app id (480 for testing) beside the project.");
        }

        /// <summary>
        /// Adds the transport and starts this machine's side of the link.
        /// </summary>
        private async void Start()
        {
            _unityCoreManager = GetComponent<UnityCoreManager>();
            UnityTransportManager unityTransportManager = GetComponent<UnityTransportManager>();
            SteamTransport steamTransport = GetComponent<SteamTransport>();

            // Compared with ==, which is how Unity reports a component that is not there.
            if (_unityCoreManager == null || unityTransportManager == null || steamTransport == null)
            {
                Debug.LogError("[SteamLinkProbe] Put this on the GameObject that holds the UnityCoreManager, the UnityTransportManager and the SteamTransport.");

                return;
            }

            if (_role is SteamLinkProbeRole.Client)
                steamTransport.RemoteSteamId = _hostSteamId;

            await unityTransportManager.EnsureAddedAsync();

            foreach (Transport transport in _unityCoreManager.CoreManager.TransportManager.Transports)
            {
                if (transport is SteamNetworkingTransport steamNetworkingTransport)
                    _steamNetworkingTransport = steamNetworkingTransport;
            }

            if (_steamNetworkingTransport is null)
            {
                Debug.LogError("[SteamLinkProbe] No Steam transport was added. Make the SteamTransport the only NetworkTransport on this GameObject and its children.");

                return;
            }

            _steamNetworkingTransport.SendFailed += SteamNetworkingTransport_OnSendFailed;
            _unityCoreManager.CoreManager.TransportManager.ConnectionLocalStateChanged += TransportManager_OnConnectionLocalStateChanged;
            _unityCoreManager.CoreManager.ClientManager.LocalClientAuthenticated += ClientManager_OnLocalClientAuthenticated;
            _unityCoreManager.CoreManager.MessageManager.RegisterMessageHandler<SteamLinkProbeMessage>(OnProbeReceived);

            if (_role is SteamLinkProbeRole.Host)
            {
                Debug.Log($"[SteamLinkProbe] Hosting as Steam ID [{SteamUser.GetSteamID().m_SteamID}] over [{_steamNetworkingTransport.Addressing}]. Give this ID to the client.");

                await unityTransportManager.StartHostAsync();
            }
            else
            {
                Debug.Log($"[SteamLinkProbe] Connecting as Steam ID [{SteamUser.GetSteamID().m_SteamID}] to host [{_hostSteamId}] over [{_steamNetworkingTransport.Addressing}].");

                await unityTransportManager.StartClientAsync();
            }

            _lastTotalBytesReceived = _unityCoreManager.CoreManager.TransportManager.TotalBytesReceived;
            _nextIntervalTime = Time.unscaledTime + _intervalSeconds;
            _isRunning = true;
        }

        /// <summary>
        /// Runs Steam's callbacks, sends this interval's probes, and logs.
        /// </summary>
        private void Update()
        {
            if (_isSteamStarted)
                SteamAPI.RunCallbacks();

            if (!_isRunning || Time.unscaledTime < _nextIntervalTime)
                return;

            _nextIntervalTime = Time.unscaledTime + _intervalSeconds;

            if (_role is SteamLinkProbeRole.Host)
                LogHost();
            else
                SendAndLogClient();
        }

        /// <summary>
        /// Shows the latest log line on screen, so each machine can be read without its console.
        /// </summary>
        private void OnGUI() => GUI.Label(new(10f, 10f, Screen.width - 20f, 60f), _statusLine);

        /// <summary>
        /// Stops listening, and shuts Steam down when this component started it.
        /// </summary>
        private void OnDestroy()
        {
            if (_steamNetworkingTransport is not null)
                _steamNetworkingTransport.SendFailed -= SteamNetworkingTransport_OnSendFailed;

            if (_unityCoreManager != null && _unityCoreManager.CoreManager is not null)
            {
                _unityCoreManager.CoreManager.TransportManager.ConnectionLocalStateChanged -= TransportManager_OnConnectionLocalStateChanged;
                _unityCoreManager.CoreManager.ClientManager.LocalClientAuthenticated -= ClientManager_OnLocalClientAuthenticated;
                _unityCoreManager.CoreManager.MessageManager.UnregisterMessageHandler<SteamLinkProbeMessage>(OnProbeReceived);
            }

            if (_isSteamStarted)
                SteamAPI.Shutdown();
        }

        /// <summary>
        /// Logs the host's view: who is connected, what has arrived since the last line, and how many probes came from each client.
        /// </summary>
        private void LogHost()
        {
            Nucleus.Managers.Transports.TransportManager transportManager = _unityCoreManager.CoreManager.TransportManager;
            int remoteClientCount = 0;
            string probeCounts = string.Empty;

            foreach (Connection connection in transportManager.ActiveConnections)
            {
                if (connection.IsHostLoopback || connection.IsPeerStandIn)
                    continue;

                remoteClientCount++;
                _probeCountsByConnectionId.TryGetValue(connection.Id, out uint probeCount);
                probeCounts += $" [client {connection.Id} ({connection.RemoteAddress}), authenticated {connection.IsAuthenticated}, probes {probeCount}]";
            }

            long totalBytesReceived = transportManager.TotalBytesReceived;

            _statusLine = $"[SteamLinkProbe] Host: [{remoteClientCount}] remote client(s), [{totalBytesReceived - _lastTotalBytesReceived}] bytes received this interval, [{_sendFailedCount}] refused sends.{probeCounts}";
            _lastTotalBytesReceived = totalBytesReceived;

            Debug.Log(_statusLine);
        }

        /// <summary>
        /// Sends one probe on each channel once authenticated, and logs the client's view.
        /// </summary>
        private void SendAndLogClient()
        {
            Nucleus.Managers.Transports.TransportManager transportManager = _unityCoreManager.CoreManager.TransportManager;
            bool isAuthenticated = transportManager.TryGetLocalClientConnection(out Connection localClientConnection) && localClientConnection.IsAuthenticated;

            if (isAuthenticated && transportManager.TryGetServerConnection(out Connection serverConnection))
            {
                serverConnection.SendMessage(Channel.Reliable, new SteamLinkProbeMessage { Sequence = ++_sentProbeCount, IsReliable = true });
                serverConnection.SendMessage(Channel.Unreliable, new SteamLinkProbeMessage { Sequence = ++_sentProbeCount, IsReliable = false });
            }

            _statusLine = $"[SteamLinkProbe] Client: authenticated [{isAuthenticated}], [{_sentProbeCount}] probes sent, [{_echoCount}] echoes back, [{_sendFailedCount}] refused sends.";

            Debug.Log(_statusLine);
        }

        /// <summary>
        /// Counts a probe on the host and echoes it, or counts an echo on the client.
        /// </summary>
        /// <param name="messageContext">Who sent it and on which channel.</param>
        /// <param name="steamLinkProbeMessage">The probe.</param>
        private void OnProbeReceived(in MessageContext messageContext, SteamLinkProbeMessage steamLinkProbeMessage)
        {
            if (_role is SteamLinkProbeRole.Client)
            {
                _echoCount++;

                return;
            }

            Connection senderConnection = messageContext.SenderConnection;

            _probeCountsByConnectionId.TryGetValue(senderConnection.Id, out uint probeCount);
            _probeCountsByConnectionId[senderConnection.Id] = probeCount + 1;

            senderConnection.SendMessage(messageContext.Channel, steamLinkProbeMessage);
        }

        /// <summary>
        /// Logs this machine's own link coming up or going down, on either side, as it happens.
        /// </summary>
        /// <param name="connectionStateChange">The change.</param>
        private void TransportManager_OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            LocalConnectionState localConnectionState = connectionStateChange.Connection.LocalState;

            if (localConnectionState is LocalConnectionState.Connected)
                Debug.Log($"[SteamLinkProbe] The [{connectionStateChange.Invoker}] side is connected.");
            else if (localConnectionState is LocalConnectionState.Disconnected)
                Debug.LogWarning($"[SteamLinkProbe] The [{connectionStateChange.Invoker}] side lost its link.");
        }

        /// <summary>
        /// Logs this machine's client being admitted by the server, which is when probes start.
        /// </summary>
        /// <param name="localClientConnection">This machine's client.</param>
        private void ClientManager_OnLocalClientAuthenticated(Connection localClientConnection) => Debug.Log($"[SteamLinkProbe] Authenticated with the server as client [{localClientConnection.Id}].");

        /// <summary>
        /// Logs a send Steam refused, with its reason.
        /// </summary>
        /// <param name="invoker">Which side was sending.</param>
        /// <param name="connection">Who the packet was for.</param>
        /// <param name="channel">The channel it was sent on.</param>
        /// <param name="result">Steam's reason.</param>
        private void SteamNetworkingTransport_OnSendFailed(Invoker invoker, Connection connection, Channel channel, EResult result)
        {
            _sendFailedCount++;

            Debug.LogWarning($"[SteamLinkProbe] Steam refused a [{channel}] send from the [{invoker}] side to [{(connection is null ? "nobody" : connection.RemoteAddress)}]: [{result}].");
        }
    }
}
