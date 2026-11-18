using Nucleus.Integrations.Newfarm;
using Nucleus.Transports;
using UnityEngine;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// Host migration over Steam Networking Sockets: a session survives losing whoever hosts it, whether peers reach each other by Steam ID
    /// or by IP address.
    /// </summary>
    /// <remarks>
    /// Put it beside a <see cref="SteamTransport"/>; having it there is what turns host migration on. A game then starts and joins as it
    /// would without it: a server opens a session with the directory once it is running, and every client that authenticates is told
    /// which session it is in, so a peer that joined by Steam ID or address survives losing that host. A host addressed by Steam ID
    /// publishes its Steam ID; one addressed by IP publishes <see cref="HostedAddress"/>, or its bind address when that is empty.
    /// Sessions need Nucleus Pro. This compiles in both editions, but with a Free <c>Nucleus.dll</c>
    /// <see cref="Nucleus.Managers.Server.ServerManager.IsHostMigrationAvailable"/> reads <see langword="false"/> and every session call
    /// returns <see langword="false"/>.
    /// </remarks>
    [RequireComponent(typeof(SteamTransport))]
    [NucleusHelpUrl(typeof(SteamHostMigration))]
    public class SteamHostMigration : NetworkHostMigration
    {
        /// <summary>
        /// The address other peers reach this peer at when it hosts by IP address.
        /// </summary>
        /// <remarks>Read when the transport is added. Has no effect when peers are addressed by Steam ID.</remarks>
        public string HostedAddress { get => _hostedAddress; set => _hostedAddress = value; }

        /// <summary>
        /// The address other peers reach this peer at when it hosts by IP address.
        /// </summary>
        [SerializeField]
        [Tooltip("Address other peers reach this peer at when it hosts by IP address, such as a public address with a forwarded port. Leave empty to use the bind address, or this machine's own IPv4 address, which only reaches the same network.")]
        private string _hostedAddress = string.Empty;

        /// <inheritdoc/>
        protected override ISessionHost CreateSessionHost(Transport transport)
        {
            if (transport is SteamNetworkingTransport steamNetworkingTransport)
                return new SteamSessionHost(steamNetworkingTransport) { HostedAddress = _hostedAddress };

            Debug.LogError($"[{nameof(SteamHostMigration)}] on [{name}] needs a {nameof(SteamTransport)} beside it and found [{transport.GetType().Name}], so host migration is off.");

            return null;
        }
    }
}
