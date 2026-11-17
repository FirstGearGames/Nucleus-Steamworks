#if UNITY_INCLUDE_TESTS
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
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
        /// A client dialling by IP a port where nothing listens gives up when the transport's connecting timeout runs out, which Steam
        /// enforces, rather than after Steam's own.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator RemoteClient_ByIpAddress_ToAPortWithNothingListening_GivesUpAfterTheConnectingTimeout()
        {
            const ushort ConnectingTimeoutSeconds = 2;

            GameObject clientObject = NewInactiveObject("SteamClient");
            UnityCoreManager clientCoreManager = clientObject.AddComponent<UnityCoreManager>();
            UnityTransportManager clientTransportManager = clientObject.AddComponent<UnityTransportManager>();
            SteamTransport clientSteamTransport = clientObject.AddComponent<SteamTransport>();
            clientSteamTransport.RemoteAddress = $"127.0.0.1:{ProbeFreeUdpPort()}";
            clientObject.SetActive(true);

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            TransportManager clientTransportManagerCore = clientCoreManager.CoreManager.TransportManager;
            SteamNetworkingTransport clientTransport = (SteamNetworkingTransport)clientTransportManagerCore.Transports[0];
            clientTransport.Addressing = SteamAddressing.IpAddress;
            clientTransport.Configuration.ConnectingTimeoutSeconds = ConnectingTimeoutSeconds;

            LogAssert.ignoreFailingMessages = true;

            float startSeconds = Time.realtimeSinceStartup;
            Task clientStartTask = clientTransportManager.StartClientAsync();

            yield return WaitForTask(clientStartTask);

            float elapsedSeconds = Time.realtimeSinceStartup - startSeconds;

            LogAssert.ignoreFailingMessages = false;

            Assert.IsTrue(clientStartTask.IsCompleted, "The client was still connecting when the wait ran out.");
            Assert.IsFalse(IsClientAuthenticated(clientTransportManagerCore), "The client connected to a port where nothing listens.");
            Assert.That(elapsedSeconds, Is.InRange(ConnectingTimeoutSeconds - 0.5f, ConnectingTimeoutSeconds + 3f), $"The client gave up after [{elapsedSeconds:F2}] s rather than about [{ConnectingTimeoutSeconds}] s.");
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
        private static IEnumerator WaitUntil(System.Func<bool> condition)
        {
            float deadlineSeconds = Time.realtimeSinceStartup + MaximumWaitSeconds;

            while (!condition() && Time.realtimeSinceStartup < deadlineSeconds)
                yield return null;
        }
    }
}
#endif
