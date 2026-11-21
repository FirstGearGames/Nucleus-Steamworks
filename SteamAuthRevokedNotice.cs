// Matches Steamworks.NET's own switch, so this compiles out wherever Steamworks.NET does, not only where it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// The reason a server gives a client it kicks because Steam withdrew its approval mid-session, such as for a VAC ban.
    /// </summary>
    [NetworkType]
    public struct SteamAuthRevokedNotice : IMessage
    {
        /// <inheritdoc/>
        public bool IsAuthenticationRequired => true;

        /// <summary>
        /// Steam's verdict, as its <c>EAuthSessionResponse</c> name.
        /// </summary>
        public string Response;
    }
}
#endif
