using System;
using System.Runtime.InteropServices;
using CodeBoost.Performance;
using Nucleus.Transports;
using Steamworks;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// Reads and sends Steam Networking Sockets messages without allocating.
    /// </summary>
    /// <remarks>
    /// Steamworks.NET converts a received message with <c>Marshal.PtrToStructure</c>, which allocates on every call. The few fields read here
    /// are taken from the native message directly at offsets computed once, so receiving costs only the pooled copy of the payload.
    /// </remarks>
    internal static class SteamMessages
    {
        /// <summary>
        /// The most messages taken from Steam in one call.
        /// </summary>
        internal const int MaximumMessagesPerReceive = 256;

        /// <summary>
        /// Where the payload pointer sits in a native message.
        /// </summary>
        private static readonly int DataOffset = (int)Marshal.OffsetOf(typeof(SteamNetworkingMessage_t), nameof(SteamNetworkingMessage_t.m_pData));
        /// <summary>
        /// Where the payload length sits in a native message.
        /// </summary>
        private static readonly int SizeOffset = (int)Marshal.OffsetOf(typeof(SteamNetworkingMessage_t), nameof(SteamNetworkingMessage_t.m_cbSize));
        /// <summary>
        /// Where the connection handle sits in a native message.
        /// </summary>
        private static readonly int ConnectionOffset = (int)Marshal.OffsetOf(typeof(SteamNetworkingMessage_t), nameof(SteamNetworkingMessage_t.m_conn));
        /// <summary>
        /// Where the send flags sit in a native message.
        /// </summary>
        private static readonly int FlagsOffset = (int)Marshal.OffsetOf(typeof(SteamNetworkingMessage_t), nameof(SteamNetworkingMessage_t.m_nFlags));

        /// <summary>
        /// Copies a received message into a pooled array and releases the native message.
        /// </summary>
        /// <param name="message">The native message Steam handed back.</param>
        /// <param name="connection">The connection the message arrived on.</param>
        /// <param name="channel">The channel the sender used.</param>
        /// <returns>The payload, over an array rented from <see cref="TrackedArrayPool{T0}"/> which the caller now owns.</returns>
        internal static ArraySegment<byte> TakePayload(IntPtr message, out HSteamNetConnection connection, out Channel channel)
        {
            int size = Marshal.ReadInt32(message, SizeOffset);
            connection = new((uint)Marshal.ReadInt32(message, ConnectionOffset));
            channel = (Marshal.ReadInt32(message, FlagsOffset) & Constants.k_nSteamNetworkingSend_Reliable) != 0 ? Channel.Reliable : Channel.Unreliable;

            byte[] payloadArray = TrackedArrayPool<byte>.Rent(size);
            Marshal.Copy(Marshal.ReadIntPtr(message, DataOffset), payloadArray, 0, size);

            SteamNetworkingMessage_t.Release(message);

            return new(payloadArray, 0, size);
        }

        /// <summary>
        /// Sends a payload on a connection, reliably or not as its channel says.
        /// </summary>
        /// <param name="isGameServer">True when the connection belongs to the game server interface.</param>
        /// <param name="connection">The connection to send on.</param>
        /// <param name="channel">The channel the engine chose.</param>
        /// <param name="payload">The payload, which Steam copies before this returns.</param>
        /// <returns><c>k_EResultOK</c> when Steam accepted the payload, otherwise Steam's reason for refusing it.</returns>
        /// <remarks>
        /// Sent without Nagle's delay, because the engine already gathers a tick's data into as few packets as it can, and holding them
        /// longer would only add latency.
        /// </remarks>
        internal static EResult Send(bool isGameServer, HSteamNetConnection connection, Channel channel, ArraySegment<byte> payload)
        {
            if (payload.Array is null)
                return EResult.k_EResultInvalidParam;

            int sendFlags = channel is Channel.Reliable ? Constants.k_nSteamNetworkingSend_ReliableNoNagle : Constants.k_nSteamNetworkingSend_UnreliableNoNagle;

            GCHandle pinnedPayload = GCHandle.Alloc(payload.Array, GCHandleType.Pinned);

            try
            {
                IntPtr data = pinnedPayload.AddrOfPinnedObject() + payload.Offset;

                return SteamSockets.SendMessageToConnection(isGameServer, connection, data, (uint)payload.Count, sendFlags);
            }
            finally
            {
                pinnedPayload.Free();
            }
        }
    }
}
