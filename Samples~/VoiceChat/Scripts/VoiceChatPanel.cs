// Matches Steamworks.NET's own platform check, so this compiles out wherever Steamworks.NET does, and not only when it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
#if NUCLEUS_STEAMWORKS_ENABLED
using Nucleus.Connections;
using Nucleus.Integrations.Unity;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Integrations.Unity.Transports;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using UnityEngine;

namespace Nucleus.Integrations.Unity.Steamworks.Samples.VoiceChat
{
    /// <summary>
    /// The Voice Chat panel: host, or join a host by its Steam ID, and leave. The host copies its Steam ID from here and sends it to a
    /// friend, who pastes it and joins; once in, each player holds the push to talk key <see cref="VoiceChatPlayer"/> reads.
    /// </summary>
    /// <remarks>
    /// Joining is one property and one call: <see cref="SteamTransport.RemoteSteamId"/> names the host, and
    /// <see cref="UnityTransportManager.StartClientAsync()"/> dials it through Steam's relay. The transport must be set to
    /// <see cref="SteamAddressing.SteamId"/>.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://github.com/FirstGearGames/Nucleus-Steamworks/blob/main/Samples~/VoiceChat/README.md")]
    public class VoiceChatPanel : MonoBehaviour
    {
        /// <summary>
        /// The bootstrap that owns Steam, read for whether Steam is up and the local Steam ID.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Bootstrap.")]
        private SteamBootstrap _steamBootstrap;
        /// <summary>
        /// The transport manager the panel starts and stops.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Unity Transport Manager.")]
        private UnityTransportManager _unityTransportManager;
        /// <summary>
        /// The Steam transport, which a join points at the host's Steam ID.
        /// </summary>
        [SerializeField]
        [Tooltip("The scene's Steam Transport, set to Steam ID addressing.")]
        private SteamTransport _steamTransport;
        /// <summary>
        /// The skin the panel draws with.
        /// </summary>
        /// <remarks>Left empty, the panel draws with Unity's default look.</remarks>
        [SerializeField]
        [Tooltip("The skin the panel draws with. Leave empty for Unity's default look.")]
        private GUISkin _guiSkin;

        /// <summary>
        /// The host's Steam ID as typed or pasted into the panel.
        /// </summary>
        private string _serverSteamIdText = string.Empty;
        /// <summary>
        /// The bound transport manager, watched for this peer's client connecting or stopping.
        /// </summary>
        private TransportManager _transportManager;
        /// <summary>
        /// The line under the Steam ID saying what the session is doing.
        /// </summary>
        private string _statusText = "Host, or paste a host's Steam ID and join.";
        /// <summary>
        /// Whether this peer has hosted or joined since it last left, which swaps the start buttons for Leave.
        /// </summary>
        private bool _hasStarted;
        /// <summary>
        /// Whether this peer is the host, whose own client stopping is part of its own Leave.
        /// </summary>
        private bool _isHost;
        /// <summary>
        /// Whether this peer's client connected since it joined, which tells a host that left from a join that never connected.
        /// </summary>
        private bool _hasConnected;

        /// <summary>
        /// Checks that every reference is set, then watches this peer's client connection.
        /// </summary>
        private void Awake()
        {
            if (_steamBootstrap == null || _unityTransportManager == null || _steamTransport == null || NucleusUnity.BoundCoreManager is null)
            {
                Debug.LogError($"[{nameof(VoiceChatPanel)}] needs its [{nameof(SteamBootstrap)}], [{nameof(UnityTransportManager)}] and [{nameof(SteamTransport)}] set, and a bound CoreManager.");
                enabled = false;

                return;
            }

            _transportManager = NucleusUnity.BoundCoreManager.TransportManager;
            _transportManager.ConnectionLocalStateChanged += OnConnectionLocalStateChanged;
        }

        /// <summary>
        /// Drops the subscription.
        /// </summary>
        private void OnDestroy()
        {
            if (_transportManager is not null)
                _transportManager.ConnectionLocalStateChanged -= OnConnectionLocalStateChanged;
        }

        /// <summary>
        /// Draws the panel.
        /// </summary>
        private void OnGUI()
        {
            SampleGUI.BeginPanel(_guiSkin, 364f, "Voice Chat");

            if (!_steamBootstrap.IsSteamInitialized)
            {
                GUILayout.Label("Steam is not running, so this peer cannot host or join.");
                SampleGUI.EndPanel();

                return;
            }

            GUILayout.Label($"My Steam ID: {_steamBootstrap.OwnSteamId}");

            if (GUILayout.Button("Copy my Steam ID"))
                GUIUtility.systemCopyBuffer = _steamBootstrap.OwnSteamId.ToString();

            GUILayout.Label(_statusText);

            if (_hasStarted)
            {
                GUILayout.Label("Hold the push to talk key (V by default) to talk.");

                if (GUILayout.Button("Leave"))
                    Leave();
            }
            else
            {
                if (GUILayout.Button("Host"))
                    Host();

                GUILayout.Label("Host's Steam ID");
                _serverSteamIdText = GUILayout.TextField(_serverSteamIdText);

                if (GUILayout.Button("Join"))
                    Join();
            }

            SampleGUI.EndPanel();
        }

        /// <summary>
        /// Starts this peer as a host: a server, and a client of its own that joins it in process.
        /// </summary>
        private async void Host()
        {
            _hasStarted = true;
            _isHost = true;
            _statusText = "Hosting.";
            await _unityTransportManager.StartHostAsync();
        }

        /// <summary>
        /// Starts this peer as a client of the typed Steam ID.
        /// </summary>
        private async void Join()
        {
            if (!ulong.TryParse(_serverSteamIdText.Trim(), out ulong serverSteamId))
            {
                Debug.LogWarning($"[{nameof(VoiceChatPanel)}] could not read [{_serverSteamIdText}] as a Steam ID; paste the host's 17 digit Steam ID.");

                return;
            }

            _steamTransport.RemoteSteamId = serverSteamId;
            _hasStarted = true;
            _hasConnected = false;
            _statusText = "Connecting.";
            await _unityTransportManager.StartClientAsync();
        }

        /// <summary>
        /// Stops every role this peer runs. A host's players are despawned, and a client drops what it received.
        /// </summary>
        /// <param name="statusText">The line shown once the panel is back on its start buttons.</param>
        private void Leave(string statusText = "Host, or paste a host's Steam ID and join.")
        {
            _hasStarted = false;
            _isHost = false;
            _statusText = statusText;
            _unityTransportManager.ShutdownAll();
        }

        /// <summary>
        /// Shows a joined client's progress, and returns to the start buttons once it stops or fails to connect, since the host it
        /// dialled is gone.
        /// </summary>
        /// <param name="connectionStateChange">The state change that was raised.</param>
        private void OnConnectionLocalStateChanged(ConnectionStateChange connectionStateChange)
        {
            if (!_hasStarted || _isHost || connectionStateChange.Invoker is not Invoker.Client)
                return;

            switch (connectionStateChange.Connection.LocalState)
            {
                case LocalConnectionState.Connected:
                    _hasConnected = true;
                    _statusText = "Connected to the host.";

                    break;
                case LocalConnectionState.Disconnected:
                    Leave(_hasConnected ? "The host left, or the connection dropped." : "Could not connect to the host.");
                    break;
            }
        }
    }
}
#endif
