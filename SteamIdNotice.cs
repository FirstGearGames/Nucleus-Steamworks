// Matches Steamworks.NET's own switch, so this compiles out wherever Steamworks.NET does, not only where it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// The server's word to a client that a connected client has a Steam ID, or has left, sent while
    /// <see cref="SteamNetworkingTransport.ShareSteamIdsEnabled"/> is on.
    /// </summary>
    [NetworkType]
    public struct SteamIdNotice : IMessage
    {
        /// <inheritdoc/>
        public bool IsAuthenticationRequired => true;

        /// <summary>
        /// The <see cref="Connections.Connection.Id"/> of the client this is about.
        /// </summary>
        public uint ConnectionId;

        /// <summary>
        /// The client's Steam ID as Steam authenticated its link, or <see cref="SteamNetworkingTransport.UnsetSteamId"/> when the client
        /// has left.
        /// </summary>
        public ulong SteamId;
    }
}
#endif
