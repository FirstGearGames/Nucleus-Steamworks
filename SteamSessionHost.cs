// Matches Steamworks.NET's own switch, so this compiles out wherever Steamworks.NET does, not only where it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using CodeBoost.Logging;
using Newfarm.Wire;
using Nucleus.Connections;
using Nucleus.Integrations.Newfarm;
using Nucleus.Transports;
using Steamworks;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// Carries a session over Steam Networking Sockets on behalf of a directory, which is the whole of what this transport contributes to
    /// a handover.
    /// </summary>
    /// <remarks>
    /// Plain .NET over Steamworks.NET with nothing from a game engine, so it works anywhere a <see cref="SteamNetworkingTransport"/> does.
    /// Noticing the host go, being elected, adopting the world and finding where the session moved to all belong to
    /// <see cref="NewfarmHostMigration"/>. This is only the part that is Steam: listening, naming where, connecting to somebody else, and
    /// leaving.
    /// The credential follows <see cref="SteamNetworkingTransport.Addressing"/>. By Steam ID it is the hosting account's Steam ID, so
    /// nobody needs an address. By IP it is an address and port, which is <see cref="HostedAddress"/> when set and otherwise this
    /// machine's own IPv4 address, which only reaches peers on the same network. By fake IP it is the address Steam grants the new host.
    /// Steam IDs are filed under one tag and addresses, real or fake, under another, so a peer never tries to read a Steam ID as an
    /// address; a fake IP address is dialled exactly as a real one is.
    /// </remarks>
    /// <seealso cref="SteamNetworkingTransport"/>
    public sealed class SteamSessionHost : ISessionHost
    {
        /// <inheritdoc/>
        public string AdapterTag => _steamNetworkingTransport.Addressing is SteamAddressing.SteamId ? SteamIdAdapterTag : IpAddressAdapterTag;

        /// <inheritdoc/>
        /// <remarks>
        /// A server started some other way than <see cref="StartHostingAsync"/>, which is how a session opens on its own, is named the
        /// first time this is read while it is up, and forgotten once it is down.
        /// </remarks>
        public string HostedCredential
        {
            get
            {
                if (!IsHostingLinkUp)
                {
                    _hostedCredential = string.Empty;

                    return string.Empty;
                }

                if (_hostedCredential.Length == 0 && TryGetHostedCredential(out string hostedCredential))
                    _hostedCredential = hostedCredential;

                return _hostedCredential;
            }
        }

        /// <inheritdoc/>
        public bool IsHostingLinkUp => IsLinkUp(Invoker.Server);

        /// <inheritdoc/>
        public bool IsJoinedLinkUp => IsLinkUp(Invoker.Client);

        /// <summary>
        /// The address other peers reach this peer's server at when peers are addressed by IP, such as a public address with a forwarded
        /// port. Empty uses the bind address when one is set, and otherwise this machine's own IPv4 address.
        /// </summary>
        /// <remarks>
        /// Read when hosting starts. The configured port is added when this names none. The address and port together may run to
        /// <see cref="NewfarmWireFormat.MaximumTextLength"/> characters, which any IPv4 address fits and a full IPv6 one may not.
        /// </remarks>
        public string HostedAddress { get; set; } = string.Empty;

        /// <summary>
        /// The tag a session addressed by Steam ID is filed under with a directory.
        /// </summary>
        public const string SteamIdAdapterTag = "steam-id";

        /// <summary>
        /// The tag a session addressed by IP address or fake IP address is filed under with a directory.
        /// </summary>
        public const string IpAddressAdapterTag = "steam-ip";

        /// <summary>
        /// The transport this stands a session up on.
        /// </summary>
        private readonly SteamNetworkingTransport _steamNetworkingTransport;

        /// <summary>
        /// Where the session this peer is hosting can be reached, set when hosting starts or when first asked for.
        /// </summary>
        private string _hostedCredential = string.Empty;

        /// <summary>
        /// How often hosting by fake IP checks whether Steam has granted the address.
        /// </summary>
        private const int FakeIpPollIntervalMilliseconds = 5;

        /// <summary>
        /// Creates an adapter over a Steam transport.
        /// </summary>
        /// <param name="steamNetworkingTransport">The transport carrying the session.</param>
        public SteamSessionHost(SteamNetworkingTransport steamNetworkingTransport)
        {
            _steamNetworkingTransport = steamNetworkingTransport;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Empty. Both sockets are driven by the engine's own loop, and a connect waits on Steam's callbacks, which the game already pumps.
        /// </remarks>
        public void Poll() { }

        /// <inheritdoc/>
        /// <remarks>
        /// Opening a listen socket is immediate: Steam either opens one or refuses on the spot. Under fake IP the budget is spent waiting
        /// for Steam to grant the address, which it does only once the server is listening; a grant that does not arrive in time stops the
        /// server again.
        /// </remarks>
        public async Task<SessionHostResult> StartHostingAsync(uint timeoutMilliseconds)
        {
            bool isFakeIp = _steamNetworkingTransport.Addressing is SteamAddressing.FakeIp;
            string hostedCredential = string.Empty;

            // Named before the server starts, so a peer that cannot say where it is never stands up a session nobody can reach.
            if (!isFakeIp && !TryGetHostedCredential(out hostedCredential))
                return SessionHostResult.LocalFailure;

            if (await _steamNetworkingTransport.ConnectAsync(Invoker.Server) is not ConnectionStateChangeResult.Success)
                return SessionHostResult.LocalFailure;

            if (isFakeIp)
            {
                hostedCredential = await AwaitServerFakeAddressAsync(timeoutMilliseconds);

                if (hostedCredential.Length == 0)
                {
                    Logger<SteamSessionHost>.LogError($"Steam granted this peer no fake IP address within [{timeoutMilliseconds}] ms, so it cannot host the session.");

                    await _steamNetworkingTransport.DisconnectLocalConnectionAsync(Invoker.Server);

                    return SessionHostResult.LocalFailure;
                }
            }

            _hostedCredential = hostedCredential;

            return SessionHostResult.Success;
        }

        /// <inheritdoc/>
        public async Task<SessionHostResult> JoinAsync(string credential, uint timeoutMilliseconds)
        {
            if (string.IsNullOrEmpty(credential))
            {
                Logger<SteamSessionHost>.LogError("A join was asked for with nowhere to join, which is this peer's own fault rather than the host's.");

                return SessionHostResult.LocalFailure;
            }

            // Whatever this peer was in is left before another is tried, so a retry never builds on the attempt before it.
            await LeaveAsync();

            if (_steamNetworkingTransport.Addressing is not SteamAddressing.SteamId)
                _steamNetworkingTransport.RemoteAddress = credential;
            else if (ulong.TryParse(credential, out ulong remoteSteamId))
                _steamNetworkingTransport.RemoteSteamId = remoteSteamId;
            else
            {
                Logger<SteamSessionHost>.LogError($"[{credential}] is not a Steam ID. Every peer in a session must address by the same means.");

                return SessionHostResult.LocalFailure;
            }

            /* The client reads its timeout before its first wait, so the budget is lent only for the start of the call and the transport's
             * own is back before anything else could read it. */
            uint configuredTimeoutMilliseconds = _steamNetworkingTransport.ConnectTimeoutMilliseconds;

            _steamNetworkingTransport.ConnectTimeoutMilliseconds = configuredTimeoutMilliseconds == 0 || timeoutMilliseconds < configuredTimeoutMilliseconds ? timeoutMilliseconds : configuredTimeoutMilliseconds;

            Task<ConnectionStateChangeResult> connectTask = _steamNetworkingTransport.ConnectAsync(Invoker.Client);

            _steamNetworkingTransport.ConnectTimeoutMilliseconds = configuredTimeoutMilliseconds;

            if (await connectTask is ConnectionStateChangeResult.Success)
                return SessionHostResult.Success;

            /* Steam carrying the far end's refusal back is the one thing a directory should hear: the host is not there, or will not have
             * this peer. A host that never answered is left to the directory, which stops hearing its heartbeat anyway, so a peer whose own
             * line dropped never has a healthy host challenged. */
            return _steamNetworkingTransport.IsLastConnectRefused ? SessionHostResult.CredentialUnreachable : SessionHostResult.LocalFailure;
        }

        /// <inheritdoc/>
        public async Task StopHostingAsync()
        {
            _hostedCredential = string.Empty;

            if (!_steamNetworkingTransport.TryGetConnection(Invoker.Server, out Connection _))
                return;

            await _steamNetworkingTransport.DisconnectLocalConnectionAsync(Invoker.Server);
        }

        /// <inheritdoc/>
        public async Task LeaveAsync()
        {
            if (!_steamNetworkingTransport.TryGetConnection(Invoker.Client, out Connection _))
                return;

            await _steamNetworkingTransport.DisconnectLocalConnectionAsync(Invoker.Client);
        }

        /// <summary>
        /// Waits for Steam to grant this peer's listening server its fake IP address.
        /// </summary>
        /// <param name="timeoutMilliseconds">The longest to wait.</param>
        /// <returns>The address, or empty when none arrived in time or Steam shut down.</returns>
        /// <remarks>The grant arrives through Steam's callback pump on the main thread, which runs between the waits.</remarks>
        private async Task<string> AwaitServerFakeAddressAsync(uint timeoutMilliseconds)
        {
            long deadlineTimestamp = Stopwatch.GetTimestamp() + timeoutMilliseconds * Stopwatch.Frequency / 1000;

            while (_steamNetworkingTransport.ServerFakeAddress is null && Stopwatch.GetTimestamp() < deadlineTimestamp && !SteamSockets.IsShutDown(_steamNetworkingTransport.IsGameServer))
                await Task.Delay(FakeIpPollIntervalMilliseconds);

            return _steamNetworkingTransport.ServerFakeAddress ?? string.Empty;
        }

        /// <summary>
        /// Names where another peer reaches this one's server once it is listening.
        /// </summary>
        /// <param name="hostedCredential">This peer's Steam ID, its fake IP address, or the address and port it listens on.</param>
        /// <returns><see langword="true"/> when there is something another peer could be pointed at.</returns>
        private bool TryGetHostedCredential(out string hostedCredential)
        {
            switch (_steamNetworkingTransport.Addressing)
            {
                case SteamAddressing.SteamId:
                    hostedCredential = SteamSockets.GetSteamId(_steamNetworkingTransport.IsGameServer).ToString();

                    return true;
                case SteamAddressing.FakeIp:
                    hostedCredential = _steamNetworkingTransport.ServerFakeAddress ?? string.Empty;

                    return hostedCredential.Length > 0;
            }

            string address = HostedAddress;

            if (string.IsNullOrEmpty(address))
                address = _steamNetworkingTransport.Configuration.BindAddress;

            if (string.IsNullOrEmpty(address) || address == IPAddress.Any.ToString())
                address = GetOwnIPv4Address();

            if (string.IsNullOrEmpty(address))
            {
                Logger<SteamSessionHost>.LogError($"This machine has no IPv4 address to give other peers. Set [{nameof(HostedAddress)}] to the address they reach this peer at.");

                hostedCredential = string.Empty;

                return false;
            }

            // An address that already carries a port is kept as it is; the client adds the configured one only when it names none.
            hostedCredential = SteamSockets.TryParseIpAddress(address, defaultPort: 0, out SteamNetworkingIPAddr ipAddress) && ipAddress.m_port != 0
                ? address
                : $"{address}:{_steamNetworkingTransport.Configuration.Port}";

            // The directory carries a credential of limited length, and a full IPv6 address with its port can run past it.
            if (hostedCredential.Length <= NewfarmWireFormat.MaximumTextLength)
                return true;

            Logger<SteamSessionHost>.LogError($"[{hostedCredential}] runs to [{hostedCredential.Length}] characters and a session directory carries at most [{NewfarmWireFormat.MaximumTextLength}]. Set [{nameof(HostedAddress)}] to a shorter address, such as an IPv4 one.");

            hostedCredential = string.Empty;

            return false;
        }

        /// <summary>
        /// Returns whether one side of the transport is connected.
        /// </summary>
        /// <param name="invoker">Which side to ask about.</param>
        /// <returns><see langword="true"/> when that side is connected.</returns>
        private bool IsLinkUp(Invoker invoker) => _steamNetworkingTransport.TryGetConnection(invoker, out Connection connection) && connection.LocalState is LocalConnectionState.Connected;

        /// <summary>
        /// Returns this machine's first IPv4 address that is not loopback, or an empty string when it has none.
        /// </summary>
        /// <returns>The address.</returns>
        /// <remarks>Reaches peers on the same network only. A peer behind a router hosting players outside it sets <see cref="HostedAddress"/>.</remarks>
        private static string GetOwnIPv4Address()
        {
            try
            {
                foreach (IPAddress ipAddress in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ipAddress.AddressFamily is AddressFamily.InterNetwork && !IPAddress.IsLoopback(ipAddress))
                        return ipAddress.ToString();
                }
            }
            catch (SocketException socketException)
            {
                Logger<SteamSessionHost>.LogWarning($"Could not read this machine's addresses: [{socketException.Message}].");
            }

            return string.Empty;
        }
    }
}
#endif
