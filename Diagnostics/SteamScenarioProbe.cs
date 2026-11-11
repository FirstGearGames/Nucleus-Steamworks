using System.Collections.Generic;
using Nucleus.Connections;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Integrations.Unity.Systems;
using Nucleus.Managers.Rpc;
using Nucleus.Systems;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// Runs a game's own client-to-server traffic over Steam: the client moves the player object it controls, which its
    /// <c>NetworkTransform</c> sends, and calls a reliable remote call on it addressed to the server every interval. The host logs where
    /// each client's player object stands as the server sees it and how many calls arrived from each client.
    /// </summary>
    /// <remarks>
    /// Drop it into a scene whose demo driver spawns a player object per client and gives that client control, such as
    /// <c>Spawning/PlayerObjects</c>, set <see cref="SteamLinkProbeRole.Host"/> or <see cref="SteamLinkProbeRole.Client"/> with the host's
    /// Steam ID, and set the demo driver's own starting role to match. It adds a <see cref="SteamTransport"/> before the driver starts and
    /// starts Steam itself.
    /// The calls are made with <see cref="RpcSendAccess.AnyClient"/>, which the host sets on every player object as it appears.
    /// A host whose logged position for a client never changes, or whose call count stays at zero, has reproduced the report; the
    /// client's own log says whether it is the controller and whether each call was accepted for sending.
    /// </remarks>
    [DisallowMultipleComponent]
    [NucleusHelpUrl(typeof(SteamScenarioProbe))]
    public class SteamScenarioProbe : MonoBehaviour
    {
        /// <summary>
        /// Which side of the link this machine plays.
        /// </summary>
        [SerializeField]
        [Tooltip("Host counts what arrives. Client moves its player object and calls the server. Set the demo driver's starting role to match.")]
        private SteamLinkProbeRole _role = SteamLinkProbeRole.Host;
        /// <summary>
        /// The Steam ID a client connects to.
        /// </summary>
        [SerializeField]
        [Tooltip("Client only: the host's Steam ID.")]
        private ulong _hostSteamId;
        /// <summary>
        /// How often a client calls the server, and how often both sides log.
        /// </summary>
        [SerializeField]
        [Tooltip("Seconds between remote calls and between log lines.")]
        private float _intervalSeconds = 1f;
        /// <summary>
        /// How far from the scene origin this machine walks the player object its own client controls.
        /// </summary>
        /// <remarks>The host walks twice as far out, so the two player objects never meet.</remarks>
        [SerializeField]
        [Tooltip("Radius of the circle this machine walks its own player object around, in metres. The host walks twice as far out.")]
        private float _circleRadius = 4f;

        /// <summary>
        /// Player objects the host has opened to calls from any client and listens on, so each is set up once.
        /// </summary>
        private readonly HashSet<NetworkSystem> _hookedSystems = new();
        /// <summary>
        /// Calls the host has had, by the identifier of the client that made them.
        /// </summary>
        private readonly Dictionary<uint, uint> _callCountsByConnectionId = new();
        /// <summary>
        /// Calls a client has sent, and how many of those the engine refused to send.
        /// </summary>
        private uint _sentCallCount;
        /// <summary>
        /// Calls the engine would not send.
        /// </summary>
        private uint _refusedCallCount;
        /// <summary>
        /// Where the client is around its circle, in radians.
        /// </summary>
        private float _circleAngle;
        /// <summary>
        /// The time of the next call and log line.
        /// </summary>
        private float _nextIntervalTime;
        /// <summary>
        /// Whether this component started Steam, so it shuts down only what it started.
        /// </summary>
        private bool _isSteamStarted;
        /// <summary>
        /// The line shown on screen.
        /// </summary>
        private string _statusLine = "Starting.";

        /// <summary>
        /// Starts Steam and puts a Steam transport in place before the demo driver adds one of its own.
        /// </summary>
        private void Awake()
        {
            _isSteamStarted = SteamAPI.Init();

            if (!_isSteamStarted)
            {
                Debug.LogError("[SteamScenarioProbe] Steam could not be started. Run the Steam client, sign in, and put a steam_appid.txt holding an app id (480 for testing) beside the project.");

                return;
            }

            UnityTransportManager unityTransportManager = FindAnyObjectByType<UnityTransportManager>();

            if (unityTransportManager == null)
            {
                Debug.LogError("[SteamScenarioProbe] Found no UnityTransportManager in the scene.");

                return;
            }

            SteamTransport steamTransport = unityTransportManager.GetComponent<SteamTransport>();

            if (steamTransport == null)
                steamTransport = unityTransportManager.gameObject.AddComponent<SteamTransport>();

            if (_role is SteamLinkProbeRole.Client)
                steamTransport.RemoteSteamId = _hostSteamId;

            Debug.Log($"[SteamScenarioProbe] [{_role}] as Steam ID [{SteamUser.GetSteamID().m_SteamID}]{(_role is SteamLinkProbeRole.Client ? $", connecting to host [{_hostSteamId}]" : string.Empty)}. Set the demo driver's starting role to [{_role}].");

            _nextIntervalTime = Time.unscaledTime + _intervalSeconds;
        }

        /// <summary>
        /// Runs Steam's callbacks, walks the client's player object, and makes this interval's call and log line.
        /// </summary>
        private void Update()
        {
            if (!_isSteamStarted)
                return;

            SteamAPI.RunCallbacks();

            WalkControlledPlayerObject();

            if (Time.unscaledTime < _nextIntervalTime)
                return;

            _nextIntervalTime = Time.unscaledTime + _intervalSeconds;

            if (_role is SteamLinkProbeRole.Host)
                HookAndLogHost();
            else
                CallAndLogClient();
        }

        /// <summary>
        /// Shows the latest log line on screen.
        /// </summary>
        private void OnGUI() => GUI.Label(new(10f, 70f, Screen.width - 20f, 80f), _statusLine);

        /// <summary>
        /// Shuts Steam down when this component started it.
        /// </summary>
        private void OnDestroy()
        {
            if (_isSteamStarted)
                SteamAPI.Shutdown();
        }

        /// <summary>
        /// Moves the player object this machine's own client controls around a circle, as a player holding a key would. On the host that
        /// is the host's own player, so both sides of the link are seen moving.
        /// </summary>
        private void WalkControlledPlayerObject()
        {
            if (!TryFindControlledPlayerObject(out NucleusBehaviour nucleusBehaviour))
                return;

            _circleAngle += Time.deltaTime;

            Transform playerTransform = nucleusBehaviour.transform;

            float circleRadius = _role is SteamLinkProbeRole.Host ? _circleRadius * 2f : _circleRadius;

            playerTransform.position = new(Mathf.Cos(_circleAngle) * circleRadius, playerTransform.position.y, Mathf.Sin(_circleAngle) * circleRadius);
        }

        /// <summary>
        /// Calls the server once on the player object this client controls, and logs the client's view.
        /// </summary>
        private void CallAndLogClient()
        {
            bool isControlling = TryFindControlledPlayerObject(out NucleusBehaviour nucleusBehaviour);

            if (isControlling)
            {
                _sentCallCount++;

                if (!nucleusBehaviour.NetworkSystem.TrySendRpc(RpcTarget.Server, Channel.Reliable, new SteamScenarioPingRpc { Sequence = _sentCallCount }))
                    _refusedCallCount++;
            }

            Vector3 position = isControlling ? nucleusBehaviour.transform.position : Vector3.zero;

            _statusLine = $"[SteamScenarioProbe] Client: controls a player object [{isControlling}], at [{position}], [{_sentCallCount}] calls made, [{_refusedCallCount}] refused for sending.";

            Debug.Log(_statusLine);
        }

        /// <summary>
        /// Opens each new player object to calls from any client and listens on it, then logs where each client's player object stands
        /// and how many calls each client made.
        /// </summary>
        private void HookAndLogHost()
        {
            string lines = string.Empty;

            foreach (NucleusBehaviour nucleusBehaviour in FindObjectsByType<NucleusBehaviour>(FindObjectsSortMode.None))
            {
                NetworkSystem networkSystem = nucleusBehaviour.NetworkSystem;

                if (networkSystem is null)
                    continue;

                if (_hookedSystems.Add(networkSystem))
                {
                    networkSystem.TrySetRpcAccess(RpcSendAccess.AnyClient);
                    networkSystem.RegisterRpcHandler<SteamScenarioPingRpc>(OnPingReceived);
                }

                uint controllerConnectionId = networkSystem.ControllerConnectionId;

                if (controllerConnectionId == NetworkSystem.UnsetId || nucleusBehaviour.IsController(ControllerType.Client))
                    continue;

                _callCountsByConnectionId.TryGetValue(controllerConnectionId, out uint callCount);
                lines += $" [client {controllerConnectionId}: player object at {nucleusBehaviour.transform.position}, calls {callCount}]";
            }

            _statusLine = $"[SteamScenarioProbe] Host:{(lines.Length == 0 ? " no client controls a player object yet." : lines)}";

            Debug.Log(_statusLine);
        }

        /// <summary>
        /// Counts a call on the host, by who made it, and keeps it from being passed on.
        /// </summary>
        /// <param name="rpcContext">Who made the call.</param>
        /// <param name="steamScenarioPingRpc">The call.</param>
        /// <returns><see cref="RpcRelayAction.Cancel"/>, since the call is for the server alone.</returns>
        private RpcRelayAction OnPingReceived(in RpcContext rpcContext, SteamScenarioPingRpc steamScenarioPingRpc)
        {
            Connection senderConnection = rpcContext.SenderConnection;

            if (senderConnection is not null)
            {
                _callCountsByConnectionId.TryGetValue(senderConnection.Id, out uint callCount);
                _callCountsByConnectionId[senderConnection.Id] = callCount + 1;
            }

            return RpcRelayAction.Cancel;
        }

        /// <summary>
        /// Finds the player object this machine's client controls.
        /// </summary>
        /// <param name="nucleusBehaviour">The behaviour on it.</param>
        /// <returns><see langword="true"/> when this client controls one.</returns>
        private static bool TryFindControlledPlayerObject(out NucleusBehaviour nucleusBehaviour)
        {
            foreach (NucleusBehaviour candidate in FindObjectsByType<NucleusBehaviour>(FindObjectsSortMode.None))
            {
                if (!candidate.IsController(ControllerType.Client))
                    continue;

                nucleusBehaviour = candidate;

                return true;
            }

            nucleusBehaviour = null;

            return false;
        }
    }
}
