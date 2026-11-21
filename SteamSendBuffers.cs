// Matches Steamworks.NET's own switch, so this compiles out wherever Steamworks.NET does, not only where it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
using System;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// The arrays one socket hands Steam to send a message on a lane and to give a connection its lanes, held by the socket so a send
    /// allocates nothing.
    /// </summary>
    internal sealed class SteamSendBuffers
    {
        /// <summary>
        /// The one message each send hands Steam.
        /// </summary>
        public readonly IntPtr[] Messages = new IntPtr[1];
        /// <summary>
        /// Steam's result for that message: its number, or its negated refusal.
        /// </summary>
        public readonly long[] Results = new long[1];
        /// <summary>
        /// The priority of each lane, the same for both.
        /// </summary>
        public readonly int[] LanePriorities = { SteamSockets.LanePriority, SteamSockets.LanePriority };
        /// <summary>
        /// The share of the send rate each lane gets while both have data queued.
        /// </summary>
        public readonly ushort[] LaneWeights = { SteamSockets.ReliableLaneWeight, SteamSockets.UnreliableLaneWeight };
    }
}
#endif
