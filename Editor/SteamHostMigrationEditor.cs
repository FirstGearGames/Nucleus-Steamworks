using Nucleus.Integrations.Unity.Transports;
using UnityEditor;

namespace Nucleus.Integrations.Unity.Editor
{
    /// <summary>
    /// Inspector presentation for <see cref="SteamHostMigration"/>: the shared host migration inspector, with the address an IP host is
    /// reached at. All presentation lives here per project convention.
    /// </summary>
    [CustomEditor(typeof(SteamHostMigration))]
    [CanEditMultipleObjects]
    public class SteamHostMigrationEditor : NetworkHostMigrationEditor
    {
        /// <inheritdoc/>
        protected override void DrawServiceSettings()
        {
            SteamTransport steamTransport = ((SteamHostMigration)target).GetComponent<SteamTransport>();

            // Only a host addressed by IP has an address of its own to publish. A mixed selection shows it, so nothing in use is hidden.
            if (!serializedObject.isEditingMultipleObjects && steamTransport != null && steamTransport.Addressing is not SteamAddressing.IpAddress)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Steam", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_hostedAddress"));
        }
    }
}
