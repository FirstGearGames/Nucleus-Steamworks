// Matches Steamworks.NET's own switch, so the Steam half compiles out wherever Steamworks.NET does, not only where it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using Nucleus.Integrations.Unity.Transports;
#if NUCLEUS_STEAMWORKS_ENABLED
using Nucleus.Transports;
#endif

namespace Nucleus.Integrations.Unity.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="SteamTransport"/>: whether Steamworks.NET is installed, with a button to install it, the
    /// running server's address with a button to copy it, how peers find each other, who a client connects to, and how the server listens
    /// and admits players. All presentation lives here per project convention.
    /// </summary>
    [CustomEditor(typeof(SteamTransport))]
    [CanEditMultipleObjects]
    public class SteamTransportEditor : UnityEditor.Editor
    {
        /// <summary>
        /// The Package Manager's install of Steamworks.NET, or <see langword="null"/> before the button is pressed.
        /// </summary>
        private AddRequest _addRequest;
        /// <summary>
        /// The git URL Steamworks.NET installs from.
        /// </summary>
        private const string SteamworksPackageUrl = "https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net";
        /// <summary>
        /// Where Steamworks.NET is published, for a reader who wants to install it another way.
        /// </summary>
        private const string SteamworksHomeUrl = "https://github.com/rlabrecque/Steamworks.NET";
        /// <summary>
        /// The width of the button that copies the server's address.
        /// </summary>
        private const float CopyButtonWidth = 48f;
        /// <summary>
        /// What every addressing needs from the game, said once under the addressing's own help.
        /// </summary>
        private const string SteamOwnershipText = "Steam must already be initialised, and its callbacks run every frame, by something else in the game, such as Heathen's Foundation for Steamworks or a SteamManager.";

        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            NucleusInspectorGUI.DrawScriptField(serializedObject);

            DrawSteamworksStatus();

            serializedObject.Update();

            SerializedProperty addressingProperty = serializedObject.FindProperty("_addressing");
            // A mixed selection shows every field, so nothing a selected transport uses is hidden.
            bool isMixed = addressingProperty.hasMultipleDifferentValues;
            bool isSteamIdShown = isMixed || addressingProperty.enumValueIndex == (int)SteamAddressing.SteamId;
            bool isIpAddressShown = isMixed || addressingProperty.enumValueIndex == (int)SteamAddressing.IpAddress;
            bool isFakeIpShown = isMixed || addressingProperty.enumValueIndex == (int)SteamAddressing.FakeIp;

            EditorGUILayout.HelpBox($"{GetAddressingText(isSteamIdShown, isIpAddressShown, isFakeIpShown)}\n\n{SteamOwnershipText}", MessageType.Info);

            EditorGUILayout.PropertyField(addressingProperty);

            using (new EditorGUI.IndentLevelScope())
            {
                if (isSteamIdShown)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_remoteSteamId"));

                if (isIpAddressShown || isFakeIpShown)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_remoteAddress"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_expectedServerSteamId"));
                }

                if (isSteamIdShown || isFakeIpShown)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_isRelayOnlyEnabled"));

                if (isIpAddressShown)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_port"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_bindAddress"));
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_isGameServer"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maximumConnections"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maximumConnectionsPerSteamId"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maximumTransmissionUnit"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_sendRateKilobytesPerSecond"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Players", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_ticketAuthenticationEnabled"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_shareSteamIdsEnabled"));

            serializedObject.ApplyModifiedProperties();
        }

        /// <summary>
        /// Draws whether Steamworks.NET is installed and compiled in, with a button to install it when it is missing, and, while this
        /// transport's server runs, where clients reach it, with a button to copy that.
        /// </summary>
        private void DrawSteamworksStatus()
        {
#if NUCLEUS_STEAMWORKS_ENABLED
            if (targets.Length == 1)
                DrawServerAddressRow();
#elif NUCLEUS_STEAMWORKS
            EditorGUILayout.HelpBox(SteamTransport.SteamworksDisabledText, MessageType.Warning);
#else
            EditorGUILayout.HelpBox("Steamworks.NET is not installed, so the Steam transport is compiled out. Install it as a package; the Package Manager needs Git for that.", MessageType.Warning);

            bool isInstalling = _addRequest is not null && !_addRequest.IsCompleted;

            if (_addRequest is not null && _addRequest.IsCompleted && _addRequest.Status is StatusCode.Failure)
                EditorGUILayout.HelpBox($"Steamworks.NET could not be installed: {_addRequest.Error?.message}", MessageType.Error);

            using (new EditorGUI.DisabledScope(isInstalling))
            {
                if (GUILayout.Button(isInstalling ? "Installing Steamworks.NET..." : "Install Steamworks.NET"))
                    _addRequest = Client.Add(SteamworksPackageUrl);
            }

            if (EditorGUILayout.LinkButton("Steamworks.NET on GitHub"))
                Application.OpenURL(SteamworksHomeUrl);

            // The request finishes on its own; repainting is what shows it finishing.
            if (isInstalling)
                Repaint();
#endif
            EditorGUILayout.Space();
        }
#if NUCLEUS_STEAMWORKS_ENABLED

        /// <summary>
        /// Draws where clients reach this transport's running server, with a button to copy it: its Steam ID, its fake IP address once Steam
        /// grants one, or its port.
        /// </summary>
        private void DrawServerAddressRow()
        {
            SteamNetworkingTransport steamNetworkingTransport = ((SteamTransport)target).SteamNetworkingTransport;

            if (!Application.isPlaying || steamNetworkingTransport is not { ServerSocket: { State: LocalConnectionState.Connected } })
                return;

            string label;
            string copyText;

            switch (steamNetworkingTransport.Addressing)
            {
                case SteamAddressing.IpAddress:
                    label = "Server port";
                    copyText = steamNetworkingTransport.Configuration.Port.ToString();

                    break;
                case SteamAddressing.FakeIp:
                    label = "Server fake IP";
                    copyText = steamNetworkingTransport.ServerFakeAddress;

                    break;
                default:
                    label = "Server Steam ID";
                    copyText = steamNetworkingTransport.ServerSteamId.ToString();

                    break;
            }

            Rect rowRect = EditorGUILayout.GetControlRect();
            Rect valueRect = new(rowRect.x, rowRect.y, rowRect.width - CopyButtonWidth - EditorGUIUtility.standardVerticalSpacing, rowRect.height);

            EditorGUI.LabelField(valueRect, label, copyText ?? "Waiting for Steam to grant it...");

            using (new EditorGUI.DisabledScope(copyText is null))
            {
                if (GUI.Button(new(rowRect.xMax - CopyButtonWidth, rowRect.y, CopyButtonWidth, rowRect.height), "Copy", EditorStyles.miniButton))
                    EditorGUIUtility.systemCopyBuffer = copyText;
            }

            // The fake IP address arrives a moment after the server starts.
            if (copyText is null)
                Repaint();
        }
#endif

        /// <summary>
        /// Returns the help for the addressing shown, or for all of them when a mixed selection shows more than one.
        /// </summary>
        /// <param name="isSteamIdShown">Whether Steam ID addressing is shown.</param>
        /// <param name="isIpAddressShown">Whether IP addressing is shown.</param>
        /// <param name="isFakeIpShown">Whether fake IP addressing is shown.</param>
        /// <returns>The help text.</returns>
        private static string GetAddressingText(bool isSteamIdShown, bool isIpAddressShown, bool isFakeIpShown)
        {
            if (isFakeIpShown && !isSteamIdShown && !isIpAddressShown)
                return "Peers connect by a fake IP address Steam grants the server, carried over Valve's relay network, so a dedicated server's real address is never shown and no port is opened. The server logs its address once Steam grants it; a client sets it as Remote Address. A game server asks Steam for one between GameServer.Init and logging on.";

            if (isIpAddressShown && !isSteamIdShown && !isFakeIpShown)
                return "Peers connect by IP address and port, the way a dedicated server is usually reached. The server listens on its port, which must be open to the players joining it.";

            return "Peers reach each other by Steam ID through Valve's relay network, so there is no port to forward and no address to share. A host hands its Steam ID to the players joining it.";
        }
    }
}
