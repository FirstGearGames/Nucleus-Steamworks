// Matches Steamworks.NET's own switch, so this compiles out wherever Steamworks.NET does, not only where it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
using System;
using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// The Steam auth session ticket a client sends its server, so <see cref="SteamTicketAuthenticator"/> can check it with Steam.
    /// </summary>
    [NetworkType]
    public struct SteamAuthTicketMessage : IMessage
    {
        /// <inheritdoc/>
        /// <remarks>False, since the client sending it is not authenticated yet.</remarks>
        public bool IsAuthenticationRequired => false;

        /// <summary>
        /// The ticket as Steam wrote it.
        /// </summary>
        /// <remarks>
        /// The client points it into its ticket buffer. The server reads it into an array rented from <c>TrackedArrayPool</c>, which its
        /// handler returns.
        /// </remarks>
        public ArraySegment<byte> Ticket;
    }
}
#endif
