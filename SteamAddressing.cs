using UnityEngine;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// How peers on a <see cref="SteamNetworkingTransport"/> find each other.
    /// </summary>
    public enum SteamAddressing : byte
    {
        /// <summary>
        /// By Steam ID, through Valve's relay network, so nobody opens a port or shares an address. Suits a player hosting from the game.
        /// </summary>
        [InspectorName("Steam ID")]
        SteamId = 0,
        /// <summary>
        /// By IP address and port, the way a dedicated server is usually reached. The server listens on its bind address and port, and
        /// a client connects to <see cref="SteamNetworkingTransport.RemoteAddress"/>.
        /// </summary>
        [InspectorName("IP Address")]
        IpAddress = 1,
    }
}
