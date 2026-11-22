// Matches Steamworks.NET's own switch, so the Steam half compiles out wherever Steamworks.NET does, not only where it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define NUCLEUS_STEAMWORKS_ENABLED
#endif
using System;
using System.Threading.Tasks;
using UnityEngine;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Managers.Core;
using Nucleus.Transports;
#if !NUCLEUS_STEAMWORKS_ENABLED
// Only the transport reads these settings, and it is compiled out here; they stay serialized so a scene keeps them for when it is not.
#pragma warning disable CS0414
#endif

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// Inspector-configurable component for the Steam Networking Sockets transport, which reaches peers by Steam ID through Valve's relay
    /// network, by IP address and port, or by a fake IP address Steam grants a server. Place it on the UnityCoreManager's GameObject; the
    /// <see cref="UnityTransportManager"/> adds it to the CoreManager with these settings applied.
    /// </summary>
    /// <remarks>
    /// The component always compiles, so a scene keeps it while Steamworks.NET is missing and its inspector can offer to install it. The
    /// transport itself compiles only when Steamworks.NET is installed as a package, which defines <c>NUCLEUS_STEAMWORKS</c>; add that
    /// symbol yourself if Steamworks.NET came in another way. Steam must already be initialised, and its callbacks run every frame, by
    /// something else in the game, such as Heathen's Foundation for Steamworks or a <c>SteamManager</c>. By Steam ID there is no port and
    /// no address: a host starts its server and hands its Steam ID to the players joining it. Either way, a host's own client needs
    /// nothing at all.
    /// </remarks>
    [DisallowMultipleComponent]
    [NucleusHelpUrl(typeof(SteamTransport))]
    public class SteamTransport : NetworkTransport
    {
        /// <summary>
        /// How peers find each other: by Steam ID through Valve's relay network, by IP address and port, or by a fake IP address.
        /// </summary>
        public SteamAddressing Addressing => _addressing;

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
#if NUCLEUS_STEAMWORKS_ENABLED

                if (_steamNetworkingTransport is not null)
                    _steamNetworkingTransport.RemoteSteamId = value;
#endif
            }
        }

        /// <summary>
        /// The address a client connects to when peers are addressed by IP or by fake IP, with an optional port such as
        /// <c>203.0.113.7:7777</c>.
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
#if NUCLEUS_STEAMWORKS_ENABLED

                if (_steamNetworkingTransport is not null)
                    _steamNetworkingTransport.RemoteAddress = value;
#endif
            }
        }
#if NUCLEUS_STEAMWORKS_ENABLED

        /// <summary>
        /// The transport this component configured, or <see langword="null"/> before it has.
        /// </summary>
        /// <remarks>Reads Steam IDs, the server's fake IP address and link status once the transport is added.</remarks>
        public SteamNetworkingTransport SteamNetworkingTransport => _steamNetworkingTransport;

        /// <summary>
        /// A check every client asking to connect passes before the engine hears of it, or <see langword="null"/> to accept anyone while the
        /// server has room.
        /// </summary>
        /// <remarks>Accepted at any time and applied to the transport this component configures.</remarks>
        public SteamNetworkingTransport.ConnectingClientFilterHandler ConnectingClientFilter
        {
            get => _connectingClientFilter;
            set
            {
                _connectingClientFilter = value;

                if (_steamNetworkingTransport is not null)
                    _steamNetworkingTransport.ConnectingClientFilter = value;
            }
        }

        /// <summary>
        /// Asked for each connecting client before it waits for a ticket, or <see langword="null"/> to send every client on to the ticket check.
        /// </summary>
        /// <remarks>Used only while Ticket Authentication is on. Accepted at any time and applied to the authenticator this component makes.</remarks>
        public SteamTicketAuthenticator.ClientAdmissionFilterHandler ClientAdmissionFilter
        {
            get => _clientAdmissionFilter;
            set
            {
                _clientAdmissionFilter = value;

                if (_steamTicketAuthenticator is not null)
                    _steamTicketAuthenticator.ClientAdmissionFilter = value;
            }
        }
#endif

        /// <summary>
        /// The message thrown from a start while Steamworks.NET is not installed.
        /// </summary>
        public const string SteamworksMissingText = "Steamworks.NET is not installed, so the Steam transport is compiled out and cannot start. Select the SteamTransport component to install it.";

        /// <summary>
        /// The message thrown from a start while Steamworks.NET is installed but compiled out, for the build target or by <c>DISABLESTEAMWORKS</c>.
        /// </summary>
        public const string SteamworksDisabledText = "Steamworks.NET is compiled out for the current build target, or by DISABLESTEAMWORKS, so the Steam transport is too and cannot start. Switch to a Windows, macOS, Linux or Android target, or remove DISABLESTEAMWORKS.";

        /// <summary>
        /// How peers find each other.
        /// </summary>
        [SerializeField]
        [Tooltip("Steam ID connects through Valve's relay network with nothing to forward. IP Address listens on a port, as a dedicated server usually does. Fake IP hides a dedicated server's real address behind one Steam grants it.")]
        private SteamAddressing _addressing = SteamAddressing.SteamId;
        /// <summary>
        /// The Steam ID a client connects to.
        /// </summary>
        [SerializeField]
        [Tooltip("Steam ID a client connects to. Leave at 0 on the host, whose own client joins its server inside the process.")]
        private ulong _remoteSteamId;
        /// <summary>
        /// The address a client connects to.
        /// </summary>
        [SerializeField]
        [Tooltip("Address a client connects to, optionally with a port such as 203.0.113.7:7777; for a fake IP, the address the server logs. Leave empty on the host, whose own client joins its server inside the process. Host names are not looked up.")]
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
        /// The most connections one Steam account may hold on the server at once.
        /// </summary>
        [SerializeField]
        [Tooltip("Most connections one Steam account may hold on the server at once, so one account cannot fill it. A connection past this is refused; none already held is closed. 0 accepts any number. The host's own client is not counted. Raise it to test several clients signed in to one Steam account.")]
        private uint _maximumConnectionsPerSteamId = DefaultMaximumConnectionsPerSteamId;
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
        private uint _sendRateKilobytesPerSecond = DefaultSendRateKilobytesPerSecond;
        /// <summary>
        /// Whether a link addressed by Steam ID or by fake IP is carried over Valve's relay network only.
        /// </summary>
        [SerializeField]
        [Tooltip("Carry Steam ID and fake IP links over Valve's relay only, so no player learns another's IP address. Off lets Steam connect players directly when their Steam settings allow it, which can lower ping for players near each other.")]
        private bool _isRelayOnlyEnabled = true;
        /// <summary>
        /// Whether a client is admitted only once Steam has confirmed its auth session ticket.
        /// </summary>
        [SerializeField]
        [Tooltip("Admit a client only once Steam confirms its ticket: it owns the game and is not banned. Server and clients both need it on; a client with it off is dropped once the authentication timeout passes.")]
        private bool _ticketAuthenticationEnabled;
        /// <summary>
        /// Whether the server tells every client the Steam ID of every other client.
        /// </summary>
        [SerializeField]
        [Tooltip("Tell every client the Steam ID of every other client, for names, avatars and friend checks. A Steam ID leads to a public Steam profile, so leave it off where players should not learn who the others are. Only the server's value counts.")]
        private bool _shareSteamIdsEnabled;
#if NUCLEUS_STEAMWORKS_ENABLED

        /// <summary>
        /// The transport this component configured, or <see langword="null"/> before it has.
        /// </summary>
        private SteamNetworkingTransport _steamNetworkingTransport;
        /// <summary>
        /// The authenticator this component made, or <see langword="null"/> while Ticket Authentication is off.
        /// </summary>
        private SteamTicketAuthenticator _steamTicketAuthenticator;
        /// <summary>
        /// The filter handed to the transport, or <see langword="null"/> for none.
        /// </summary>
        private SteamNetworkingTransport.ConnectingClientFilterHandler _connectingClientFilter;
        /// <summary>
        /// The filter handed to the ticket authenticator, or <see langword="null"/> for none.
        /// </summary>
        private SteamTicketAuthenticator.ClientAdmissionFilterHandler _clientAdmissionFilter;
#endif

        /// <summary>
        /// The send rate a new component starts at, four mebibytes a second, matching the transport's own default.
        /// </summary>
        private const uint DefaultSendRateKilobytesPerSecond = 4_096;
        /// <summary>
        /// The cap on each Steam account's connections a new component starts at, one, matching the transport's own default.
        /// </summary>
        private const uint DefaultMaximumConnectionsPerSteamId = 1;
#if NUCLEUS_STEAMWORKS_ENABLED

        /// <inheritdoc/>
        public override async Task<Transport> AddToAsync(CoreManager coreManager)
        {
            // The transport manager removes the transport at shutdown and adds it again at the next start, so the last start's authenticator goes first.
            _steamTicketAuthenticator?.Deinitialize();
            _steamTicketAuthenticator = null;

            _steamNetworkingTransport = (SteamNetworkingTransport)await coreManager.TransportManager.AddTransportAsync<SteamNetworkingTransport>();

            _steamNetworkingTransport.Addressing = _addressing;
            _steamNetworkingTransport.RemoteSteamId = _remoteSteamId;
            _steamNetworkingTransport.RemoteAddress = _remoteAddress;
            _steamNetworkingTransport.IsGameServer = _isGameServer;
            _steamNetworkingTransport.SetMaximumConnections(_maximumConnections);
            _steamNetworkingTransport.MaximumConnectionsPerSteamId = _maximumConnectionsPerSteamId;
            _steamNetworkingTransport.Configuration.Port = _port;
            _steamNetworkingTransport.SetBindAddress(_bindAddress);
            _steamNetworkingTransport.Configuration.MaximumTransmissionUnit = _maximumTransmissionUnit;
            _steamNetworkingTransport.SendRateKilobytesPerSecond = _sendRateKilobytesPerSecond;
            _steamNetworkingTransport.IsRelayOnlyEnabled = _isRelayOnlyEnabled;
            _steamNetworkingTransport.ShareSteamIdsEnabled = _shareSteamIdsEnabled;
            _steamNetworkingTransport.ConnectingClientFilter = _connectingClientFilter;

            if (_ticketAuthenticationEnabled)
            {
                _steamTicketAuthenticator = new() { ClientAdmissionFilter = _clientAdmissionFilter };
                _steamTicketAuthenticator.Initialize(coreManager, _steamNetworkingTransport);
            }

            return _steamNetworkingTransport;
        }

        /// <summary>
        /// Unregisters the ticket authenticator, if this component made one.
        /// </summary>
        private void OnDestroy()
        {
            _steamTicketAuthenticator?.Deinitialize();
            _steamTicketAuthenticator = null;
        }
#elif NUCLEUS_STEAMWORKS

        /// <inheritdoc/>
        /// <remarks>Steamworks.NET is compiled out here, so there is no transport to add.</remarks>
        public override Task<Transport> AddToAsync(CoreManager coreManager) => throw new InvalidOperationException(SteamworksDisabledText);
#else

        /// <inheritdoc/>
        /// <remarks>Steamworks.NET is not installed, so there is no transport to add.</remarks>
        public override Task<Transport> AddToAsync(CoreManager coreManager) => throw new InvalidOperationException(SteamworksMissingText);
#endif
    }
}
