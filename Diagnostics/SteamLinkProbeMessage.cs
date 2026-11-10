using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// One numbered probe a <see cref="SteamLinkProbe"/> client sends to the server, and the server echoes back.
    /// </summary>
    [NetworkType]
    public struct SteamLinkProbeMessage : IMessage
    {
        /// <inheritdoc/>
        public bool IsAuthenticationRequired => true;

        /// <summary>
        /// The probe's number, counting every probe the client has sent.
        /// </summary>
        public uint Sequence;

        /// <summary>
        /// Whether it was sent on the reliable channel.
        /// </summary>
        public bool IsReliable;
    }
}
