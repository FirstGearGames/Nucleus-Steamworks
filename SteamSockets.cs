using System;
using System.Collections.Generic;
using Nucleus.Transports;
using Steamworks;

namespace Nucleus.Integrations.Unity.Transports
{
    /// <summary>
    /// Routes each Steam Networking Sockets call to the client interface or to the game server interface.
    /// </summary>
    /// <remarks>
    /// Steamworks.NET exposes the two interfaces as separate static classes with identical members, and a handle opened through one is
    /// only valid on that one. Every caller here says which interface its handle belongs to, so a listen socket opened as a game server is
    /// never driven through the client interface or the other way round.
    /// </remarks>
    internal static class SteamSockets
    {
        /// <summary>
        /// Returns whether the chosen interface is unavailable, never initialised or already shut down.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <returns>True when no call may be made on it, as Steamworks.NET throws on every one.</returns>
        /// <remarks>
        /// The game owns Steam, and Unity does not order one object's teardown against another's, so Steam can shut down while a socket is
        /// still open. Its handles went with it, so a socket only forgets them then.
        /// </remarks>
        internal static bool IsShutDown(bool isGameServer)
        {
            try
            {
                if (isGameServer)
                    InteropHelp.TestIfAvailableGameServer();
                else
                    InteropHelp.TestIfAvailableClient();

                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }

        /// <summary>
        /// Returns the Steam ID of whichever account the chosen interface signed in as.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <returns>The Steam ID.</returns>
        internal static ulong GetSteamId(bool isGameServer) => isGameServer ? SteamGameServer.GetSteamID().m_SteamID : SteamUser.GetSteamID().m_SteamID;

        /// <summary>
        /// Starts Steam fetching its network config and certificate on the chosen interface, ahead of the first link.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <remarks>Steam does this on its own at the first link otherwise; a call once it has started changes nothing.</remarks>
        internal static void InitRelayNetworkAccess(bool isGameServer)
        {
            if (isGameServer)
                SteamGameServerNetworkingUtils.InitRelayNetworkAccess();
            else
                SteamNetworkingUtils.InitRelayNetworkAccess();
        }

        /// <summary>
        /// Returns whether the player's interface has settled its network config, having fetched it or given up on it.
        /// </summary>
        /// <returns>True once a dial would no longer fail for want of Steam's certificate.</returns>
        /// <remarks>
        /// A dial made straight after Steam starts, before the config has arrived, closes with <c>k_ESteamNetConnectionEnd_Local_BadCert</c>
        /// rather than waiting for it. A config Steam failed to fetch also counts as settled, so the dial goes ahead and fails on its own.
        /// </remarks>
        internal static bool IsNetworkConfigSettled()
        {
            SteamNetworkingUtils.GetRelayNetworkStatus(out SteamRelayNetworkStatus_t relayNetworkStatus);

            return relayNetworkStatus.m_eAvailNetworkConfig is ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current or ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Previously or ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Failed or ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_CannotTry;
        }

        /// <summary>
        /// Opens a listen socket that peers reach by this account's Steam identity.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="options">The settings every connection the socket accepts is opened with, or <see langword="null"/> for Steam's own.</param>
        internal static HSteamListenSocket CreateListenSocketP2P(bool isGameServer, SteamNetworkingConfigValue_t[] options) => isGameServer ? SteamGameServerNetworkingSockets.CreateListenSocketP2P(0, GetOptionCount(options), options) : SteamNetworkingSockets.CreateListenSocketP2P(0, GetOptionCount(options), options);

        /// <summary>
        /// Opens a listen socket that peers reach by IP address and port.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="localAddress">The address and port to listen on; an all-zero address listens on every interface.</param>
        /// <param name="options">The settings every connection the socket accepts is opened with, or <see langword="null"/> for Steam's own.</param>
        internal static HSteamListenSocket CreateListenSocketIP(bool isGameServer, ref SteamNetworkingIPAddr localAddress, SteamNetworkingConfigValue_t[] options) => isGameServer ? SteamGameServerNetworkingSockets.CreateListenSocketIP(ref localAddress, GetOptionCount(options), options) : SteamNetworkingSockets.CreateListenSocketIP(ref localAddress, GetOptionCount(options), options);

        /// <summary>
        /// Builds the settings a socket's connections are opened with: Steam's send rate held at the transport's, its timeouts, and its
        /// relay only setting.
        /// </summary>
        /// <param name="steamNetworkingTransport">The transport whose settings are read.</param>
        /// <returns>The settings.</returns>
        /// <remarks>
        /// The rate is set as both Steam's lowest and highest, because Steam sends at its lowest unless something raises it, and nothing
        /// does, so a higher ceiling alone changes nothing. A timeout of zero, and a rate of zero, keep Steam's own. Built once per socket,
        /// when it opens.
        /// </remarks>
        internal static SteamNetworkingConfigValue_t[] CreateConnectionOptions(SteamNetworkingTransport steamNetworkingTransport)
        {
            Configuration configuration = steamNetworkingTransport.Configuration;
            uint sendRateKilobytesPerSecond = steamNetworkingTransport.SendRateKilobytesPerSecond;

            List<SteamNetworkingConfigValue_t> options = new();

            if (sendRateKilobytesPerSecond > 0)
            {
                int sendRateBytesPerSecond = (int)Math.Min(sendRateKilobytesPerSecond * 1_024UL, int.MaxValue);

                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, sendRateBytesPerSecond));
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, sendRateBytesPerSecond));
            }

            if (configuration.ConnectingTimeoutSeconds > 0)
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutInitial, configuration.ConnectingTimeoutSeconds * 1_000));

            if (configuration.ConnectedTimeoutSeconds > 0)
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected, configuration.ConnectedTimeoutSeconds * 1_000));

            // Only a Steam ID link tries a direct route; an IP address is dialled as given.
            if (steamNetworkingTransport.IsRelayOnlyEnabled && steamNetworkingTransport.Addressing is SteamAddressing.SteamId)
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable, Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable));

            return options.ToArray();
        }

        /// <summary>
        /// Reads an IP address, with or without a port, into Steam's form.
        /// </summary>
        /// <param name="text">The address, such as <c>203.0.113.7</c>, <c>203.0.113.7:7777</c> or <c>[::1]:7777</c>. Empty means every interface, and <c>localhost</c> the loopback address.</param>
        /// <param name="defaultPort">The port used when <paramref name="text"/> carries none.</param>
        /// <param name="ipAddress">The address read.</param>
        /// <returns>True when <paramref name="text"/> was an address. Host names are not looked up.</returns>
        internal static bool TryParseIpAddress(string text, ushort defaultPort, out SteamNetworkingIPAddr ipAddress)
        {
            ipAddress = new();
            ipAddress.Clear();

            if (string.Equals(text, "localhost", StringComparison.OrdinalIgnoreCase))
                ipAddress.SetIPv6LocalHost();
            else if (!string.IsNullOrEmpty(text) && !ipAddress.ParseString(text))
                return false;

            if (ipAddress.m_port == 0)
                ipAddress.m_port = defaultPort;

            return true;
        }

        /// <summary>
        /// Closes a listen socket.
        /// </summary>
        internal static void CloseListenSocket(bool isGameServer, HSteamListenSocket listenSocket)
        {
            if (isGameServer)
                SteamGameServerNetworkingSockets.CloseListenSocket(listenSocket);
            else
                SteamNetworkingSockets.CloseListenSocket(listenSocket);
        }

        /// <summary>
        /// Creates a poll group, which lets every accepted connection be read with one call.
        /// </summary>
        internal static HSteamNetPollGroup CreatePollGroup(bool isGameServer) => isGameServer ? SteamGameServerNetworkingSockets.CreatePollGroup() : SteamNetworkingSockets.CreatePollGroup();

        /// <summary>
        /// Destroys a poll group.
        /// </summary>
        internal static void DestroyPollGroup(bool isGameServer, HSteamNetPollGroup pollGroup)
        {
            if (isGameServer)
                SteamGameServerNetworkingSockets.DestroyPollGroup(pollGroup);
            else
                SteamNetworkingSockets.DestroyPollGroup(pollGroup);
        }

        /// <summary>
        /// Moves a connection into a poll group, bringing any messages it has already received with it.
        /// </summary>
        internal static void SetConnectionPollGroup(bool isGameServer, HSteamNetConnection connection, HSteamNetPollGroup pollGroup)
        {
            if (isGameServer)
                SteamGameServerNetworkingSockets.SetConnectionPollGroup(connection, pollGroup);
            else
                SteamNetworkingSockets.SetConnectionPollGroup(connection, pollGroup);
        }

        /// <summary>
        /// Accepts a connection a peer is making to a listen socket.
        /// </summary>
        internal static EResult AcceptConnection(bool isGameServer, HSteamNetConnection connection) => isGameServer ? SteamGameServerNetworkingSockets.AcceptConnection(connection) : SteamNetworkingSockets.AcceptConnection(connection);

        /// <summary>
        /// Closes a connection and frees its handle.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="connection">The connection to close.</param>
        /// <param name="isLingerEnabled">True to keep sending reliable data already queued before the connection closes.</param>
        internal static void CloseConnection(bool isGameServer, HSteamNetConnection connection, bool isLingerEnabled)
        {
            if (isGameServer)
                SteamGameServerNetworkingSockets.CloseConnection(connection, 0, null, isLingerEnabled);
            else
                SteamNetworkingSockets.CloseConnection(connection, 0, null, isLingerEnabled);
        }

        /// <summary>
        /// Opens a connection to a peer by its Steam identity.
        /// </summary>
        /// <param name="remoteSteamId">The peer's Steam ID.</param>
        /// <param name="options">The settings the connection is opened with, or <see langword="null"/> for Steam's own.</param>
        internal static HSteamNetConnection ConnectP2P(ulong remoteSteamId, SteamNetworkingConfigValue_t[] options)
        {
            SteamNetworkingIdentity remoteIdentity = new();
            remoteIdentity.SetSteamID64(remoteSteamId);

            return SteamNetworkingSockets.ConnectP2P(ref remoteIdentity, 0, GetOptionCount(options), options);
        }

        /// <summary>
        /// Opens a connection to a peer by IP address and port.
        /// </summary>
        /// <param name="remoteAddress">The peer's address and port.</param>
        /// <param name="options">The settings the connection is opened with, or <see langword="null"/> for Steam's own.</param>
        internal static HSteamNetConnection ConnectByIPAddress(ref SteamNetworkingIPAddr remoteAddress, SteamNetworkingConfigValue_t[] options) => SteamNetworkingSockets.ConnectByIPAddress(ref remoteAddress, GetOptionCount(options), options);

        /// <summary>
        /// Sends a message on a connection.
        /// </summary>
        /// <returns><c>k_EResultOK</c> when Steam accepted the message, otherwise Steam's reason for refusing it.</returns>
        internal static EResult SendMessageToConnection(bool isGameServer, HSteamNetConnection connection, IntPtr data, uint length, int sendFlags) => isGameServer
            ? SteamGameServerNetworkingSockets.SendMessageToConnection(connection, data, length, sendFlags, out _)
            : SteamNetworkingSockets.SendMessageToConnection(connection, data, length, sendFlags, out _);

        /// <summary>
        /// Takes up to <paramref name="messages"/>.Length waiting messages from every connection in a poll group.
        /// </summary>
        internal static int ReceiveMessagesOnPollGroup(bool isGameServer, HSteamNetPollGroup pollGroup, IntPtr[] messages) => isGameServer ? SteamGameServerNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, messages, messages.Length) : SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, messages, messages.Length);

        /// <summary>
        /// Takes up to <paramref name="messages"/>.Length waiting messages from one connection.
        /// </summary>
        internal static int ReceiveMessagesOnConnection(bool isGameServer, HSteamNetConnection connection, IntPtr[] messages) => isGameServer ? SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection(connection, messages, messages.Length) : SteamNetworkingSockets.ReceiveMessagesOnConnection(connection, messages, messages.Length);

        /// <summary>
        /// Creates the status callback for the chosen interface, which Steam raises from inside its own callback pump.
        /// </summary>
        internal static Callback<SteamNetConnectionStatusChangedCallback_t> CreateStatusCallback(bool isGameServer, Callback<SteamNetConnectionStatusChangedCallback_t>.DispatchDelegate handler) => isGameServer ? Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(handler) : Callback<SteamNetConnectionStatusChangedCallback_t>.Create(handler);

        /// <summary>
        /// Returns how many settings a list holds, zero for none.
        /// </summary>
        private static int GetOptionCount(SteamNetworkingConfigValue_t[] options) => options?.Length ?? 0;

        /// <summary>
        /// Builds one whole-number setting.
        /// </summary>
        private static SteamNetworkingConfigValue_t CreateInt32Option(ESteamNetworkingConfigValue configValue, int value)
        {
            SteamNetworkingConfigValue_t option = new()
            {
                m_eValue = configValue,
                m_eDataType = ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
            };
            option.m_val.m_int32 = value;

            return option;
        }
    }
}
