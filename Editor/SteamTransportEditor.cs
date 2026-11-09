using UnityEditor;
using Nucleus.Integrations.Unity.Transports;

namespace Nucleus.Integrations.Unity.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="SteamTransport"/>: how peers find each other, who a client connects to, and how the server
    /// listens and signs in to Steam. All presentation lives here per project convention.
    /// </summary>
    [CustomEditor(typeof(SteamTransport))]
    [CanEditMultipleObjects]
    public class SteamTransportEditor : UnityEditor.Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            NucleusInspectorGUI.DrawScriptField(serializedObject);

            serializedObject.Update();

            SerializedProperty addressingProperty = serializedObject.FindProperty("_addressing");
            // A mixed selection shows every field, so nothing a selected transport uses is hidden.
            bool isSteamIdShown = addressingProperty.hasMultipleDifferentValues || addressingProperty.enumValueIndex == (int)SteamAddressing.SteamId;
            bool isIpAddressShown = addressingProperty.hasMultipleDifferentValues || addressingProperty.enumValueIndex == (int)SteamAddressing.IpAddress;

            EditorGUILayout.HelpBox(isIpAddressShown && !isSteamIdShown
                ? "Peers connect by IP address and port, the way a dedicated server is usually reached. The server listens on its port, which must be open to the players joining it.\n\nSteam must already be initialised, and its callbacks run every frame, by something else in the game, such as Heathen's Foundation for Steamworks or a SteamManager."
                : "Peers reach each other by Steam ID through Valve's relay network, so there is no port to forward and no address to share. A host hands its Steam ID to the players joining it.\n\nSteam must already be initialised, and its callbacks run every frame, by something else in the game, such as Heathen's Foundation for Steamworks or a SteamManager.", MessageType.Info);

            EditorGUILayout.PropertyField(addressingProperty);

            using (new EditorGUI.IndentLevelScope())
            {
                if (isSteamIdShown)
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_remoteSteamId"));

                if (isIpAddressShown)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_remoteAddress"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_port"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_bindAddress"));
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_isGameServer"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maximumConnections"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_maximumTransmissionUnit"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_sendRateKilobytesPerSecond"));

            SerializedProperty hostMigrationProperty = serializedObject.FindProperty("_isHostMigrationEnabled");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Host Migration", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(hostMigrationProperty);

            if (hostMigrationProperty.hasMultipleDifferentValues || hostMigrationProperty.boolValue)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_directoryAddress"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_directoryPort"));

                    if (isIpAddressShown)
                        EditorGUILayout.PropertyField(serializedObject.FindProperty("_hostedAddress"));
                }
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
