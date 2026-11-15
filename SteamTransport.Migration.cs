using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nucleus.Integrations.Newfarm;
using Nucleus.Managers.Client;
using Nucleus.Managers.Core;
using UnityEngine;

namespace Nucleus.Integrations.Unity.Transports
{
    public partial class SteamTransport
    {
        /// <summary>
        /// Whether the session registers with a directory so it survives losing whoever is hosting it.
        /// </summary>
        /// <remarks>
        /// Read when the transport is added. With it set, a game starts and joins as it would without it: a server opens a session with
        /// the directory once it is running, and every client that authenticates is told which session it is in, so a peer that joined
        /// by Steam ID or address survives losing that host. <see cref="StartHostingSessionAsync"/> and <see cref="JoinSessionAsync"/>
        /// remain for driving the directory directly. A server that cannot open its session raises
        /// <see cref="NewfarmHostMigration.SessionOpenFailed"/> on <see cref="Migration"/>.
        /// Sessions need Nucleus Pro. This compiles in both editions, but with a Free <c>Nucleus.dll</c>
        /// <see cref="Nucleus.Managers.Server.ServerManager.IsHostMigrationAvailable"/> reads <see langword="false"/> and every session call
        /// returns <see langword="false"/>.
        /// </remarks>
        public bool IsHostMigrationEnabled { get => _isHostMigrationEnabled; set => _isHostMigrationEnabled = value; }

        /// <summary>
        /// Keeps the session alive across the loss of its host, or <see langword="null"/> when <see cref="IsHostMigrationEnabled"/> is off
        /// or the transport has not been added yet.
        /// </summary>
        /// <remarks>
        /// Polled from this component's <c>Update</c>, so a game needs only to start hosting or to join through it. Nothing about it is
        /// Steam: it drives a directory, and reaches Steam through a <see cref="SteamSessionHost"/>.
        /// </remarks>
        public NewfarmHostMigration Migration { get; private set; }

        /// <summary>
        /// Names the session with the directory, which is what a host hands to its clients and the only thing a peer needs to find its way
        /// back after a handover.
        /// </summary>
        public ulong SessionId => Migration is null ? NewfarmHostMigration.UnsetSessionId : Migration.SessionId;

        /// <summary>
        /// Whether the session registers with a directory so it survives losing whoever is hosting it.
        /// </summary>
        [SerializeField]
        [Tooltip("Register the session with a newfarm directory so it survives losing its host.")]
        private bool _isHostMigrationEnabled;
        /// <summary>
        /// The IP address or host name of the directory the session registers with.
        /// </summary>
        [SerializeField]
        [Tooltip("IP address or host name of the newfarm directory the session registers with. A host name is looked up through DNS when the transport is added.")]
        private string _directoryAddress = "127.0.0.1";
        /// <summary>
        /// The port the directory listens on.
        /// </summary>
        [SerializeField]
        [Tooltip("Port the newfarm directory listens on.")]
        private ushort _directoryPort = 47778;
        /// <summary>
        /// The address other peers reach this peer at when it hosts by IP address.
        /// </summary>
        [SerializeField]
        [Tooltip("Address other peers reach this peer at when it hosts by IP address, such as a public address with a forwarded port. Leave empty to use the bind address, or this machine's own IPv4 address, which only reaches the same network.")]
        private string _hostedAddress = string.Empty;

        /// <summary>
        /// Opens a session, hosts it, and registers where it can be reached.
        /// </summary>
        /// <returns><see langword="true"/> when this peer is hosting the session.</returns>
        /// <remarks>Requires <see cref="IsHostMigrationEnabled"/>; without a directory there is no session to open.</remarks>
        public async Task<bool> StartHostingSessionAsync()
        {
            if (Migration is null)
            {
                Debug.LogError($"[{nameof(SteamTransport)}] Host migration is off, so there is no session to host. Start the server through the CoreManager instead.");

                return false;
            }

            return await Migration.StartHostingAsync();
        }

        /// <summary>
        /// Finds a session by its identifier and joins whoever currently hosts it.
        /// </summary>
        /// <param name="sessionId">The session, as the host distributed it.</param>
        /// <returns><see langword="true"/> when this peer is in the session.</returns>
        public async Task<bool> JoinSessionAsync(ulong sessionId)
        {
            if (Migration is null)
            {
                Debug.LogError($"[{nameof(SteamTransport)}] Host migration is off, so a session cannot be joined. Set a Steam ID or address and connect a client instead.");

                return false;
            }

            return await Migration.JoinAsync(sessionId);
        }

        /// <summary>
        /// Gives the session up to whoever the directory elects next, without leaving it and without stopping play.
        /// </summary>
        /// <returns><see langword="true"/> when this peer was hosting and has now given the session up.</returns>
        public bool TrySurrenderHostingSession()
        {
            if (Migration is not null)
                return Migration.TrySurrenderHosting();

            Debug.LogError($"[{nameof(SteamTransport)}] Host migration is off, so there is no session to hand on. Stop the server through the CoreManager instead.");

            return false;
        }

        /// <summary>
        /// Stands up host migration when the inspector asked for it, once the transport is added.
        /// </summary>
        /// <param name="coreManager">The CoreManager the transport belongs to.</param>
        /// <remarks>Awaited by the add, which every start awaits, so a server cannot start before its session is ready to open.</remarks>
        private async Task EnableHostMigrationAsync(CoreManager coreManager)
        {
            if (!_isHostMigrationEnabled)
                return;

            IPAddress directoryIpAddress = await ResolveDirectoryAddressAsync();

            if (directoryIpAddress is null)
                return;

            /* The world is cleared or kept at the moment the link drops, so a peer that means to carry it across a handover has to say so
             * before then. */
            coreManager.ClientManager.DisconnectResetMode = DisconnectResetMode.RetainReceivedWorld;

            SteamSessionHost steamSessionHost = new(_steamNetworkingTransport) { HostedAddress = _hostedAddress };

            Migration = new(coreManager, steamSessionHost, new IPEndPoint(directoryIpAddress, _directoryPort));
        }

        /// <summary>
        /// Reads the directory address as an IP address, or looks it up through DNS when it is a host name.
        /// </summary>
        /// <returns>The directory's IP address, or <see langword="null"/> when the lookup failed, which leaves host migration off.</returns>
        /// <remarks>The first address the lookup returns is used. The directory listens on both IPv4 and IPv6, so either family reaches it.</remarks>
        private async Task<IPAddress> ResolveDirectoryAddressAsync()
        {
            if (IPAddress.TryParse(_directoryAddress, out IPAddress directoryIpAddress))
                return directoryIpAddress;

            try
            {
                IPAddress[] directoryIpAddresses = await Dns.GetHostAddressesAsync(_directoryAddress);

                if (directoryIpAddresses.Length > 0)
                    return directoryIpAddresses[0];

                Debug.LogError($"[{nameof(SteamTransport)}] Host migration is off: the DNS lookup for directory [{_directoryAddress}] returned no addresses.");
            }
            catch (Exception exception) when (exception is SocketException or ArgumentException)
            {
                Debug.LogError($"[{nameof(SteamTransport)}] Host migration is off: directory [{_directoryAddress}] is not an IP address and its DNS lookup failed: {exception.Message}");
            }

            return null;
        }

        /// <summary>
        /// Drives the directory, which is what notices a lost host and acts on what the directory says to do about it.
        /// </summary>
        private void Update()
        {
            Migration?.Poll();
        }

        /// <summary>
        /// Releases the directory link with this component.
        /// </summary>
        private void OnDestroy()
        {
            Migration?.Dispose();
            Migration = null;
        }
    }
}
