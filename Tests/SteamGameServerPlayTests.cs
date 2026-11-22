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
using Nucleus.Integrations.Newfarm;
using Nucleus.Integrations.Unity.Managers.Core;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Integrations.Unity.Transports;
using Nucleus.Managers.Core;
using Nucleus.Managers.Messages;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nucleus.Integrations.Unity.Tests
{
    /// <summary>
    /// Live coverage of what the Steam transport does for a dedicated server: Steam auth session tickets and fake IP addresses, against a
    /// Steam game server this fixture logs on anonymously beside the Steam client running on this machine.
    /// </summary>
    /// <remarks>
    /// Needs the Steam client running and signed in, a <c>steam_appid.txt</c> holding an app id (480, Spacewar, for testing) in the project
    /// folder, and Steam reachable for the game server's anonymous log on. Without them every case is ignored rather than failed. The
    /// game server is a different Steam identity from the signed-in player, so a client in the same process reaches it the way a player
    /// reaches a dedicated server.
    /// </remarks>
    public class SteamGameServerPlayTests
    {
        /// <summary>
        /// How long a case waits on Steam or the engine before calling it a failure.
        /// </summary>
        private const float MaximumWaitSeconds = 30f;

        /// <summary>
        /// How long Steam is given to grant a fake IP address, which it retries for some time before giving up.
        /// </summary>
        private const float FakeIpWaitSeconds = 90f;

        /// <summary>
        /// Everything a case created, destroyed after it.
        /// </summary>
        private readonly List<GameObject> _createdObjects = new();

        /// <summary>
        /// Whether this fixture initialised Steam, and so shuts it down.
        /// </summary>
        private bool _isSteamInitialized;

        /// <summary>
        /// Whether this fixture initialised the game server, and so shuts it down.
        /// </summary>
        private bool _isGameServerInitialized;

        /// <summary>
        /// Initialises Steam and a game server once for the fixture, or ignores every case when either cannot be.
        /// </summary>
        /// <remarks>The game server asks for its fake IP address before logging on, as Steam requires of a game server.</remarks>
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _isSteamInitialized = SteamAPI.Init();

            if (!_isSteamInitialized)
                Assert.Ignore("Steam could not be initialised. Run the Steam client and put a steam_appid.txt holding an app id in the project folder.");

            _isGameServerInitialized = GameServer.Init(unIP: 0, usGamePort: ProbeFreeUdpPort(), usQueryPort: ProbeFreeUdpPort(), EServerMode.eServerModeAuthenticationAndSecure, pchVersionString: "1.0.0.0");

            if (!_isGameServerInitialized)
                Assert.Ignore("A Steam game server could not be initialised in this process.");

            SteamGameServer.SetDedicatedServer(bDedicated: true);
            SteamGameServerNetworkingSockets.BeginAsyncRequestFakeIP(nNumPorts: 1);
            SteamGameServer.LogOnAnonymous();
        }

        /// <summary>
        /// Logs the game server off and shuts it and Steam down, if this fixture started them.
        /// </summary>
        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (_isGameServerInitialized)
            {
                SteamGameServer.LogOff();
                GameServer.Shutdown();
            }

            if (_isSteamInitialized)
                SteamAPI.Shutdown();
        }

        /// <summary>
        /// Starts running Steam's callbacks, the game server's among them, and waits for the game server to be logged on.
        /// </summary>
        /// <returns>The coroutine doing the setup.</returns>
        [UnitySetUp]
        public IEnumerator SetUp()
        {
            SteamCallbackPump steamCallbackPump = NewInactiveObject("SteamCallbackPump").AddComponent<SteamCallbackPump>();
            steamCallbackPump.IsGameServerPumped = true;
            steamCallbackPump.gameObject.SetActive(true);

            yield return WaitUntil(() => SteamGameServer.BLoggedOn(), MaximumWaitSeconds);

            if (!SteamGameServer.BLoggedOn())
                Assert.Ignore("The Steam game server could not log on anonymously.");
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
        /// A client whose auth session ticket Steam confirms is admitted by a game server, with its Steam ID as its identity.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator TicketAuthentication_OnAGameServer_AdmitsAClientSteamVouchesFor()
        {
            ulong ownSteamId = SteamUser.GetSteamID().m_SteamID;
            ushort port = ProbeFreeUdpPort();

            UnityTransportManager serverTransportManager = CreatePeer("SteamGameServer", string.Empty);
            UnityTransportManager clientTransportManager = CreatePeer("SteamClient", $"127.0.0.1:{port}");

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            SteamNetworkingTransport serverTransport = ConfigureTransport(serverTransportManager, isGameServer: true, SteamAddressing.IpAddress, port);
            SteamNetworkingTransport clientTransport = ConfigureTransport(clientTransportManager, isGameServer: false, SteamAddressing.IpAddress, port);

            SteamTicketAuthenticator serverAuthenticator = new();
            serverAuthenticator.Initialize(serverTransport.CoreManager, serverTransport);
            SteamTicketAuthenticator clientAuthenticator = new();
            clientAuthenticator.Initialize(clientTransport.CoreManager, clientTransport);

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitForTask(clientTransportManager.StartClientAsync());

            TransportManager serverTransportManagerCore = serverTransport.CoreManager.TransportManager;
            TransportManager clientTransportManagerCore = clientTransport.CoreManager.TransportManager;

            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore), MaximumWaitSeconds);
            Assert.IsTrue(IsClientAuthenticated(clientTransportManagerCore), "The game server never admitted a client Steam vouches for.");

            Connection remoteClientConnection = null;

            foreach (Connection activeConnection in serverTransportManagerCore.ActiveConnections)
                remoteClientConnection = activeConnection;

            Assert.IsNotNull(remoteClientConnection, "The game server holds no client.");
            Assert.AreEqual(ownSteamId.ToString(), remoteClientConnection.Identity, "The admitted client's identity is not its Steam ID.");

            serverAuthenticator.Deinitialize();
            clientAuthenticator.Deinitialize();
        }

        /// <summary>
        /// A client the admission filter refuses is never admitted, and the filter is asked with the client's Steam ID before any ticket
        /// is checked.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator TicketAuthentication_ClientRefusedByTheAdmissionFilter_IsNeverAdmitted()
        {
            ulong ownSteamId = SteamUser.GetSteamID().m_SteamID;
            ulong askedSteamId = SteamNetworkingTransport.UnsetSteamId;
            ushort port = ProbeFreeUdpPort();

            bool FilterClient(Connection connection, ulong steamId)
            {
                askedSteamId = steamId;

                return false;
            }

            UnityTransportManager serverTransportManager = CreatePeer("SteamGameServer", string.Empty);
            UnityTransportManager clientTransportManager = CreatePeer("SteamClient", $"127.0.0.1:{port}");

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            SteamNetworkingTransport serverTransport = ConfigureTransport(serverTransportManager, isGameServer: true, SteamAddressing.IpAddress, port);
            SteamNetworkingTransport clientTransport = ConfigureTransport(clientTransportManager, isGameServer: false, SteamAddressing.IpAddress, port);

            SteamTicketAuthenticator serverAuthenticator = new() { ClientAdmissionFilter = FilterClient };
            serverAuthenticator.Initialize(serverTransport.CoreManager, serverTransport);
            SteamTicketAuthenticator clientAuthenticator = new();
            clientAuthenticator.Initialize(clientTransport.CoreManager, clientTransport);

            TransportManager serverTransportManagerCore = serverTransport.CoreManager.TransportManager;
            TransportManager clientTransportManagerCore = clientTransport.CoreManager.TransportManager;

            LogAssert.ignoreFailingMessages = true;

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => askedSteamId != SteamNetworkingTransport.UnsetSteamId && serverTransportManagerCore.ActiveConnections.Count == 0, MaximumWaitSeconds);
            yield return new WaitForSecondsRealtime(1f);

            LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual(ownSteamId, askedSteamId, "The admission filter was not asked with the client's Steam ID.");
            Assert.IsFalse(IsClientAuthenticated(clientTransportManagerCore), "A client the admission filter refused was admitted.");
            Assert.AreEqual(0, serverTransportManagerCore.ActiveConnections.Count, "The game server still holds a client the admission filter refused.");

            serverAuthenticator.Deinitialize();
            clientAuthenticator.Deinitialize();
        }

        /// <summary>
        /// Ticket authentication records the account that owns the game license a client plays on, readable from the moment the server
        /// announces the client, so a ban check on <c>ClientAuthenticated</c> can use it, and forgets it when the client leaves. For an
        /// account that owns the game, as this machine's does, the owner is the client's own Steam ID.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        /// <remarks>The announcement is watched by a handler subscribed before the transport's own, so the owner cannot be recorded by it.</remarks>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator TicketAuthentication_RecordsTheLicenseOwner_BeforeTheClientIsAnnounced()
        {
            ulong ownSteamId = SteamUser.GetSteamID().m_SteamID;
            ulong ownerSteamIdWhenAnnounced = SteamNetworkingTransport.UnsetSteamId;
            ushort port = ProbeFreeUdpPort();
            SteamNetworkingTransport serverTransport = null;

            void OnClientAuthenticated(Connection connection) => serverTransport.TryGetOwnerSteamId(connection, out ownerSteamIdWhenAnnounced);

            UnityTransportManager serverTransportManager = CreatePeer("SteamGameServer", string.Empty);
            UnityTransportManager clientTransportManager = CreatePeer("SteamClient", $"127.0.0.1:{port}");
            CoreManager serverCoreManager = serverTransportManager.GetComponent<UnityCoreManager>().CoreManager;

            serverCoreManager.ServerManager.ClientAuthenticated += OnClientAuthenticated;

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            serverTransport = ConfigureTransport(serverTransportManager, isGameServer: true, SteamAddressing.IpAddress, port);
            SteamNetworkingTransport clientTransport = ConfigureTransport(clientTransportManager, isGameServer: false, SteamAddressing.IpAddress, port);

            SteamTicketAuthenticator serverAuthenticator = new();
            serverAuthenticator.Initialize(serverTransport.CoreManager, serverTransport);
            SteamTicketAuthenticator clientAuthenticator = new();
            clientAuthenticator.Initialize(clientTransport.CoreManager, clientTransport);

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitForTask(clientTransportManager.StartClientAsync());

            TransportManager serverTransportManagerCore = serverTransport.CoreManager.TransportManager;
            TransportManager clientTransportManagerCore = clientTransport.CoreManager.TransportManager;

            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore), MaximumWaitSeconds);
            Assert.IsTrue(IsClientAuthenticated(clientTransportManagerCore), "The game server never admitted a client Steam vouches for.");

            Connection remoteClientConnection = null;

            foreach (Connection activeConnection in serverTransportManagerCore.ActiveConnections)
                remoteClientConnection = activeConnection;

            Assert.IsNotNull(remoteClientConnection, "The game server holds no client.");
            Assert.AreEqual(ownSteamId.ToString(), remoteClientConnection.Identity, "The admitted client's identity is not its Steam ID.");

            uint remoteClientConnectionId = remoteClientConnection.Id;

            Assert.AreEqual(ownSteamId, ownerSteamIdWhenAnnounced, "The license owner could not be read when the client was announced.");
            Assert.IsTrue(serverTransport.TryGetOwnerSteamId(remoteClientConnection, out ulong ownerSteamId), "The game server holds no license owner for its client.");
            Assert.AreEqual(ownSteamId, ownerSteamId, "The license owner of an account that owns the game is not its own Steam ID.");
            Assert.IsTrue(serverTransport.TryGetOwnerSteamId(remoteClientConnectionId, out ulong ownerSteamIdById) && ownerSteamIdById == ownSteamId, "The license owner could not be read by connection id.");

            int serverReceivedValue = -1;
            int clientReceivedValue = -1;

            void OnServerReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage) => serverReceivedValue = steamProbeMessage.Value;
            void OnClientReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage) => clientReceivedValue = steamProbeMessage.Value;

            serverTransport.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnServerReceivedProbe);
            clientTransport.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);

            clientTransportManagerCore.TryGetServerConnection(out Connection serverConnection);
            serverConnection.SendMessage(Channel.Reliable, new SteamProbeMessage { Value = 33 });
            remoteClientConnection.SendMessage(Channel.Reliable, new SteamProbeMessage { Value = 34 });

            yield return WaitUntil(() => serverReceivedValue == 33 && clientReceivedValue == 34, MaximumWaitSeconds);
            Assert.AreEqual(33, serverReceivedValue, "The client's message never reached the game server.");
            Assert.AreEqual(34, clientReceivedValue, "The game server's message never reached the client.");

            serverTransport.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnServerReceivedProbe);
            clientTransport.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);

            yield return WaitForTask(clientTransport.DisconnectLocalConnectionAsync(Invoker.Client));
            yield return WaitUntil(() => serverTransportManagerCore.ActiveConnections.Count == 0, MaximumWaitSeconds);
            Assert.AreEqual(0, serverTransportManagerCore.ActiveConnections.Count, "The game server still held the client after it left.");
            Assert.IsFalse(serverTransport.TryGetOwnerSteamId(remoteClientConnectionId, out ulong _), "The license owner outlived the client it was recorded for.");

            serverCoreManager.ServerManager.ClientAuthenticated -= OnClientAuthenticated;
            serverAuthenticator.Deinitialize();
            clientAuthenticator.Deinitialize();
        }

        /// <summary>
        /// A game server addressed by fake IP is granted an address, and a client reaches it there over Valve's relay network with data
        /// each way.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(240000)]
        public IEnumerator FakeIp_OnAGameServer_ClientReachesItAtTheGrantedAddress()
        {
            UnityTransportManager serverTransportManager = CreatePeer("SteamGameServer", string.Empty);
            UnityTransportManager clientTransportManager = CreatePeer("SteamClient", string.Empty);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            SteamNetworkingTransport serverTransport = ConfigureTransport(serverTransportManager, isGameServer: true, SteamAddressing.FakeIp, ProbeFreeUdpPort());
            SteamNetworkingTransport clientTransport = ConfigureTransport(clientTransportManager, isGameServer: false, SteamAddressing.FakeIp, ProbeFreeUdpPort());

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitUntil(() => serverTransport.ServerFakeAddress is not null, FakeIpWaitSeconds);

            if (serverTransport.ServerFakeAddress is null)
                Assert.Inconclusive("Steam granted the game server no fake IP address in time, so the client had nowhere to connect.");

            clientTransport.RemoteAddress = serverTransport.ServerFakeAddress;

            TransportManager serverTransportManagerCore = serverTransport.CoreManager.TransportManager;
            TransportManager clientTransportManagerCore = clientTransport.CoreManager.TransportManager;

            int serverReceivedValue = -1;
            int clientReceivedValue = -1;

            void OnServerReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage) => serverReceivedValue = steamProbeMessage.Value;
            void OnClientReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage) => clientReceivedValue = steamProbeMessage.Value;

            serverTransport.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnServerReceivedProbe);
            clientTransport.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 1, MaximumWaitSeconds);
            Assert.IsTrue(IsClientAuthenticated(clientTransportManagerCore), $"The client never reached the game server at its fake IP address [{serverTransport.ServerFakeAddress}].");

            clientTransportManagerCore.TryGetServerConnection(out Connection serverConnection);
            serverConnection.SendMessage(Channel.Reliable, new SteamProbeMessage { Value = 31 });
            serverTransport.CoreManager.ServerManager.SendMessage(Channel.Reliable, new SteamProbeMessage { Value = 32 });

            yield return WaitUntil(() => serverReceivedValue == 31 && clientReceivedValue == 32, MaximumWaitSeconds);
            Assert.AreEqual(31, serverReceivedValue, "The client's message never reached the game server over its fake IP link.");
            Assert.AreEqual(32, clientReceivedValue, "The game server's message never reached the client over its fake IP link.");

            serverTransport.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnServerReceivedProbe);
            clientTransport.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);
        }

        /// <summary>
        /// Under fake IP, a peer taking over a session names its fake IP address as where it is hosted, filed with IP addresses, and a
        /// survivor rejoins it there, as host migration does after a handover.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(240000)]
        public IEnumerator HostMigration_OnFakeIp_TheNewHostNamesItsFakeAddress_AndASurvivorJoinsIt()
        {
            UnityTransportManager serverTransportManager = CreatePeer("SteamGameServer", string.Empty);
            UnityTransportManager clientTransportManager = CreatePeer("SteamClient", string.Empty);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            SteamNetworkingTransport serverTransport = ConfigureTransport(serverTransportManager, isGameServer: true, SteamAddressing.FakeIp, ProbeFreeUdpPort());
            SteamNetworkingTransport clientTransport = ConfigureTransport(clientTransportManager, isGameServer: false, SteamAddressing.FakeIp, ProbeFreeUdpPort());

            SteamSessionHost hostingSessionHost = new(serverTransport);
            Task<SessionHostResult> hostingTask = hostingSessionHost.StartHostingAsync(timeoutMilliseconds: (uint)(FakeIpWaitSeconds * 1_000f));

            yield return WaitUntil(() => hostingTask.IsCompleted, FakeIpWaitSeconds);
            Assert.AreEqual(SessionHostResult.Success, hostingTask.Result, "The new host could not start hosting under fake IP.");
            Assert.IsNotNull(serverTransport.ServerFakeAddress, "Steam granted the new host no fake IP address.");
            Assert.AreEqual(serverTransport.ServerFakeAddress, hostingSessionHost.HostedCredential, "The new host did not name its fake IP address as where it is hosted.");
            Assert.AreEqual(SteamSessionHost.IpAddressAdapterTag, hostingSessionHost.AdapterTag, "A fake IP address was not filed with IP addresses.");

            SteamSessionHost joiningSessionHost = new(clientTransport);
            Task<SessionHostResult> joiningTask = joiningSessionHost.JoinAsync(hostingSessionHost.HostedCredential, timeoutMilliseconds: (uint)(MaximumWaitSeconds * 1_000f));

            yield return WaitUntil(() => joiningTask.IsCompleted, MaximumWaitSeconds);
            Assert.AreEqual(SessionHostResult.Success, joiningTask.Result, $"The survivor could not rejoin the new host at [{hostingSessionHost.HostedCredential}].");

            TransportManager clientTransportManagerCore = clientTransport.CoreManager.TransportManager;

            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore), MaximumWaitSeconds);
            Assert.IsTrue(IsClientAuthenticated(clientTransportManagerCore), "The survivor reached the new host but was never admitted.");
        }

        /// <summary>
        /// By default a link addressed by Steam ID or by fake IP shares no ICE candidates, the addresses a direct route is found through, so
        /// it can only ride Valve's relay and no player learns another's IP address: Steam reads the setting back as disabled on the
        /// server's listen socket and on the client's link, and a transport set up from code starts with relay only on as well.
        /// </summary>
        /// <param name="addressing">How the client reaches the game server.</param>
        /// <returns>The coroutine driving the case.</returns>
        /// <remarks>
        /// Both ends share this process, where Steam carries a link between them through memory whatever its settings, so the route proves
        /// nothing here; what each end opened with is read back instead.
        /// </remarks>
        [UnityTest]
        [Timeout(240000)]
        public IEnumerator RelayOnly_ByDefault_IsAppliedToBothEndsOfAPeerToPeerLink([Values(SteamAddressing.SteamId, SteamAddressing.FakeIp)] SteamAddressing addressing)
        {
            UnityTransportManager serverTransportManager = CreatePeer("SteamGameServer", string.Empty);
            UnityTransportManager clientTransportManager = CreatePeer("SteamClient", string.Empty);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            SteamNetworkingTransport serverTransport = ConfigureTransport(serverTransportManager, isGameServer: true, addressing, ProbeFreeUdpPort());
            SteamNetworkingTransport clientTransport = ConfigureTransport(clientTransportManager, isGameServer: false, addressing, ProbeFreeUdpPort());

            yield return WaitForTask(serverTransportManager.StartServerAsync());

            clientTransport.RemoteSteamId = serverTransport.ServerSteamId;

            // A fake IP link dials the address Steam grants the server instead, once it has.
            if (addressing is SteamAddressing.FakeIp)
            {
                yield return WaitUntil(() => serverTransport.ServerFakeAddress is not null, FakeIpWaitSeconds);

                if (serverTransport.ServerFakeAddress is null)
                    Assert.Inconclusive("Steam granted the game server no fake IP address in time, so the client had nowhere to connect.");

                clientTransport.RemoteAddress = serverTransport.ServerFakeAddress;
            }

            TransportManager serverTransportManagerCore = serverTransport.CoreManager.TransportManager;
            TransportManager clientTransportManagerCore = clientTransport.CoreManager.TransportManager;

            int serverReceivedValue = -1;
            int clientReceivedValue = -1;

            void OnServerReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage) => serverReceivedValue = steamProbeMessage.Value;
            void OnClientReceivedProbe(in MessageContext messageContext, SteamProbeMessage steamProbeMessage) => clientReceivedValue = steamProbeMessage.Value;

            serverTransport.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnServerReceivedProbe);
            clientTransport.CoreManager.MessageManager.RegisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientAuthenticated(clientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 1, MaximumWaitSeconds);
            Assert.IsTrue(IsClientAuthenticated(clientTransportManagerCore), $"The client never reached the game server by [{addressing}].");

            clientTransportManagerCore.TryGetServerConnection(out Connection serverConnection);
            serverConnection.SendMessage(Channel.Reliable, new SteamProbeMessage { Value = 35 });
            serverTransport.CoreManager.ServerManager.SendMessage(Channel.Reliable, new SteamProbeMessage { Value = 36 });

            yield return WaitUntil(() => serverReceivedValue == 35 && clientReceivedValue == 36, MaximumWaitSeconds);
            Assert.AreEqual(35, serverReceivedValue, $"The client's message never reached the game server by [{addressing}].");
            Assert.AreEqual(36, clientReceivedValue, $"The game server's message never reached the client by [{addressing}].");

            serverTransport.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnServerReceivedProbe);
            clientTransport.CoreManager.MessageManager.UnregisterMessageHandler<SteamProbeMessage>(OnClientReceivedProbe);

            HSteamListenSocket listenSocket = (HSteamListenSocket)typeof(SteamNetworkingServerSocket).GetField("_listenSocket", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(serverTransport.ServerSocket);
            HSteamNetConnection clientSteamConnection = (HSteamNetConnection)typeof(SteamNetworkingClientSocket).GetField("_steamConnection", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(clientTransport.ClientSocket);

            Assert.AreEqual(Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable, GetIceSetting(isGameServer: true, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_ListenSocket, listenSocket.m_HSteamListenSocket), $"The game server's listen socket lets Steam share ICE candidates by [{addressing}].");
            Assert.AreEqual(Constants.k_nSteamNetworkingConfig_P2P_Transport_ICE_Enable_Disable, GetIceSetting(isGameServer: false, ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Connection, clientSteamConnection.m_HSteamNetConnection), $"The client's link lets Steam share ICE candidates by [{addressing}].");
            Assert.IsTrue(new SteamNetworkingTransport().IsRelayOnlyEnabled, "A transport set up from code allows direct connections by default.");
        }

        /// <summary>
        /// Creates a peer with a core manager, transport manager and Steam transport component, and activates it.
        /// </summary>
        /// <param name="objectName">The peer's object name.</param>
        /// <param name="remoteAddress">Where its client connects, under IP or fake IP addressing.</param>
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
        /// Sets up the transport a peer's manager added.
        /// </summary>
        /// <param name="unityTransportManager">The peer's transport manager, its transport already added.</param>
        /// <param name="isGameServer">Whether its server half runs on the game server interface.</param>
        /// <param name="addressing">How it is addressed.</param>
        /// <param name="port">The port its server listens on under IP addressing.</param>
        /// <returns>The transport.</returns>
        private static SteamNetworkingTransport ConfigureTransport(UnityTransportManager unityTransportManager, bool isGameServer, SteamAddressing addressing, ushort port)
        {
            SteamNetworkingTransport steamNetworkingTransport = (SteamNetworkingTransport)unityTransportManager.GetComponent<UnityCoreManager>().CoreManager.TransportManager.Transports[0];
            steamNetworkingTransport.IsGameServer = isGameServer;
            steamNetworkingTransport.Addressing = addressing;
            steamNetworkingTransport.Configuration.Port = port;

            return steamNetworkingTransport;
        }

        /// <summary>
        /// Returns which ICE candidates Steam shares on a listen socket or a connection, as its
        /// <c>k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable</c> setting reads, set there or inherited.
        /// </summary>
        /// <param name="isGameServer">True for the game server interface.</param>
        /// <param name="scope">Whether <paramref name="handle"/> names a listen socket or a connection.</param>
        /// <param name="handle">The listen socket's or the connection's handle.</param>
        /// <returns>The setting.</returns>
        private static int GetIceSetting(bool isGameServer, ESteamNetworkingConfigScope scope, uint handle)
        {
            System.IntPtr valuePointer = Marshal.AllocHGlobal(sizeof(int));

            try
            {
                ulong valueBytes = sizeof(int);
                ESteamNetworkingConfigValue configValue = ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_P2P_Transport_ICE_Enable;

                if (isGameServer)
                    SteamGameServerNetworkingUtils.GetConfigValue(configValue, scope, (System.IntPtr)handle, out ESteamNetworkingConfigDataType _, valuePointer, ref valueBytes);
                else
                    SteamNetworkingUtils.GetConfigValue(configValue, scope, (System.IntPtr)handle, out ESteamNetworkingConfigDataType _, valuePointer, ref valueBytes);

                return Marshal.ReadInt32(valuePointer);
            }
            finally
            {
                Marshal.FreeHGlobal(valuePointer);
            }
        }

        /// <summary>
        /// Returns whether the manager's own client is connected and authenticated.
        /// </summary>
        private static bool IsClientAuthenticated(TransportManager transportManager) =>
            transportManager.TryGetLocalClientConnection(out Connection localClientConnection) && localClientConnection.LocalState is LocalConnectionState.Connected && localClientConnection.IsAuthenticated;

        /// <summary>
        /// Returns a UDP port that was free a moment ago.
        /// </summary>
        private static ushort ProbeFreeUdpPort()
        {
            using UdpClient udpClient = new(new IPEndPoint(IPAddress.Loopback, 0));

            return (ushort)((IPEndPoint)udpClient.Client.LocalEndPoint).Port;
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
        private static IEnumerator WaitForTask(Task task) => WaitUntil(() => task.IsCompleted, MaximumWaitSeconds);

        /// <summary>
        /// Waits until the condition holds, or the wait runs out.
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
