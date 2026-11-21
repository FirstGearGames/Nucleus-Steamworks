#if UNITY_INCLUDE_TESTS
using System;
using Nucleus.Managers.Messages;
using Nucleus.Serializers;

namespace Nucleus.Integrations.Unity.Tests
{
    /// <summary>
    /// A message as large as a case asks, which the Steam play tests send a server to find where its client packet limit lies.
    /// </summary>
    [NetworkType]
    public struct SteamBulkMessage : IMessage
    {
        /// <inheritdoc/>
        public bool IsAuthenticationRequired => true;

        /// <summary>
        /// The bytes sent. Read into an array rented from <c>TrackedArrayPool</c>, which the receiving handler returns.
        /// </summary>
        public ArraySegment<byte> Payload;
    }
}
#endif
