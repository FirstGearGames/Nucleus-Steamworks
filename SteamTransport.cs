using System.Threading.Tasks;
using UnityEngine;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Core;
using Nucleus.Transports;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// Inspector-configurable component for the Steam Networking Sockets transport, which reaches peers by Steam ID through Valve's relay
    /// network, or by IP address and port. Place it on the UnityCoreManager's GameObject; the <see cref="UnityTransportManager"/> adds it
    /// to the CoreManager with these settings applied.
    /// </summary>
    /// <remarks>
    /// Compiled only when Steamworks.NET is installed as a package, which defines <c>NUCLEUS_STEAMWORKS</c>. Add that symbol yourself if
    /// Steamworks.NET came in another way. Steam must already be initialised, and its callbacks run every frame, by something else in the
    /// game, such as Heathen's Foundation for Steamworks or a <c>SteamManager</c>.
    /// By Steam ID there is no port and no address: a host starts its server and hands its Steam ID to the players joining it. Either way,
    /// a host's own client needs nothing at all.
    /// </remarks>
    [DisallowMultipleComponent]
    [NucleusHelpUrl(typeof(SteamTransport))]
    public partial class SteamTransport : NetworkTransport
    {
        /// <summary>
        /// The Steam ID a client connects to when peers are addressed by Steam ID: the hosting player's ID, or a game server's ID.
        /// </summary>
        /// <remarks>
        /// Accepted at any time and applied at the next client start, including to the transport an earlier start built. Zero, or this
        /// peer's own ID, connects a host's client to its own server.
        /// </remarks>
        public ulong RemoteSteamId
        {
            get => _remoteSteamId;
            set
            {
                _remoteSteamId = value;

                if (_steamNetworkingTransport is not null)
                    _steamNetworkingTransport.RemoteSteamId = value;
            }
        }

        /// <summary>
        /// The IP address a client connects to when peers are addressed by IP, with an optional port such as <c>203.0.113.7:7777</c>.
        /// </summary>
        /// <remarks>
        /// Accepted at any time and applied at the next client start, including to the transport an earlier start built. Empty, or a
        /// loopback address, connects a host's client to its own server.
        /// </remarks>
        public string RemoteAddress
        {
            get => _remoteAddress;
            set
            {
                _remoteAddress = value;

                if (_steamNetworkingTransport is not null)
                    _steamNetworkingTransport.RemoteAddress = value;
            }
        }

        /// <summary>
        /// How peers find each other.
        /// </summary>
        [SerializeField]
        [Tooltip("Steam ID connects through Valve's relay network with nothing to forward. IP Address listens on a port, as a dedicated server usually does.")]
        private SteamAddressing _addressing = SteamAddressing.SteamId;
        /// <summary>
        /// The Steam ID a client connects to.
        /// </summary>
        [SerializeField]
        [Tooltip("Steam ID a client connects to. Leave at 0 on the host, whose own client joins its server inside the process.")]
        private ulong _remoteSteamId;
        /// <summary>
        /// The IP address a client connects to.
        /// </summary>
        [SerializeField]
        [Tooltip("IP address a client connects to, optionally with a port such as 203.0.113.7:7777. Leave empty on the host, whose own client joins its server inside the process. Host names are not looked up.")]
        private string _remoteAddress = string.Empty;
        /// <summary>
        /// The port the server listens on, and the port a client connects to when its address names none.
        /// </summary>
        [SerializeField]
        [Tooltip("Port the server listens on, and the port a client connects to when its address names none.")]
        private ushort _port = 7777;
        /// <summary>
        /// The local address the server listens on.
        /// </summary>
        [SerializeField]
        [Tooltip("Local address the server listens on, when addressing by IP. Leave empty to listen on every interface.")]
        private string _bindAddress = string.Empty;
        /// <summary>
        /// Whether the server signs in through Steam's game server interface.
        /// </summary>
        [SerializeField]
        [Tooltip("Serve through the Steam game server interface. Set for a dedicated server; leave off when a player hosts.")]
        private bool _isGameServer;
        /// <summary>
        /// The most clients the server accepts at once.
        /// </summary>
        [SerializeField]
        [Tooltip("Most clients the server accepts at once. 0 accepts any number.")]
        private uint _maximumConnections;
        /// <summary>
        /// The largest datagram to build before it is split.
        /// </summary>
        [SerializeField]
        [Tooltip("Largest datagram to build before it is split.")]
        private ushort _maximumTransmissionUnit = 1200;
        /// <summary>
        /// The rate Steam sends at on each connection, in kilobytes a second.
        /// </summary>
        [SerializeField]
        [Tooltip("Rate Steam sends at on each connection, in kilobytes a second. Steam's own is 256, slower than nearly any player's line. 0 keeps Steam's own.")]
        private uint _sendRateKilobytesPerSecond = SteamNetworkingTransport.DefaultSendRateKilobytesPerSecond;

        /// <summary>
        /// The transport this component configured, or <see langword="null"/> before it has.
        /// </summary>
        private SteamNetworkingTransport _steamNetworkingTransport;

        /// <inheritdoc/>
        public override async Task<Transport> AddToAsync(CoreManager coreManager)
        {
            _steamNetworkingTransport = (SteamNetworkingTransport)await coreManager.TransportManager.AddTransportAsync<SteamNetworkingTransport>();

            _steamNetworkingTransport.Addressing = _addressing;
            _steamNetworkingTransport.RemoteSteamId = _remoteSteamId;
            _steamNetworkingTransport.RemoteAddress = _remoteAddress;
            _steamNetworkingTransport.IsGameServer = _isGameServer;
            _steamNetworkingTransport.SetMaximumConnections(_maximumConnections);
            _steamNetworkingTransport.Configuration.Port = _port;
            _steamNetworkingTransport.SetBindAddress(_bindAddress);
            _steamNetworkingTransport.Configuration.MaximumTransmissionUnit = _maximumTransmissionUnit;
            _steamNetworkingTransport.SendRateKilobytesPerSecond = _sendRateKilobytesPerSecond;

            await EnableHostMigrationAsync(coreManager);

            return _steamNetworkingTransport;
        }
    }
}
