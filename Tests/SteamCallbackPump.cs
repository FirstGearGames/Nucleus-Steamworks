#if UNITY_INCLUDE_TESTS
using Steamworks;
using UnityEngine;

namespace Nucleus.Integrations.Unity.Tests
{
    /// <summary>
    /// Runs Steam's callbacks every frame, as a game's own Steam manager would, so the transport hears connections open and close.
    /// </summary>
    public class SteamCallbackPump : MonoBehaviour
    {
        /// <summary>
        /// Whether a game server's callbacks are run as well as the player's.
        /// </summary>
        public bool IsGameServerPumped;

        /// <summary>
        /// Dispatches whatever Steam has queued since the last frame.
        /// </summary>
        private void Update()
        {
            SteamAPI.RunCallbacks();

            if (IsGameServerPumped)
                GameServer.RunCallbacks();
        }
    }
}
#endif
