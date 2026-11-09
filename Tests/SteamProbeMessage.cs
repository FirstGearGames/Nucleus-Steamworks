#if UNITY_INCLUDE_TESTS
using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.Unity.Tests
{
    /// <summary>
    /// A one-byte message the Steam play tests send each way, to prove data crosses the transport rather than only the handshake.
    /// </summary>
    [NetworkType]
    public struct SteamProbeMessage : IMessage
    {
        /// <inheritdoc/>
        public bool IsAuthenticationRequired => true;

        /// <summary>
        /// The value sent, checked on arrival.
        /// </summary>
        public byte Value;
    }
}
#endif
