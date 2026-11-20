#if UNITY_INCLUDE_TESTS
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Nucleus.Connections;
using Nucleus.Integrations.Unity.Managers.Core;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Integrations.Unity.Transports;
using Nucleus.Managers.Transports;
using Nucleus.Transports;
using Steamworks;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nucleus.Integrations.Unity.Tests
{
    /// <summary>
    /// Live coverage of the Steam transport outliving Steam: a game that shuts Steam down while a server and a client are still up.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="SteamNetworkingTransportPlayTests"/> because its case shuts Steam down, which every other case needs up.
    /// Needs the Steam client running and signed in, and a <c>steam_appid.txt</c> in the project folder; without them the case is ignored.
    /// </remarks>
    public class SteamShutdownPlayTests
    {
        /// <summary>
        /// How long the case waits on Steam or the engine before calling it a failure.
        /// </summary>
        private const float MaximumWaitSeconds = 30f;

        /// <summary>
        /// Everything the case created, destroyed after it.
        /// </summary>
        private readonly List<GameObject> _createdObjects = new();

        /// <summary>
        /// Whether Steam is up and was started by this fixture, and so is shut down by it.
        /// </summary>
        private bool _isSteamInitialized;

        /// <summary>
        /// Initialises Steam for the fixture, or ignores the case when it cannot be.
        /// </summary>
        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _isSteamInitialized = SteamAPI.Init();

            if (!_isSteamInitialized)
                Assert.Ignore("Steam could not be initialised. Run the Steam client and put a steam_appid.txt holding an app id in the project folder.");
        }

        /// <summary>
        /// Shuts Steam down if the case left it up.
        /// </summary>
        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            if (_isSteamInitialized)
                SteamAPI.Shutdown();
        }

        /// <summary>
        /// Destroys everything the case created.
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

            yield return null;
        }

        /// <summary>
        /// Steam shut down under a running server and a connected IP client stops both, and stopping them again afterwards throws nothing.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(120000)]
        public IEnumerator SteamShutDown_WithServerAndClientUp_StopsBothWithoutThrowing()
        {
            GameObject callbackPumpObject = NewInactiveObject("SteamCallbackPump");
            callbackPumpObject.AddComponent<SteamCallbackPump>();
            callbackPumpObject.SetActive(true);

            ushort port = ProbeFreeUdpPort();
            UnityCoreManager serverCoreManager = NewPeer("SteamServer", remoteAddress: string.Empty, out UnityTransportManager serverTransportManager);
            UnityCoreManager clientCoreManager = NewPeer("SteamClient", $"127.0.0.1:{port}", out UnityTransportManager clientTransportManager);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());
            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            UseIpAddressing(serverCoreManager, port);
            UseIpAddressing(clientCoreManager, port);

            TransportManager serverTransportManagerCore = serverCoreManager.CoreManager.TransportManager;
            TransportManager clientTransportManagerCore = clientCoreManager.CoreManager.TransportManager;

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitUntil(() => serverTransportManagerCore.IsServerStarted);
            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => IsClientConnected(clientTransportManagerCore) && serverTransportManagerCore.ActiveConnections.Count == 1);
            Assert.IsTrue(IsClientConnected(clientTransportManagerCore), "The client never connected over Steam.");

            // The pump goes first: running Steam's callbacks after a shutdown throws in Steamworks.NET, whatever the transport does.
            Object.DestroyImmediate(callbackPumpObject);
            SteamAPI.Shutdown();
            _isSteamInitialized = false;

            LogAssert.Expect(LogType.Warning, new Regex("Steam was shut down while the server was running, so the server stopped\\."));
            LogAssert.Expect(LogType.Warning, new Regex("Steam was shut down while the client was connected, so the client stopped\\."));

            yield return WaitUntil(() => !serverTransportManagerCore.IsServerStarted && !IsClientConnected(clientTransportManagerCore));
            Assert.IsFalse(serverTransportManagerCore.IsServerStarted, "The server was still running after Steam shut down.");
            Assert.IsFalse(IsClientConnected(clientTransportManagerCore), "The client was still connected after Steam shut down.");

            Task serverStopTask = serverTransportManagerCore.Transports[0].DisconnectLocalConnectionAsync(Invoker.Server);
            Task clientStopTask = clientTransportManagerCore.Transports[0].DisconnectLocalConnectionAsync(Invoker.Client);

            yield return WaitForTask(serverStopTask);
            yield return WaitForTask(clientStopTask);
            Assert.IsNull(serverStopTask.Exception, "Stopping the server after Steam shut down threw.");
            Assert.IsNull(clientStopTask.Exception, "Stopping the client after Steam shut down threw.");
        }

        /// <summary>
        /// Returns whether the manager's own client is connected.
        /// </summary>
        /// <param name="transportManager">The peer's transport manager.</param>
        /// <returns>True while the peer's own client is connected.</returns>
        private static bool IsClientConnected(TransportManager transportManager) =>
            transportManager.TryGetLocalClientConnection(out Connection localClientConnection) && localClientConnection.LocalState is LocalConnectionState.Connected;

        /// <summary>
        /// Addresses an added peer's transport by IP on the given port.
        /// </summary>
        /// <param name="unityCoreManager">The peer, its transport already added.</param>
        /// <param name="port">The port the peer listens on or connects to.</param>
        private static void UseIpAddressing(UnityCoreManager unityCoreManager, ushort port)
        {
            SteamNetworkingTransport steamNetworkingTransport = (SteamNetworkingTransport)unityCoreManager.CoreManager.TransportManager.Transports[0];
            steamNetworkingTransport.Addressing = SteamAddressing.IpAddress;
            steamNetworkingTransport.Configuration.Port = port;
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
        /// Creates an active peer with a Steam transport, not yet added, that the teardown destroys.
        /// </summary>
        /// <param name="objectName">The name of the peer's object.</param>
        /// <param name="remoteAddress">The address the peer's client connects to, empty for a server.</param>
        /// <param name="unityTransportManager">The peer's transport manager.</param>
        /// <returns>The peer's core manager.</returns>
        private UnityCoreManager NewPeer(string objectName, string remoteAddress, out UnityTransportManager unityTransportManager)
        {
            GameObject peerObject = NewInactiveObject(objectName);
            UnityCoreManager unityCoreManager = peerObject.AddComponent<UnityCoreManager>();
            unityTransportManager = peerObject.AddComponent<UnityTransportManager>();
            peerObject.AddComponent<SteamTransport>().RemoteAddress = remoteAddress;
            peerObject.SetActive(true);

            return unityCoreManager;
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
