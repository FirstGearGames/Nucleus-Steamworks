#if UNITY_INCLUDE_TESTS
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using Nucleus.Connections;
using Nucleus.Integrations.Unity.Managers.Core;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Integrations.Unity.Transports;
using Nucleus.Managers.Messages;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nucleus.Integrations.Unity.Tests
{
    /// <summary>
    /// Live coverage of the Steam transport against the Steam client running on this machine: a host's own client paired in process, and
    /// a client reaching a server through Steam's networking library by IP address.
    /// </summary>
    /// <remarks>
    /// Needs the Steam client running and signed in, and a <c>steam_appid.txt</c> holding an app id (480, Spacewar, for testing) in the
    /// project folder. Without them every case is ignored rather than failed. Peers cannot reach their own Steam ID through the relay
    /// network, so a client crossing Steam to a server in the same process addresses it by IP.
    /// </remarks>
    public class SteamNetworkingTransportPlayTests
    {
        /// <summary>
        /// How long a case waits on Steam or the engine before calling it a failure.
        /// </summary>
        private const float MaximumWaitSeconds = 30f;

        /// <summary>
        /// Everything a case created, destroyed after it.
        /// </summary>
        private readonly List<GameObject> _createdObjects = new();

        /// <summary>
        /// Whether this fixture initialised Steam, and so shuts it down.
        /// </summary>
        private bool _isSteamInitialized;

        /// <summary>
        /// Initialises Steam once for the fixture, or ignores every case when it cannot be.
        /// </summary>
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _isSteamInitialized = SteamAPI.Init();

            if (!_isSteamInitialized)
                Assert.Ignore("Steam could not be initialised. Run the Steam client and put a steam_appid.txt holding an app id in the project folder.");
        }

        /// <summary>
        /// Shuts Steam down if this fixture started it.
        /// </summary>
        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (_isSteamInitialized)
                SteamAPI.Shutdown();
        }

        /// <summary>
        /// Starts running Steam's callbacks for the case.
        /// </summary>
        /// <returns>The coroutine doing the setup.</returns>
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            NewInactiveObject("SteamCallbackPump").AddComponent<SteamCallbackPump>().gameObject.SetActive(true);

            yield return null;
        }

        /// <summary>
        /// Destroys everything the case created, and gives Steam a moment to close what those objects held.
        /// </summary>
        /// <returns>The coroutine doing the teardown.</returns>
        [UnityTearDown]
        public IEnumerator TearDown()
        {
            foreach (GameObject createdObject in _createdObjects)
            {
                if (createdObject != null)
                    Object.Destroy(createdObject);
            }

            _createdObjects.Clear();

            yield return new WaitForSecondsRealtime(0.5f);
        }

        /// <summary>
        /// A host's own client is paired with its server inside the process, is the same Connection on both sides, and pairs again
        /// after it leaves.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator HostsOwnClient_PairsInProcess_AndPairsAgainAfterLeaving()
        {
            GameObject hostObject = NewInactiveObject("SteamHost");
            UnityCoreManager hostCoreManager = hostObject.AddComponent<UnityCoreManager>();
            UnityTransportManager hostTransportManager = hostObject.AddComponent<UnityTransportManager>();
            hostObject.AddComponent<SteamTransport>();
            hostObject.SetActive(true);

            yield return WaitForTask(hostTransportManager.StartServerAsync());

            TransportManager transportManager = hostCoreManager.CoreManager.TransportManager;
            yield return WaitUntil(() => transportManager.IsServerStarted);
            Assert.IsTrue(transportManager.IsServerStarted, "The host's server never started listening on Steam.");

            Transport transport = transportManager.Transports[0];

            for (int link = 0; link < 2; link++)
            {
                yield return WaitForTask(hostTransportManager.StartClientAsync());
                yield return WaitUntil(() => IsClientAuthenticated(transportManager));
                Assert.IsTrue(IsClientAuthenticated(transportManager), $"The host's own client never authenticated on link [{link}].");

                transportManager.TryGetLocalClientConnection(out Connection localClientConnection);
                Assert.IsTrue(IsHeldByServer(transportManager, localClientConnection), $"The server's roster entry for the host's own client is not the client's own Connection on link [{link}].");
                Assert.IsTrue(transport.IsOwnClientConnection(localClientConnection), $"The transport did not recognise the host's own client on link [{link}].");

                yield return WaitForTask(transport.DisconnectLocalConnectionAsync(Invoker.Client));
                yield return WaitUntil(() => !IsHeldByServer(transportManager, localClientConnection));
                Assert.IsFalse(IsHeldByServer(transportManager, localClientConnection), $"The server still held the host's own client after it left on link [{link}].");
            }
        }

        /// <summary>
        /// A client reaches a server by IP address through Steam's networking library, is seen by the server as a remote client, and
        /// connects again after it leaves.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator RemoteClient_ByIpAddress_ConnectsThroughSteam_AndConnectsAgainAfterLeaving()
        {
            ushort port = ProbeFreeUdpPort();

            GameObject serverObject = NewInactiveObject("SteamServer");
            UnityCoreManager serverCoreManager = serverObject.AddComponent<UnityCoreManager>();
            UnityTransportManager serverTransportManager = serverObject.AddComponent<UnityTransportManager>();
            serverObject.AddComponent<SteamTransport>();
            serverObject.SetActive(true);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            TransportManager serverTransportManagerCore = serverCoreManager.CoreManager.TransportManager;
            SteamNetworkingTransport serverTransport = (SteamNetworkingTransport)serverTransportManagerCore.Transports[0];
            serverTransport.Addressing = SteamAddressing.IpAddress;
            serverTransport.Configuration.Port = port;

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitUntil(() => serverTransportManagerCore.IsServerStarted);
            Assert.IsTrue(serverTransportManagerCore.IsServerStarted, $"The server never started listening on port [{port}].");

            GameObject clientObject = NewInactiveObject("SteamClient");
            UnityCoreManager clientCoreManager = clientObject.AddComponent<UnityCoreManager>();
            UnityTransportManager clientTransportManager = clientObject.AddComponent<UnityTransportManager>();
            SteamTransport clientSteamTransport = clientObject.AddComponent<SteamTransport>();
            clientSteamTransport.RemoteAddress = $"127.0.0.1:{port}";
            clientObject.SetActive(true);

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            TransportManager clientTransportManagerCore = clientCoreManager.CoreManager.TransportManager;
            Transport clientTransport = clientTransportManagerCore.Transports[0];
            ((SteamNetworkingTransport)clientTransport).Addressing = SteamAddressing.IpAddress;

            int serverReceivedValue = -1;
            int clientReceivedValue = -1;

            void OnServerReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage) => serverReceivedValue = steamProbeMessage.Value;
            void OnClientReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage) => clientReceivedValue = steamProbeMessage.Value;

            serverCoreManager.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnServerReceivedProbe);
            clientCoreManager.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);

            for (int link = 0; link < 2; link++)
            {
                yield return WaitForTask(clientTransportManager.StartClientAsync());
                yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 1);
                Assert.IsTrue(IsClientAuthenticated(clientTransportManagerCore), $"The client never authenticated over Steam on link [{link}].");
                Assert.AreEqual(1, serverTransportManagerCore.ActiveConnections.Count, $"The server does not hold exactly the one remote client on link [{link}].");

                foreach (Connection serverSideClientConnection in serverTransportManagerCore.ActiveConnections)
                    Assert.IsFalse(serverTransport.IsOwnClientConnection(serverSideClientConnection), $"The server took a remote client for its own on link [{link}].");

                // Data each way, beyond the handshake: the server approves a client without reading anything it sent.
                byte clientSentValue = (byte)(10 + link);
                byte serverSentValue = (byte)(20 + link);

                clientTransportManagerCore.TryGetServerConnection(out Connection serverConnection);
                serverConnection.SendMessage(Channel.Reliable, new SteamProbeMessage { Value = clientSentValue });
                serverCoreManager.CoreManager.ServerManager.SendMessage(Channel.Reliable, new SteamProbeMessage { Value = serverSentValue });

                yield return WaitUntil(() => serverReceivedValue == clientSentValue && clientReceivedValue == serverSentValue);
                Assert.AreEqual(clientSentValue, serverReceivedValue, $"The client's message never reached the server over Steam on link [{link}].");
                Assert.AreEqual(serverSentValue, clientReceivedValue, $"The server's message never reached the client over Steam on link [{link}].");

                yield return WaitForTask(clientTransport.DisconnectLocalConnectionAsync(Invoker.Client));
                yield return WaitUntil(() => serverTransportManagerCore.ActiveConnections.Count == 0);
                Assert.AreEqual(0, serverTransportManagerCore.ActiveConnections.Count, $"The server still held the client after it left on link [{link}].");
            }

            serverCoreManager.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnServerReceivedProbe);
            clientCoreManager.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);
        }

        /// <summary>
        /// A remote client's link has a Steam status on both sides, and a host's own client, paired in process, has none.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator RemoteClient_ByIpAddress_HasALinkStatusOnBothSides_AndTheHostsOwnClientHasNone()
        {
            ushort port = ProbeFreeUdpPort();

            GameObject serverObject = NewInactiveObject("SteamServer");
            UnityCoreManager serverCoreManager = serverObject.AddComponent<UnityCoreManager>();
            UnityTransportManager serverTransportManager = serverObject.AddComponent<UnityTransportManager>();
            serverObject.AddComponent<SteamTransport>();
            serverObject.SetActive(true);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            TransportManager serverTransportManagerCore = serverCoreManager.CoreManager.TransportManager;
            SteamNetworkingTransport serverTransport = (SteamNetworkingTransport)serverTransportManagerCore.Transports[0];
            serverTransport.Addressing = SteamAddressing.IpAddress;
            serverTransport.Configuration.Port = port;

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitForTask(serverTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(serverTransportManagerCore));
            Assert.IsTrue(IsClientAuthenticated(serverTransportManagerCore), "The host's own client never authenticated.");

            serverTransportManagerCore.TryGetLocalClientConnection(out Connection hostClientConnection);

            GameObject clientObject = NewInactiveObject("SteamClient");
            UnityCoreManager clientCoreManager = clientObject.AddComponent<UnityCoreManager>();
            UnityTransportManager clientTransportManager = clientObject.AddComponent<UnityTransportManager>();
            clientObject.AddComponent<SteamTransport>().RemoteAddress = $"127.0.0.1:{port}";
            clientObject.SetActive(true);

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            TransportManager clientTransportManagerCore = clientCoreManager.CoreManager.TransportManager;
            SteamNetworkingTransport clientTransport = (SteamNetworkingTransport)clientTransportManagerCore.Transports[0];
            clientTransport.Addressing = SteamAddressing.IpAddress;

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 2);
            Assert.IsTrue(IsClientAuthenticated(clientTransportManagerCore), "The remote client never authenticated over Steam.");

            Connection remoteClientConnection = null;

            foreach (Connection activeConnection in serverTransportManagerCore.ActiveConnections)
            {
                if (!ReferenceEquals(activeConnection, hostClientConnection))
                    remoteClientConnection = activeConnection;
            }

            clientTransportManagerCore.TryGetLocalClientConnection(out Connection clientConnection);
            clientTransportManagerCore.TryGetServerConnection(out Connection serverConnection);

            Assert.IsNotNull(remoteClientConnection, "The server holds no remote client.");
            Assert.IsTrue(serverTransport.TryGetLinkStatus(remoteClientConnection, out SteamLinkStatus _), "The server read no link status for its remote client.");
            Assert.IsTrue(clientTransport.TryGetLinkStatus(clientConnection, out SteamLinkStatus _), "The client read no link status through its own Connection.");
            Assert.IsTrue(clientTransport.TryGetLinkStatus(serverConnection, out SteamLinkStatus _), "The client read no link status through its server Connection.");
            Assert.IsFalse(serverTransport.TryGetLinkStatus(hostClientConnection, out SteamLinkStatus _), "The host's own client, paired in process, read a Steam link status.");
        }

        /// <summary>
        /// Simulated latency reaches Steam: with 100 ms set, the ping Steam measures on a remote client's link rises by about 200 ms,
        /// since both peers share this process and Steam delays each one's sends, and falls back once the conditions are cleared.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator RemoteClient_ByIpAddress_UnderSimulatedLatency_SteamsPingRises()
        {
            const uint LatencyMilliseconds = 100;

            ushort port = ProbeFreeUdpPort();

            GameObject serverObject = NewInactiveObject("SteamServer");
            UnityCoreManager serverCoreManager = serverObject.AddComponent<UnityCoreManager>();
            UnityTransportManager serverTransportManager = serverObject.AddComponent<UnityTransportManager>();
            serverObject.AddComponent<SteamTransport>();
            serverObject.SetActive(true);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            TransportManager serverTransportManagerCore = serverCoreManager.CoreManager.TransportManager;
            SteamNetworkingTransport serverTransport = (SteamNetworkingTransport)serverTransportManagerCore.Transports[0];
            serverTransport.Addressing = SteamAddressing.IpAddress;
            serverTransport.Configuration.Port = port;

            yield return WaitForTask(serverTransportManager.StartServerAsync());

            GameObject clientObject = NewInactiveObject("SteamClient");
            UnityCoreManager clientCoreManager = clientObject.AddComponent<UnityCoreManager>();
            UnityTransportManager clientTransportManager = clientObject.AddComponent<UnityTransportManager>();
            clientObject.AddComponent<SteamTransport>().RemoteAddress = $"127.0.0.1:{port}";
            clientObject.SetActive(true);

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            TransportManager clientTransportManagerCore = clientCoreManager.CoreManager.TransportManager;
            ((SteamNetworkingTransport)clientTransportManagerCore.Transports[0]).Addressing = SteamAddressing.IpAddress;

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 1);
            Assert.AreEqual(1, serverTransportManagerCore.ActiveConnections.Count, "The server does not hold the remote client.");

            Connection remoteClientConnection = null;

            foreach (Connection activeConnection in serverTransportManagerCore.ActiveConnections)
                remoteClientConnection = activeConnection;

            void OnClientReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage) { }

            clientCoreManager.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);

            serverTransport.TryGetLinkStatus(remoteClientConnection, out SteamLinkStatus baselineLinkStatus);
            int baselinePingMilliseconds = baselineLinkStatus.PingMilliseconds;
            int raisedPingMilliseconds = baselinePingMilliseconds + (int)LatencyMilliseconds * 3 / 2;

            serverTransport.SetNetworkConditions(new NetworkConditions { LatencyMilliseconds = LatencyMilliseconds });

            try
            {
                // Steam measures ping from its own keepalives and acknowledgements, so traffic keeps the measurement fresh.
                yield return WaitUntil(() => IsPingAtLeast(serverTransport, remoteClientConnection, raisedPingMilliseconds));
                serverTransport.TryGetLinkStatus(remoteClientConnection, out SteamLinkStatus raisedLinkStatus);
                Assert.GreaterOrEqual(raisedLinkStatus.PingMilliseconds, raisedPingMilliseconds, $"Steam's ping rose from [{baselinePingMilliseconds}] ms only to [{raisedLinkStatus.PingMilliseconds}] ms under [{LatencyMilliseconds}] ms of simulated latency.");
                Assert.AreEqual(LatencyMilliseconds, serverTransport.GetNetworkConditions()?.LatencyMilliseconds, "The transport did not keep the conditions it was given.");
            }
            finally
            {
                serverTransport.SetNetworkConditions(networkConditions: null);
            }

            yield return WaitUntil(() => !IsPingAtLeast(serverTransport, remoteClientConnection, raisedPingMilliseconds));
            serverTransport.TryGetLinkStatus(remoteClientConnection, out SteamLinkStatus clearedLinkStatus);
            Assert.Less(clearedLinkStatus.PingMilliseconds, raisedPingMilliseconds, "Steam's ping stayed raised after the conditions were cleared.");

            clientCoreManager.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);
        }

        /// <summary>
        /// A transport given simulated conditions clears them from Steam, which keeps them for the whole process, when it shuts down, so
        /// they do not carry into the next play session.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator Transport_GivenNetworkConditions_ClearsThemFromSteamWhenShutDown()
        {
            const uint LatencyMilliseconds = 100;

            GameObject serverObject = NewInactiveObject("SteamServer");
            UnityCoreManager serverCoreManager = serverObject.AddComponent<UnityCoreManager>();
            UnityTransportManager serverTransportManager = serverObject.AddComponent<UnityTransportManager>();
            serverObject.AddComponent<SteamTransport>();
            serverObject.SetActive(true);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            SteamNetworkingTransport serverTransport = (SteamNetworkingTransport)serverCoreManager.CoreManager.TransportManager.Transports[0];
            serverTransport.SetNetworkConditions(new NetworkConditions { LatencyMilliseconds = LatencyMilliseconds });

            Assert.AreEqual((int)LatencyMilliseconds, GetSteamsSimulatedLatencyMilliseconds(), "Steam did not take the simulated latency.");

            Object.Destroy(serverObject);

            yield return WaitUntil(() => GetSteamsSimulatedLatencyMilliseconds() == 0);
            Assert.AreEqual(0, GetSteamsSimulatedLatencyMilliseconds(), "Steam still simulated latency after the transport shut down.");
        }

        /// <summary>
        /// A client the connecting filter refuses is closed before the engine hears of it, the filter is asked with the client's own Steam
        /// ID, and the same client joins once the filter lets it.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator RemoteClient_RefusedByTheConnectingFilter_NeverJoins_AndJoinsOnceLetIn()
        {
            ushort port = ProbeFreeUdpPort();
            ulong ownSteamId = SteamUser.GetSteamID().m_SteamID;
            ulong askedSteamId = SteamNetworkingTransport.UnsetSteamId;
            bool isClientAllowed = false;

            bool FilterConnectingClient(ulong steamId)
            {
                askedSteamId = steamId;

                return isClientAllowed;
            }

            GameObject serverObject = NewInactiveObject("SteamServer");
            UnityCoreManager serverCoreManager = serverObject.AddComponent<UnityCoreManager>();
            UnityTransportManager serverTransportManager = serverObject.AddComponent<UnityTransportManager>();
            serverObject.AddComponent<SteamTransport>().ConnectingClientFilter = FilterConnectingClient;
            serverObject.SetActive(true);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            TransportManager serverTransportManagerCore = serverCoreManager.CoreManager.TransportManager;
            SteamNetworkingTransport serverTransport = (SteamNetworkingTransport)serverTransportManagerCore.Transports[0];
            serverTransport.Addressing = SteamAddressing.IpAddress;
            serverTransport.Configuration.Port = port;

            yield return WaitForTask(serverTransportManager.StartServerAsync());

            GameObject clientObject = NewInactiveObject("SteamClient");
            UnityCoreManager clientCoreManager = clientObject.AddComponent<UnityCoreManager>();
            UnityTransportManager clientTransportManager = clientObject.AddComponent<UnityTransportManager>();
            clientObject.AddComponent<SteamTransport>().RemoteAddress = $"127.0.0.1:{port}";
            clientObject.SetActive(true);

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            TransportManager clientTransportManagerCore = clientCoreManager.CoreManager.TransportManager;
            ((SteamNetworkingTransport)clientTransportManagerCore.Transports[0]).Addressing = SteamAddressing.IpAddress;

            LogAssert.ignoreFailingMessages = true;

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return new WaitForSecondsRealtime(1f);

            LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(ownSteamId, askedSteamId, "The filter was not asked with the connecting client's Steam ID.");
            Assert.IsFalse(IsClientAuthenticated(clientTransportManagerCore), "A client the filter refused joined.");
            Assert.AreEqual(0, serverTransportManagerCore.ActiveConnections.Count, "The server holds a client the filter refused.");

            isClientAllowed = true;

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore));
            Assert.IsTrue(IsClientAuthenticated(clientTransportManagerCore), "The client never joined once the filter let it in.");
        }

        /// <summary>
        /// A server knows the Steam ID of every client and a client knows its own; a client learns the other clients' Steam IDs only
        /// while the server shares them.
        /// </summary>
        /// <param name="isShared">Whether the server shares Steam IDs.</param>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator ClientSteamIds_AreKnownToTheServer_AndToOtherClientsOnlyWhenShared([Values(true, false)] bool isShared)
        {
            ushort port = ProbeFreeUdpPort();
            ulong ownSteamId = SteamUser.GetSteamID().m_SteamID;

            GameObject serverObject = NewInactiveObject("SteamServer");
            UnityCoreManager serverCoreManager = serverObject.AddComponent<UnityCoreManager>();
            UnityTransportManager serverTransportManager = serverObject.AddComponent<UnityTransportManager>();
            serverObject.AddComponent<SteamTransport>();
            serverObject.SetActive(true);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            TransportManager serverTransportManagerCore = serverCoreManager.CoreManager.TransportManager;
            SteamNetworkingTransport serverTransport = (SteamNetworkingTransport)serverTransportManagerCore.Transports[0];
            serverTransport.Addressing = SteamAddressing.IpAddress;
            serverTransport.Configuration.Port = port;
            serverTransport.ShareSteamIdsEnabled = isShared;

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitForTask(serverTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(serverTransportManagerCore));

            serverTransportManagerCore.TryGetLocalClientConnection(out Connection hostClientConnection);

            GameObject clientObject = NewInactiveObject("SteamClient");
            UnityCoreManager clientCoreManager = clientObject.AddComponent<UnityCoreManager>();
            UnityTransportManager clientTransportManager = clientObject.AddComponent<UnityTransportManager>();
            clientObject.AddComponent<SteamTransport>().RemoteAddress = $"127.0.0.1:{port}";
            clientObject.SetActive(true);

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            TransportManager clientTransportManagerCore = clientCoreManager.CoreManager.TransportManager;
            SteamNetworkingTransport clientTransport = (SteamNetworkingTransport)clientTransportManagerCore.Transports[0];
            clientTransport.Addressing = SteamAddressing.IpAddress;

            int clientSteamIdChangedCount = 0;

            void OnClientSteamIdChanged(uint connectionId, ulong steamId) => clientSteamIdChangedCount++;

            clientTransport.SteamIdChanged += OnClientSteamIdChanged;

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 2);

            clientTransportManagerCore.TryGetLocalClientConnection(out Connection clientConnection);

            Connection remoteClientConnection = null;

            foreach (Connection activeConnection in serverTransportManagerCore.ActiveConnections)
            {
                if (!ReferenceEquals(activeConnection, hostClientConnection))
                    remoteClientConnection = activeConnection;
            }

            // Shared IDs arrive a moment after the client joins; a case that shares none still waits as long before it looks.
            yield return WaitUntil(() => clientTransport.TryGetSteamId(hostClientConnection.Id, out _) || !isShared && clientSteamIdChangedCount > 0);
            yield return new WaitForSecondsRealtime(0.5f);

            Assert.IsTrue(serverTransport.TryGetSteamId(remoteClientConnection, out ulong remoteClientSteamId) && remoteClientSteamId == ownSteamId, "The server does not know its remote client's Steam ID.");
            Assert.IsTrue(serverTransport.TryGetSteamId(hostClientConnection, out ulong hostClientSteamId) && hostClientSteamId == ownSteamId, "The server does not know its own client's Steam ID.");
            Assert.IsTrue(clientTransport.TryGetSteamId(clientConnection, out ulong clientSteamId) && clientSteamId == ownSteamId, "The client does not know its own Steam ID.");
            Assert.AreEqual(isShared, clientTransport.TryGetSteamId(hostClientConnection.Id, out _), isShared ? "The client never learned the other client's Steam ID while the server shared them." : "The client learned the other client's Steam ID while the server shared none.");
            Assert.AreEqual(isShared ? 2 : 1, clientSteamIdChangedCount, "The client did not report each Steam ID it learned once.");
            Assert.IsTrue(clientTransport.TryGetConnection(ownSteamId, out Connection _), "The client could not find a Connection by Steam ID.");

            clientTransport.SteamIdChanged -= OnClientSteamIdChanged;
        }

        /// <summary>
        /// A server reads a client's message up to Synapse's largest packet, and Steam closes a client that sends a larger one before the
        /// server reads any of it.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator RemoteClient_SendingPastThePacketLimit_IsClosed()
        {
            ushort port = ProbeFreeUdpPort();

            GameObject serverObject = NewInactiveObject("SteamServer");
            UnityCoreManager serverCoreManager = serverObject.AddComponent<UnityCoreManager>();
            UnityTransportManager serverTransportManager = serverObject.AddComponent<UnityTransportManager>();
            serverObject.AddComponent<SteamTransport>();
            serverObject.SetActive(true);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            TransportManager serverTransportManagerCore = serverCoreManager.CoreManager.TransportManager;
            SteamNetworkingTransport serverTransport = (SteamNetworkingTransport)serverTransportManagerCore.Transports[0];
            serverTransport.Addressing = SteamAddressing.IpAddress;
            serverTransport.Configuration.Port = port;

            yield return WaitForTask(serverTransportManager.StartServerAsync());

            GameObject clientObject = NewInactiveObject("SteamClient");
            UnityCoreManager clientCoreManager = clientObject.AddComponent<UnityCoreManager>();
            UnityTransportManager clientTransportManager = clientObject.AddComponent<UnityTransportManager>();
            clientObject.AddComponent<SteamTransport>().RemoteAddress = $"127.0.0.1:{port}";
            clientObject.SetActive(true);

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            TransportManager clientTransportManagerCore = clientCoreManager.CoreManager.TransportManager;
            ((SteamNetworkingTransport)clientTransportManagerCore.Transports[0]).Addressing = SteamAddressing.IpAddress;

            int lastReceivedByteCount = 0;

            void OnServerReceivedBulk(in MessageContext messageContext, SteamBulkMessage steamBulkMessage)
            {
                lastReceivedByteCount = steamBulkMessage.Payload.Count;

                if (messageContext.IsDeserialized && steamBulkMessage.Payload.Array is not null)
                    CodeBoost.Performance.TrackedArrayPool<byte>.Return(steamBulkMessage.Payload.Array);
            }

            serverCoreManager.CoreManager.MessageManager.RegisterMessageHandler<SteamBulkMessage>(OnServerReceivedBulk);

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 1);

            int limitBytes = 255 * serverTransport.Configuration.MaximumTransmissionUnit;
            int underLimitBytes = limitBytes / 2;

            clientTransportManagerCore.TryGetServerConnection(out Connection serverConnection);
            serverConnection.SendMessage(Channel.Reliable, new SteamBulkMessage { Payload = new(new byte[underLimitBytes]) });

            yield return WaitUntil(() => lastReceivedByteCount == underLimitBytes);
            Assert.AreEqual(underLimitBytes, lastReceivedByteCount, "The server never read a message well under the client packet limit.");

            LogAssert.ignoreFailingMessages = true;

            serverConnection.SendMessage(Channel.Reliable, new SteamBulkMessage { Payload = new(new byte[limitBytes + 1_000]) });

            yield return WaitUntil(() => serverTransportManagerCore.ActiveConnections.Count == 0);

            LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(0, serverTransportManagerCore.ActiveConnections.Count, "The server still holds a client that sent a message past the limit.");
            Assert.AreEqual(underLimitBytes, lastReceivedByteCount, "The server read a message past the limit.");

            serverCoreManager.CoreManager.MessageManager.UnregisterMessageHandler<SteamBulkMessage>(OnServerReceivedBulk);
        }

        /// <summary>
        /// An unreliable message sent behind a reliable burst still queued in Steam arrives before the burst does, because the two ride
        /// lanes of their own.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator UnreliableMessage_SentBehindAReliableBurst_ArrivesFirst()
        {
            const int BurstBytes = 400_000;
            const byte UnreliableValue = 7;

            ushort port = ProbeFreeUdpPort();

            GameObject serverObject = NewInactiveObject("SteamServer");
            UnityCoreManager serverCoreManager = serverObject.AddComponent<UnityCoreManager>();
            UnityTransportManager serverTransportManager = serverObject.AddComponent<UnityTransportManager>();
            serverObject.AddComponent<SteamTransport>();
            serverObject.SetActive(true);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            TransportManager serverTransportManagerCore = serverCoreManager.CoreManager.TransportManager;
            SteamNetworkingTransport serverTransport = (SteamNetworkingTransport)serverTransportManagerCore.Transports[0];
            serverTransport.Addressing = SteamAddressing.IpAddress;
            serverTransport.Configuration.Port = port;
            // Slow enough that the burst takes about half a second to leave, so anything queued behind it would plainly wait.
            serverTransport.SendRateKilobytesPerSecond = 768;

            yield return WaitForTask(serverTransportManager.StartServerAsync());

            GameObject clientObject = NewInactiveObject("SteamClient");
            UnityCoreManager clientCoreManager = clientObject.AddComponent<UnityCoreManager>();
            UnityTransportManager clientTransportManager = clientObject.AddComponent<UnityTransportManager>();
            clientObject.AddComponent<SteamTransport>().RemoteAddress = $"127.0.0.1:{port}";
            clientObject.SetActive(true);

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            TransportManager clientTransportManagerCore = clientCoreManager.CoreManager.TransportManager;
            ((SteamNetworkingTransport)clientTransportManagerCore.Transports[0]).Addressing = SteamAddressing.IpAddress;

            float burstArrivedSeconds = -1f;
            float unreliableArrivedSeconds = -1f;

            void OnClientReceivedBulk(in MessageContext messageContext, SteamBulkMessage steamBulkMessage)
            {
                burstArrivedSeconds = Time.realtimeSinceStartup;

                if (messageContext.IsDeserialized && steamBulkMessage.Payload.Array is not null)
                    CodeBoost.Performance.TrackedArrayPool<byte>.Return(steamBulkMessage.Payload.Array);
            }

            void OnClientReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage)
            {
                if (steamProbeMessage.Value == UnreliableValue)
                    unreliableArrivedSeconds = Time.realtimeSinceStartup;
            }

            clientCoreManager.CoreManager.MessageManager.RegisterMessageHandler<SteamBulkMessage>(OnClientReceivedBulk);
            clientCoreManager.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 1);

            serverCoreManager.CoreManager.ServerManager.SendMessage(Channel.Reliable, new SteamBulkMessage { Payload = new(new byte[BurstBytes]) });

            // A frame later, so the burst is already in Steam's queue when the unreliable message is handed over.
            yield return null;
            yield return null;

            serverCoreManager.CoreManager.ServerManager.SendMessage(Channel.Unreliable, new SteamProbeMessage { Value = UnreliableValue });

            yield return WaitUntil(() => burstArrivedSeconds >= 0f && unreliableArrivedSeconds >= 0f);

            Assert.GreaterOrEqual(burstArrivedSeconds, 0f, "The reliable burst never arrived.");
            Assert.GreaterOrEqual(unreliableArrivedSeconds, 0f, "The unreliable message never arrived.");
            Assert.Less(unreliableArrivedSeconds, burstArrivedSeconds, "The unreliable message waited behind the reliable burst.");

            clientCoreManager.CoreManager.MessageManager.UnregisterMessageHandler<SteamBulkMessage>(OnClientReceivedBulk);
            clientCoreManager.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);
        }

        /// <summary>
        /// Launch arguments set where the client connects and the port, and a Steam ID that is not a number is refused with an error.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        public IEnumerator LaunchArguments_SetTheAddressSteamIdAndPort()
        {
            GameObject transportObject = NewInactiveObject("SteamTransport");
            UnityCoreManager unityCoreManager = transportObject.AddComponent<UnityCoreManager>();
            UnityTransportManager unityTransportManager = transportObject.AddComponent<UnityTransportManager>();
            transportObject.AddComponent<SteamTransport>();
            transportObject.SetActive(true);

            yield return WaitForTask(unityTransportManager.EnsureAddedAsync());

            SteamNetworkingTransport steamNetworkingTransport = (SteamNetworkingTransport)unityCoreManager.CoreManager.TransportManager.Transports[0];

            steamNetworkingTransport.ApplyCommandLineArguments(new CommandLineArguments(new[] { "-address", "203.0.113.7:27015", "-steamid", "76561198012345678", "-port", "27016" }));

            Assert.AreEqual("203.0.113.7:27015", steamNetworkingTransport.RemoteAddress, "The address argument was not applied.");
            Assert.AreEqual(76561198012345678UL, steamNetworkingTransport.RemoteSteamId, "The Steam ID argument was not applied.");
            Assert.AreEqual(27016, steamNetworkingTransport.Configuration.Port, "The port argument was not applied.");

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("is not a Steam ID"));
            steamNetworkingTransport.ApplyCommandLineArguments(new CommandLineArguments(new[] { "-steamid", "someone" }));

            Assert.AreEqual(76561198012345678UL, steamNetworkingTransport.RemoteSteamId, "A Steam ID argument that is not a number replaced the one set before.");
        }

        /// <summary>
        /// By default one Steam account holds one connection: a second client signed in to the same account is refused while the first
        /// keeps its link, and the second joins once the cap is lifted.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator SteamIdCap_ByDefault_RefusesASecondConnectionFromOneAccount_UntilLifted()
        {
            ushort port = ProbeFreeUdpPort();

            UnityTransportManager serverTransportManager = CreatePeer("SteamServer", string.Empty);
            UnityTransportManager firstClientTransportManager = CreatePeer("SteamFirstClient", $"127.0.0.1:{port}");
            UnityTransportManager secondClientTransportManager = CreatePeer("SteamSecondClient", $"127.0.0.1:{port}");

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(firstClientTransportManager.EnsureAddedAsync());
            yield return WaitForTask(secondClientTransportManager.EnsureAddedAsync());

            SteamNetworkingTransport serverTransport = UseIpAddressing(serverTransportManager, port);
            SteamNetworkingTransport firstClientTransport = UseIpAddressing(firstClientTransportManager, port);
            SteamNetworkingTransport secondClientTransport = UseIpAddressing(secondClientTransportManager, port);
            TransportManager serverTransportManagerCore = serverTransport.CoreManager.TransportManager;
            TransportManager firstClientTransportManagerCore = firstClientTransport.CoreManager.TransportManager;
            TransportManager secondClientTransportManagerCore = secondClientTransport.CoreManager.TransportManager;

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitForTask(firstClientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(firstClientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 1);
            Assert.IsTrue(IsClientAuthenticated(firstClientTransportManagerCore), "The first client never joined.");

            // The refused client logs that it could not connect.
            LogAssert.ignoreFailingMessages = true;

            yield return WaitForTask(secondClientTransportManager.StartClientAsync());
            yield return new WaitForSecondsRealtime(1f);

            LogAssert.ignoreFailingMessages = false;

            Assert.IsFalse(IsClientAuthenticated(secondClientTransportManagerCore), "A second connection from one Steam account joined under the default cap of one.");
            Assert.AreEqual(1, serverTransportManagerCore.ActiveConnections.Count, "The server does not hold exactly the first client after refusing the second.");

            // Refusing the second client left the first one's link alone.
            yield return AssertProbesCrossEachWay(serverTransport, firstClientTransport, value: 51);

            serverTransport.MaximumConnectionsPerSteamId = SteamNetworkingTransport.UnsetMaximumConnectionsPerSteamId;

            yield return WaitForTask(secondClientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(secondClientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 2);
            Assert.IsTrue(IsClientAuthenticated(secondClientTransportManagerCore), "The second client never joined once the cap was lifted.");
            Assert.AreEqual(2, serverTransportManagerCore.ActiveConnections.Count, "The server does not hold both clients once the cap was lifted.");

            yield return AssertProbesCrossEachWay(serverTransport, secondClientTransport, value: 53);
        }

        /// <summary>
        /// While a cap is set, a connection whose Steam ID Steam could not authenticate is refused, since it cannot be counted, and the
        /// client already connected keeps its link.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        /// <remarks>
        /// Steam authenticates every link this machine can open, even with <c>k_ESteamNetworkingConfig_IP_AllowWithoutAuth</c> set, so the
        /// arrival is Steam's report of a connected link with its unauthenticated flag set, handed to the server socket as Steam's own
        /// callback would hand it. The cap is two, so counting alone would let the arrival in beside the client already connected.
        /// </remarks>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator SteamIdCap_AConnectionSteamCouldNotAuthenticate_IsRefused_AndNobodyIsClosed()
        {
            const uint UnissuedSteamConnectionHandle = uint.MaxValue;

            ulong ownSteamId = SteamUser.GetSteamID().m_SteamID;
            ushort port = ProbeFreeUdpPort();

            UnityTransportManager serverTransportManager = CreatePeer("SteamServer", string.Empty);
            UnityTransportManager clientTransportManager = CreatePeer("SteamClient", $"127.0.0.1:{port}");

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            SteamNetworkingTransport serverTransport = UseIpAddressing(serverTransportManager, port);
            SteamNetworkingTransport clientTransport = UseIpAddressing(clientTransportManager, port);
            serverTransport.MaximumConnectionsPerSteamId = 2;
            TransportManager serverTransportManagerCore = serverTransport.CoreManager.TransportManager;
            TransportManager clientTransportManagerCore = clientTransport.CoreManager.TransportManager;

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 1);
            Assert.AreEqual(1, serverTransportManagerCore.ActiveConnections.Count, "The server does not hold the client.");

            SteamNetConnectionStatusChangedCallback_t statusChanged = default;
            statusChanged.m_hConn = new(UnissuedSteamConnectionHandle);
            statusChanged.m_info.m_hListenSocket = (HSteamListenSocket)typeof(SteamNetworkingServerSocket).GetField("_listenSocket", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(serverTransport.ServerSocket);
            statusChanged.m_info.m_eState = ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected;
            statusChanged.m_info.m_nFlags = Constants.k_nSteamNetworkConnectionInfoFlags_Unauthenticated;
            statusChanged.m_info.m_identityRemote.SetSteamID64(ownSteamId);

            typeof(SteamNetworkingServerSocket).GetMethod("OnConnectionStatusChanged", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(serverTransport.ServerSocket, new object[] { statusChanged });

            // An admitted arrival would be applied on the server's next receive pass.
            yield return new WaitForSecondsRealtime(0.5f);

            Assert.AreEqual(1, serverTransportManagerCore.ActiveConnections.Count, "The server admitted a connection Steam could not authenticate.");

            yield return AssertProbesCrossEachWay(serverTransport, clientTransport, value: 71);
        }

        /// <summary>
        /// A client that vanishes without closing its link, as a crashed game does, holds its account's place only until Steam times the
        /// link out: the same account is refused until then and joins after, and how long that took is logged.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        /// <remarks>
        /// The crashing client reaches the server through a <see cref="SteamUdpRelay"/> that is cut at the crash, so nothing it sends
        /// afterwards, its close included, arrives. The returning client signs in to the same account and dials the server directly.
        /// </remarks>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator SteamIdCap_ACrashedClientsLink_HoldsItsPlaceOnlyUntilSteamTimesItOut()
        {
            const float ReturnWaitSeconds = 40f;
            const float RetryIntervalSeconds = 1f;
            const float TimeoutMarginSeconds = 5f;

            ushort port = ProbeFreeUdpPort();

            SteamUdpRelay steamUdpRelay = NewInactiveObject("SteamUdpRelay").AddComponent<SteamUdpRelay>();
            steamUdpRelay.Begin(port);

            UnityTransportManager serverTransportManager = CreatePeer("SteamServer", string.Empty);
            UnityTransportManager crashingClientTransportManager = CreatePeer("SteamCrashingClient", $"127.0.0.1:{steamUdpRelay.Port}");
            UnityTransportManager returningClientTransportManager = CreatePeer("SteamReturningClient", $"127.0.0.1:{port}");

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(crashingClientTransportManager.EnsureAddedAsync());
            yield return WaitForTask(returningClientTransportManager.EnsureAddedAsync());

            SteamNetworkingTransport serverTransport = UseIpAddressing(serverTransportManager, port);
            SteamNetworkingTransport crashingClientTransport = UseIpAddressing(crashingClientTransportManager, port);
            SteamNetworkingTransport returningClientTransport = UseIpAddressing(returningClientTransportManager, port);
            TransportManager serverTransportManagerCore = serverTransport.CoreManager.TransportManager;
            TransportManager crashingClientTransportManagerCore = crashingClientTransport.CoreManager.TransportManager;
            TransportManager returningClientTransportManagerCore = returningClientTransport.CoreManager.TransportManager;

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitForTask(crashingClientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(crashingClientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 1);
            Assert.AreEqual(1, serverTransportManagerCore.ActiveConnections.Count, "The server does not hold the client before it crashes.");

            Connection crashedClientConnection = null;
            float releasedSeconds = -1f;

            foreach (Connection activeConnection in serverTransportManagerCore.ActiveConnections)
                crashedClientConnection = activeConnection;

            // Compared while the departure is raised, before the pool can hand the instance to the returning client.
            void OnClientDisconnecting(Connection connection)
            {
                if (ReferenceEquals(connection, crashedClientConnection))
                    releasedSeconds = Time.realtimeSinceStartup;
            }

            serverTransport.CoreManager.ServerManager.ClientDisconnecting += OnClientDisconnecting;

            steamUdpRelay.Cut();
            Object.Destroy(crashingClientTransportManager.gameObject);

            float crashedSeconds = Time.realtimeSinceStartup;
            uint refusedAttemptCount = 0;

            // Each refused attempt logs that the client could not connect.
            LogAssert.ignoreFailingMessages = true;

            while (!IsClientAuthenticated(returningClientTransportManagerCore) && Time.realtimeSinceStartup < crashedSeconds + ReturnWaitSeconds)
            {
                yield return WaitForTask(returningClientTransportManager.StartClientAsync());
                yield return WaitUntil(() => IsClientAuthenticated(returningClientTransportManagerCore), RetryIntervalSeconds);

                if (!IsClientAuthenticated(returningClientTransportManagerCore) && releasedSeconds < 0f)
                    refusedAttemptCount++;
            }

            LogAssert.ignoreFailingMessages = false;

            float joinedSeconds = Time.realtimeSinceStartup;
            int connectedTimeoutMilliseconds = GetSteamsGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_TimeoutConnected);

            // A client let in at once leaves the crashed link to time out on its own, which the measurement still waits for.
            yield return WaitUntil(() => releasedSeconds >= 0f, connectedTimeoutMilliseconds / 1000f + TimeoutMarginSeconds);

            serverTransport.CoreManager.ServerManager.ClientDisconnecting -= OnClientDisconnecting;

            Debug.Log($"[SteamIdCap] A crashed client's link held its account's place for [{releasedSeconds - crashedSeconds:F1}] s against Steam's connected timeout of [{connectedTimeoutMilliseconds}] ms. The same account was refused [{refusedAttemptCount}] times and joined [{joinedSeconds - crashedSeconds:F1}] s after the crash.");

            Assert.Greater(refusedAttemptCount, 0u, "The same account joined while the crashed client's link still held its place.");
            Assert.GreaterOrEqual(releasedSeconds, 0f, "The server never let the crashed client's link go.");
            Assert.LessOrEqual(releasedSeconds - crashedSeconds, connectedTimeoutMilliseconds / 1000f + TimeoutMarginSeconds, "The crashed client's link held its place well past Steam's connected timeout.");
            Assert.IsTrue(IsClientAuthenticated(returningClientTransportManagerCore), "The same account never joined once the crashed client's link was let go.");

            yield return AssertProbesCrossEachWay(serverTransport, returningClientTransport, value: 61);
        }

        /// <summary>
        /// Creates an active peer with a core manager, transport manager and Steam transport component, its transport not yet added.
        /// </summary>
        /// <param name="objectName">The peer's object name.</param>
        /// <param name="remoteAddress">Where its client connects, empty for a server.</param>
        /// <returns>Its transport manager.</returns>
        private UnityTransportManager CreatePeer(string objectName, string remoteAddress)
        {
            GameObject peerObject = NewInactiveObject(objectName);
            peerObject.AddComponent<UnityCoreManager>();
            UnityTransportManager unityTransportManager = peerObject.AddComponent<UnityTransportManager>();
            peerObject.AddComponent<SteamTransport>().RemoteAddress = remoteAddress;
            peerObject.SetActive(true);

            return unityTransportManager;
        }

        /// <summary>
        /// Returns the send latency Steam simulates for the whole process on the player's interface, in milliseconds.
        /// </summary>
        private static int GetSteamsSimulatedLatencyMilliseconds() => GetSteamsGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_FakePacketLag_Send);

        /// <summary>
        /// Returns one of Steam's whole-number settings for the whole process on the player's interface.
        /// </summary>
        /// <param name="configValue">The setting.</param>
        /// <returns>Its value.</returns>
        private static int GetSteamsGlobalInt32(ESteamNetworkingConfigValue configValue)
        {
            System.IntPtr valuePointer = Marshal.AllocHGlobal(sizeof(int));

            try
            {
                ulong valueBytes = sizeof(int);
                SteamNetworkingUtils.GetConfigValue(configValue, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global, System.IntPtr.Zero, out ESteamNetworkingConfigDataType _, valuePointer, ref valueBytes);

                return Marshal.ReadInt32(valuePointer);
            }
            finally
            {
                Marshal.FreeHGlobal(valuePointer);
            }
        }

        /// <summary>
        /// Sends a probe from a client to its server and one back to that client, and fails the case unless both arrive.
        /// </summary>
        /// <param name="serverTransport">The server's transport.</param>
        /// <param name="clientTransport">The client's transport.</param>
        /// <param name="value">The value the client sends; the server answers with the next one.</param>
        /// <returns>The coroutine doing the exchange.</returns>
        /// <remarks>The server answers over the Connection the probe arrived on, so only that client is sent to.</remarks>
        private static IEnumerator AssertProbesCrossEachWay(SteamNetworkingTransport serverTransport, SteamNetworkingTransport clientTransport, byte value)
        {
            byte answerValue = (byte)(value + 1);
            int serverReceivedValue = -1;
            int clientReceivedValue = -1;

            void OnServerReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage)
            {
                serverReceivedValue = steamProbeMessage.Value;
                messageContext.SenderConnection.SendMessage(Channel.Reliable, new SteamProbeMessage { Value = answerValue });
            }

            void OnClientReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage) => clientReceivedValue = steamProbeMessage.Value;

            serverTransport.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnServerReceivedProbe);
            clientTransport.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);

            clientTransport.CoreManager.TransportManager.TryGetServerConnection(out Connection serverConnection);
            serverConnection.SendMessage(Channel.Reliable, new SteamProbeMessage { Value = value });

            yield return WaitUntil(() => serverReceivedValue == value && clientReceivedValue == answerValue);

            serverTransport.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnServerReceivedProbe);
            clientTransport.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);

            Assert.AreEqual((int)value, serverReceivedValue, "The client's probe never reached the server.");
            Assert.AreEqual((int)answerValue, clientReceivedValue, "The server's answer never reached the client.");
        }

        /// <summary>
        /// Returns whether Steam's measured ping on a server's link to a client has reached a figure, sending a little on it so the
        /// measurement keeps moving.
        /// </summary>
        private static bool IsPingAtLeast(SteamNetworkingTransport serverTransport, Connection connection, int pingMilliseconds)
        {
            connection.SendMessage(Channel.Reliable, new SteamProbeMessage { Value = 1 });

            return serverTransport.TryGetLinkStatus(connection, out SteamLinkStatus linkStatus) && linkStatus.PingMilliseconds >= pingMilliseconds;
        }

        /// <summary>
        /// Returns whether the manager's own client is connected and authenticated.
        /// </summary>
        private static bool IsClientAuthenticated(TransportManager transportManager) =>
            transportManager.TryGetLocalClientConnection(out Connection localClientConnection) && localClientConnection.LocalState is LocalConnectionState.Connected && localClientConnection.IsAuthenticated;

        /// <summary>
        /// Returns whether the server's roster holds this very Connection.
        /// </summary>
        private static bool IsHeldByServer(TransportManager transportManager, Connection connection)
        {
            foreach (Connection activeConnection in transportManager.ActiveConnections)
            {
                if (ReferenceEquals(activeConnection, connection))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns a UDP port that was free a moment ago.
        /// </summary>
        private static ushort ProbeFreeUdpPort()
        {
            using UdpClient udpClient = new(new IPEndPoint(IPAddress.Loopback, 0));

            return (ushort)((IPEndPoint)udpClient.Client.LocalEndPoint).Port;
        }

        /// <summary>
        /// Addresses an added peer's transport by IP on the given port.
        /// </summary>
        /// <param name="unityTransportManager">The peer's transport manager, its transport already added.</param>
        /// <param name="port">The port the peer listens on, or connects to when its address names none.</param>
        /// <returns>The peer's transport.</returns>
        private static SteamNetworkingTransport UseIpAddressing(UnityTransportManager unityTransportManager, ushort port)
        {
            SteamNetworkingTransport steamNetworkingTransport = (SteamNetworkingTransport)unityTransportManager.GetComponent<UnityCoreManager>().CoreManager.TransportManager.Transports[0];
            steamNetworkingTransport.Addressing = SteamAddressing.IpAddress;
            steamNetworkingTransport.Configuration.Port = port;

            return steamNetworkingTransport;
        }

        /// <summary>
        /// Creates an inactive object the teardown destroys.
        /// </summary>
        private GameObject NewInactiveObject(string objectName)
        {
            GameObject createdObject = new(objectName);
            createdObject.SetActive(false);

            _createdObjects.Add(createdObject);

            return createdObject;
        }

        /// <summary>
        /// Waits until the task completes, or the wait runs out.
        /// </summary>
        private static IEnumerator WaitForTask(Task task) => WaitUntil(() => task.IsCompleted);

        /// <summary>
        /// Waits until the condition holds, or the wait runs out.
        /// </summary>
        private static IEnumerator WaitUntil(System.Func<bool> condition) => WaitUntil(condition, MaximumWaitSeconds);

        /// <summary>
        /// Waits until the condition holds, or the given wait runs out.
        /// </summary>
        private static IEnumerator WaitUntil(System.Func<bool> condition, float maximumWaitSeconds)
        {
            float deadlineSeconds = Time.realtimeSinceStartup + maximumWaitSeconds;

            while (!condition() && Time.realtimeSinceStartup < deadlineSeconds)
                yield return null;
        }
    }
}
#endif
