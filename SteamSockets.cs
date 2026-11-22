// Matches Steamworks.NET's own switch, so this compiles out wherever Steamworks.NET does, not only where it is missing.
#if NUCLEUS_STEAMWORKS && !DISABLESTEAMWORKS && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || UNITY_ANDROID || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CodeBoost.Logging;
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
        /// The lane reliable packets are sent on.
        /// </summary>
        internal const ushort ReliableLane = 0;
        /// <summary>
        /// The lane unreliable packets are sent on, so state never waits behind a reliable burst queued in Steam.
        /// </summary>
        internal const ushort UnreliableLane = 1;
        /// <summary>
        /// How many lanes each connection is given.
        /// </summary>
        internal const int LaneCount = 2;
        /// <summary>
        /// The priority both lanes share, so neither waits for the other to empty.
        /// </summary>
        internal const int LanePriority = 0;
        /// <summary>
        /// The reliable lane's share of the send rate while both lanes have data queued.
        /// </summary>
        internal const ushort ReliableLaneWeight = 1;
        /// <summary>
        /// The unreliable lane's share of the send rate while both lanes have data queued: three to the reliable lane's one, so state keeps
        /// flowing while a reliable burst such as a late join still moves.
        /// </summary>
        internal const ushort UnreliableLaneWeight = 3;
        /// <summary>
        /// The fake port a server addressed by fake IP listens on: the first and only one it asks Steam for.
        /// </summary>
        internal const int FakePortIndex = 0;
        /// <summary>
        /// The most segments Synapse carries one packet in, which with the transmission unit sets the largest packet a client may send.
        /// </summary>
        /// <remarks>Matches Synapse, so a client is held to the same packet size whichever of the two transports it joins over.</remarks>
        internal const int MaximumClientPacketSegments = 255;
        /// <summary>
        /// The reason given to Steam whenever this transport closes a connection; the text beside it says why.
        /// </summary>
        private const int ApplicationCloseReason = (int)ESteamNetConnectionEnd.k_ESteamNetConnectionEnd_App_Generic;
        /// <summary>
        /// How long Steam holds back a packet it reorders, in milliseconds.
        /// </summary>
        private const int ReorderDelayMilliseconds = 100;

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
        /// Opens a listen socket that peers reach by the fake IP address Steam grants this account.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="options">The settings every connection the socket accepts is opened with, or <see langword="null"/> for Steam's own.</param>
        /// <remarks>Needs <see cref="BeginAsyncRequestFakeIP"/> called first, though not its grant to have arrived.</remarks>
        internal static HSteamListenSocket CreateListenSocketP2PFakeIP(bool isGameServer, SteamNetworkingConfigValue_t[] options) => isGameServer ? SteamGameServerNetworkingSockets.CreateListenSocketP2PFakeIP(FakePortIndex, GetOptionCount(options), options) : SteamNetworkingSockets.CreateListenSocketP2PFakeIP(FakePortIndex, GetOptionCount(options), options);

        /// <summary>
        /// Asks Steam for a fake IP address with one port.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <returns>True when a new request began; false when one was already made in this process, whose grant <see cref="GetFakeIP"/> reads.</returns>
        /// <remarks>The grant arrives later as a <see cref="SteamNetworkingFakeIPResult_t"/> callback.</remarks>
        internal static bool BeginAsyncRequestFakeIP(bool isGameServer) => isGameServer ? SteamGameServerNetworkingSockets.BeginAsyncRequestFakeIP(nNumPorts: 1) : SteamNetworkingSockets.BeginAsyncRequestFakeIP(nNumPorts: 1);

        /// <summary>
        /// Reads the fake IP address this process was already granted.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="fakeIpResult">Steam's grant, whose result says whether there is one.</param>
        internal static void GetFakeIP(bool isGameServer, out SteamNetworkingFakeIPResult_t fakeIpResult)
        {
            if (isGameServer)
                SteamGameServerNetworkingSockets.GetFakeIP(FakePortIndex, out fakeIpResult);
            else
                SteamNetworkingSockets.GetFakeIP(FakePortIndex, out fakeIpResult);
        }

        /// <summary>
        /// Creates the fake IP grant callback for the chosen interface, which Steam raises from inside its own callback pump.
        /// </summary>
        internal static Callback<SteamNetworkingFakeIPResult_t> CreateFakeIpResultCallback(bool isGameServer, Callback<SteamNetworkingFakeIPResult_t>.DispatchDelegate handler) => isGameServer ? Callback<SteamNetworkingFakeIPResult_t>.CreateGameServer(handler) : Callback<SteamNetworkingFakeIPResult_t>.Create(handler);

        /// <summary>
        /// Writes a fake IP grant as the address and port a client connects to, such as <c>169.254.12.34:27015</c>.
        /// </summary>
        /// <param name="fakeIpResult">A successful grant.</param>
        /// <returns>The address.</returns>
        internal static string FormatFakeAddress(in SteamNetworkingFakeIPResult_t fakeIpResult)
        {
            uint ip = fakeIpResult.m_unIP;

            return $"{ip >> 24}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}:{fakeIpResult.m_unPorts[FakePortIndex]}";
        }

        /// <summary>
        /// Builds the settings a socket's connections are opened with: Steam's send rate held at the transport's, its relay only setting
        /// and, on a server, the largest message a client may send. Steam's own timeouts are kept.
        /// </summary>
        /// <param name="steamNetworkingTransport">The transport whose settings are read.</param>
        /// <param name="isServer">True for the server's listen socket, whose accepted connections are held to the client packet limit.</param>
        /// <returns>The settings.</returns>
        /// <remarks>
        /// The rate is set as both Steam's lowest and highest, because Steam sends at its lowest unless something raises it, and nothing
        /// does, so a higher ceiling alone changes nothing. A rate of zero keeps Steam's own. A client sending a message past the limit is
        /// closed by Steam itself before anything is read, as Synapse refuses a packet needing more than its segments. Built once per
        /// socket, when it opens.
        /// </remarks>
        internal static SteamNetworkingConfigValue_t[] CreateConnectionOptions(SteamNetworkingTransport steamNetworkingTransport, bool isServer)
        {
            uint sendRateKilobytesPerSecond = steamNetworkingTransport.SendRateKilobytesPerSecond;

            List<SteamNetworkingConfigValue_t> options = new();

            if (sendRateKilobytesPerSecond > 0)
            {
                int sendRateBytesPerSecond = (int)Math.Min(sendRateKilobytesPerSecond * 1_024UL, int.MaxValue);

                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMin, sendRateBytesPerSecond));
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, sendRateBytesPerSecond));
            }

            // A Steam ID or fake IP link is a peer-to-peer link, which Steam may otherwise move to a direct route; an IP address is dialled as given.
            if (steamNetworkingTransport.IsRelayOnlyEnabled && steamNetworkingTransport.Addressing is SteamAddressing.SteamId or SteamAddressing.FakeIp)
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable, Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable));

            if (isServer)
                options.Add(CreateInt32Option(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_RecvMaxMessageSize, GetMaximumClientPacketBytes(steamNetworkingTransport)));

            return options.ToArray();
        }

        /// <summary>
        /// Returns the largest message a server accepts from a client: Synapse's most segments times the transmission unit.
        /// </summary>
        /// <param name="steamNetworkingTransport">The transport whose transmission unit is read.</param>
        /// <returns>The limit, in bytes.</returns>
        internal static int GetMaximumClientPacketBytes(SteamNetworkingTransport steamNetworkingTransport) => (int)Math.Min((long)MaximumClientPacketSegments * steamNetworkingTransport.Configuration.MaximumTransmissionUnit, int.MaxValue);

        /// <summary>
        /// Gives a connection its reliable and unreliable lanes, so unreliable state never waits behind a reliable burst queued in Steam.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="connection">The connection, which may still be connecting.</param>
        /// <param name="lanePriorities">The priority of each lane, <see cref="LaneCount"/> long.</param>
        /// <param name="laneWeights">The share of each lane, <see cref="LaneCount"/> long.</param>
        internal static void ConfigureLanes(bool isGameServer, HSteamNetConnection connection, int[] lanePriorities, ushort[] laneWeights)
        {
            EResult result = isGameServer
                ? SteamGameServerNetworkingSockets.ConfigureConnectionLanes(connection, LaneCount, lanePriorities, laneWeights)
                : SteamNetworkingSockets.ConfigureConnectionLanes(connection, LaneCount, lanePriorities, laneWeights);

            if (result is not EResult.k_EResultOK)
                Logger<SteamNetworkingTransport>.LogError($"Steam could not give connection [{connection.m_HSteamNetConnection}] its lanes: [{result}]. Its unreliable packets will be refused.");
        }

        /// <summary>
        /// Logs why Steam closed a link, which the engine's disconnect does not carry.
        /// </summary>
        /// <param name="connectionInfo">Steam's description of the closed link.</param>
        /// <param name="subject">What the link was, starting the sentence.</param>
        internal static void LogClosed(in SteamNetConnectionInfo_t connectionInfo, string subject) =>
            Logger<SteamNetworkingTransport>.LogInformation($"{subject} closed: [{(ESteamNetConnectionEnd)connectionInfo.m_eEndReason}] [{connectionInfo.m_szEndDebug}].");

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
        /// <param name="reason">Why, which the far end logs when it learns of the close, or <see langword="null"/> for a handle the far end already closed.</param>
        /// <param name="isLingerEnabled">True to keep sending reliable data already queued before the connection closes.</param>
        internal static void CloseConnection(bool isGameServer, HSteamNetConnection connection, string reason, bool isLingerEnabled)
        {
            if (isGameServer)
                SteamGameServerNetworkingSockets.CloseConnection(connection, ApplicationCloseReason, reason, isLingerEnabled);
            else
                SteamNetworkingSockets.CloseConnection(connection, ApplicationCloseReason, reason, isLingerEnabled);
        }

        /// <summary>
        /// Reads the identity Steam authenticated for the far end of a connection.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="connection">The connection.</param>
        /// <param name="remoteIdentity">The far end's identity, when Steam knows the connection.</param>
        /// <returns>True when Steam described the connection.</returns>
        internal static bool TryGetRemoteIdentity(bool isGameServer, HSteamNetConnection connection, out SteamNetworkingIdentity remoteIdentity)
        {
            SteamNetConnectionInfo_t connectionInfo;
            bool isKnown = isGameServer ? SteamGameServerNetworkingSockets.GetConnectionInfo(connection, out connectionInfo) : SteamNetworkingSockets.GetConnectionInfo(connection, out connectionInfo);
            remoteIdentity = connectionInfo.m_identityRemote;

            return isKnown;
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
        /// Allocates a native message with a payload buffer of the given size, for <see cref="SendMessages"/> to take.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="size">The payload's length, in bytes.</param>
        /// <returns>The message, which Steam frees once it is sent.</returns>
        internal static IntPtr AllocateMessage(bool isGameServer, int size) => isGameServer ? SteamGameServerNetworkingUtils.AllocateMessage(size) : SteamNetworkingUtils.AllocateMessage(size);

        /// <summary>
        /// Hands Steam native messages to send, each naming its connection, flags and lane. Steam owns every message afterwards, whether
        /// sent or refused.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="messages">The messages.</param>
        /// <param name="results">Filled with each message's number, or its negated <see cref="EResult"/> when refused.</param>
        internal static void SendMessages(bool isGameServer, IntPtr[] messages, long[] results)
        {
            if (isGameServer)
                SteamGameServerNetworkingSockets.SendMessages(messages.Length, messages, results, bDeleteFailedMessages: true);
            else
                SteamNetworkingSockets.SendMessages(messages.Length, messages, results, bDeleteFailedMessages: true);
        }

        /// <summary>
        /// Takes up to <paramref name="messages"/>.Length waiting messages from every connection in a poll group.
        /// </summary>
        internal static int ReceiveMessagesOnPollGroup(bool isGameServer, HSteamNetPollGroup pollGroup, IntPtr[] messages) => isGameServer ? SteamGameServerNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, messages, messages.Length) : SteamNetworkingSockets.ReceiveMessagesOnPollGroup(pollGroup, messages, messages.Length);

        /// <summary>
        /// Takes up to <paramref name="messages"/>.Length waiting messages from one connection.
        /// </summary>
        internal static int ReceiveMessagesOnConnection(bool isGameServer, HSteamNetConnection connection, IntPtr[] messages) => isGameServer ? SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection(connection, messages, messages.Length) : SteamNetworkingSockets.ReceiveMessagesOnConnection(connection, messages, messages.Length);

        /// <summary>
        /// Reads Steam's live status of one connection.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="connection">The connection, or invalid for none.</param>
        /// <param name="linkStatus">The status, when Steam reported one.</param>
        /// <returns>True when Steam reported the connection's status; false for an invalid connection, or once Steam has shut down.</returns>
        internal static bool TryGetLinkStatus(bool isGameServer, HSteamNetConnection connection, out SteamLinkStatus linkStatus)
        {
            linkStatus = default;

            if (connection == HSteamNetConnection.Invalid || IsShutDown(isGameServer))
                return false;

            SteamNetConnectionRealTimeStatus_t realTimeStatus = default;
            SteamNetConnectionRealTimeLaneStatus_t laneStatus = default;

            EResult result = isGameServer
                ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(connection, ref realTimeStatus, nLanes: 0, ref laneStatus)
                : SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref realTimeStatus, nLanes: 0, ref laneStatus);

            if (result is not EResult.k_EResultOK)
                return false;

            linkStatus = new(in realTimeStatus);

            return true;
        }

        /// <summary>
        /// Creates the status callback for the chosen interface, which Steam raises from inside its own callback pump.
        /// </summary>
        internal static Callback<SteamNetConnectionStatusChangedCallback_t> CreateStatusCallback(bool isGameServer, Callback<SteamNetConnectionStatusChangedCallback_t>.DispatchDelegate handler) => isGameServer ? Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(handler) : Callback<SteamNetConnectionStatusChangedCallback_t>.Create(handler);

        /// <summary>
        /// Writes simulated network conditions into the chosen interface's process-wide fake lag, loss, reordering and duplication.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="networkConditions">The conditions; the default value simulates nothing.</param>
        /// <returns>True when the interface was up and took them; false before Steam starts or once it has shut down.</returns>
        /// <remarks>
        /// Steam simulates on send only, so latency set on both peers adds to the round trip twice. Jitter follows Steam's own spread,
        /// averaging half of <see cref="NetworkConditions.JitterMilliseconds"/> and never above it, and a reordered packet is held back
        /// <see cref="ReorderDelayMilliseconds"/>. <see cref="NetworkConditions.Seed"/> is ignored, since Steam draws its own choices.
        /// </remarks>
        internal static bool TryApplyNetworkConditions(bool isGameServer, NetworkConditions networkConditions)
        {
            if (IsShutDown(isGameServer))
                return false;

            float jitterMilliseconds = networkConditions.JitterMilliseconds;
            IntPtr valuePointer = Marshal.AllocHGlobal(sizeof(int));

            try
            {
                SetGlobalInt32(isGameServer, valuePointer, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLag_Send, (int)Math.Min(networkConditions.LatencyMilliseconds, int.MaxValue));
                SetGlobalFloat(isGameServer, valuePointer, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketJitter_Send_Avg, jitterMilliseconds / 2f);
                SetGlobalFloat(isGameServer, valuePointer, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketJitter_Send_Max, jitterMilliseconds);
                SetGlobalFloat(isGameServer, valuePointer, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketJitter_Send_Pct, jitterMilliseconds > 0f ? 100f : 0f);
                SetGlobalFloat(isGameServer, valuePointer, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLoss_Send, networkConditions.PacketLossChance * 100f);
                SetGlobalFloat(isGameServer, valuePointer, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Send, networkConditions.OutOfOrderChance * 100f);
                SetGlobalInt32(isGameServer, valuePointer, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketReorder_Time, ReorderDelayMilliseconds);
                SetGlobalFloat(isGameServer, valuePointer, ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketDup_Send, networkConditions.DuplicateChance * 100f);
            }
            finally
            {
                Marshal.FreeHGlobal(valuePointer);
            }

            return true;
        }

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

        /// <summary>
        /// Sets one process-wide whole-number setting on the chosen interface.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="valuePointer">Four bytes of native memory the value is written through.</param>
        /// <param name="configValue">The setting.</param>
        /// <param name="value">Its value.</param>
        private static void SetGlobalInt32(bool isGameServer, IntPtr valuePointer, ESteamNetworkingConfigValue configValue, int value)
        {
            Marshal.WriteInt32(valuePointer, value);
            SetGlobal(isGameServer, valuePointer, configValue, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32);
        }

        /// <summary>
        /// Sets one process-wide decimal setting on the chosen interface.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="valuePointer">Four bytes of native memory the value is written through.</param>
        /// <param name="configValue">The setting.</param>
        /// <param name="value">Its value.</param>
        private static void SetGlobalFloat(bool isGameServer, IntPtr valuePointer, ESteamNetworkingConfigValue configValue, float value)
        {
            Marshal.WriteInt32(valuePointer, BitConverter.SingleToInt32Bits(value));
            SetGlobal(isGameServer, valuePointer, configValue, ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Float);
        }

        /// <summary>
        /// Hands Steam one process-wide setting already written to native memory.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="valuePointer">The native memory holding the value.</param>
        /// <param name="configValue">The setting.</param>
        /// <param name="dataType">The value's type.</param>
        private static void SetGlobal(bool isGameServer, IntPtr valuePointer, ESteamNetworkingConfigValue configValue, ESteamNetworkingConfigDataType dataType)
        {
            bool isAccepted = isGameServer
                ? SteamGameServerNetworkingUtils.SetConfigValue(configValue, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, dataType, valuePointer)
                : SteamNetworkingUtils.SetConfigValue(configValue, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, IntPtr.Zero, dataType, valuePointer);

            if (!isAccepted)
                Logger<SteamNetworkingTransport>.LogWarning($"Steam refused the setting [{configValue}], so it is not simulated.");
        }
    }
}
#endif
