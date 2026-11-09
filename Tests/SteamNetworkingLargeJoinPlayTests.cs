#if UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Nucleus.Connections;
using Nucleus.Integrations.Unity.Managers.Core;
using Nucleus.Integrations.Unity.Managers.Transports;
using Nucleus.Integrations.Unity.Transports;
using Nucleus.Managers.Core;
using Nucleus.Managers.Server;
using Nucleus.Systems;
using Steamworks;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nucleus.Integrations.Unity.Tests
{
    /// <summary>
    /// Live coverage of a client joining late over a world of wide values through Steam's networking library, against the Steam client
    /// running on this machine.
    /// </summary>
    /// <remarks>
    /// Needs the Steam client running and signed in, and a <c>steam_appid.txt</c> holding an app id (480, Spacewar, for testing) in the
    /// project folder. Without them every case is ignored rather than failed. The client reaches the server by IP, since a peer cannot
    /// reach its own Steam ID through the relay network. At Steam's own send rate, 256 KiB a second, each 64 KiB value takes a quarter
    /// of a second to arrive, several ticks, which a client has to wait out rather than report lost.
    /// </remarks>
    public class SteamNetworkingLargeJoinPlayTests
    {
        /// <summary>
        /// How many systems the server holds before the client joins.
        /// </summary>
        private const int SystemCount = 50;
        /// <summary>
        /// The width of each system's string, in kibibytes.
        /// </summary>
        private const int LabelKibibytes = 64;
        /// <summary>
        /// How many times the state's own size the server may send for the join.
        /// </summary>
        private const double MaximumSentToStateRatio = 1.5d;
        /// <summary>
        /// How long a join may take, in seconds, many times what the defaults need to carry the state once.
        /// </summary>
        private const float JoinSeconds = 60f;
        /// <summary>
        /// The transport's send rate that keeps Steam's own, 256 KiB a second.
        /// </summary>
        private const uint SteamsOwnSendRate = 0;
        /// <summary>
        /// An object pacing budget under Steam's own send rate at the default tick rate of thirty, with room for the ordinary traffic beside it.
        /// </summary>
        private const byte BudgetUnderSteamsOwnRateKilobytes = 7;
        /// <summary>
        /// How long a case waits on Steam or the engine for anything other than the join, in seconds.
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
                    UnityEngine.Object.Destroy(createdObject);
            }

            _createdObjects.Clear();

            yield return new WaitForSecondsRealtime(1f);
        }

        /// <summary>
        /// A late join over a world of 64 KiB values arrives whole, having been sent little more than the state.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator LateJoin_OverWideValues_AtTheDefaults_IsSentTheStateAboutOnce()
        {
            JoinResult joinResult = new();

            yield return RunLateJoin(ServerManager.DefaultMaximumObjectKilobytesPerTick, SteamNetworkingTransport.DefaultSendRateKilobytesPerSecond, joinResult);

            Assert.IsTrue(joinResult.IsComplete, $"The client held [{joinResult.HeldCount}] of [{SystemCount}] values after [{JoinSeconds}] s. {joinResult}");
            Assert.LessOrEqual(joinResult.SentToStateRatio, MaximumSentToStateRatio, joinResult.ToString());
        }

        /// <summary>
        /// The same join at Steam's own send rate, paced under it, arrives whole having been sent little more than the state, though
        /// each value takes several ticks to arrive.
        /// </summary>
        /// <returns>The coroutine driving the case.</returns>
        [UnityTest]
        [Timeout(180000)]
        public IEnumerator LateJoin_OverWideValues_AtSteamsOwnRate_IsSentTheStateAboutOnce()
        {
            JoinResult joinResult = new();

            yield return RunLateJoin(BudgetUnderSteamsOwnRateKilobytes, SteamsOwnSendRate, joinResult);

            Assert.IsTrue(joinResult.IsComplete, $"The client held [{joinResult.HeldCount}] of [{SystemCount}] values after [{JoinSeconds}] s. {joinResult}");
            Assert.LessOrEqual(joinResult.SentToStateRatio, MaximumSentToStateRatio, joinResult.ToString());
        }

        /// <summary>
        /// Starts a Steam server holding the world, joins a client to it by IP, and waits until the client holds every value or the join
        /// runs out of time, filling in what it cost.
        /// </summary>
        /// <param name="maximumObjectKilobytesPerTick">The server's object pacing budget.</param>
        /// <param name="sendRateKilobytesPerSecond">The rate Steam sends at both ways, or zero for Steam's own.</param>
        /// <param name="joinResult">Filled in with what the join cost.</param>
        /// <returns>The coroutine driving the join.</returns>
        private IEnumerator RunLateJoin(byte maximumObjectKilobytesPerTick, uint sendRateKilobytesPerSecond, JoinResult joinResult)
        {
            ushort port = ProbeFreeUdpPort();

            GameObject serverObject = NewInactiveObject("SteamServer");
            UnityCoreManager serverCoreManager = serverObject.AddComponent<UnityCoreManager>();
            UnityTransportManager serverTransportManager = serverObject.AddComponent<UnityTransportManager>();
            serverObject.AddComponent<SteamTransport>();
            serverObject.SetActive(true);

            yield return WaitForTask(serverTransportManager.EnsureAddedAsync());

            CoreManager server = serverCoreManager.CoreManager;
            SteamNetworkingTransport serverTransport = (SteamNetworkingTransport)server.TransportManager.Transports[0];
            serverTransport.Addressing = SteamAddressing.IpAddress;
            serverTransport.Configuration.Port = port;
            serverTransport.SendRateKilobytesPerSecond = sendRateKilobytesPerSecond;
            server.ServerManager.MaximumObjectKilobytesPerTick = maximumObjectKilobytesPerTick;

            yield return WaitForTask(serverTransportManager.StartServerAsync());
            yield return WaitUntil(() => server.TransportManager.IsServerStarted, MaximumWaitSeconds);
            Assert.IsTrue(server.TransportManager.IsServerStarted, $"The server never started listening on port [{port}].");

            uint[] systemIds = new uint[SystemCount];
            string[] labels = new string[SystemCount];
            System.Random random = new(LabelKibibytes);
            char[] characters = new char[LabelKibibytes * 1024];

            for (int systemIndex = 0; systemIndex < SystemCount; systemIndex++)
            {
                Assert.IsTrue(NetworkSystemPool.TryRent<NetworkSystem, SteamLargeLabelComponent>(server, out NetworkSystem serverSystem, canStartSystem: true), "The server could not start a system.");

                for (int characterIndex = 0; characterIndex < characters.Length; characterIndex++)
                    characters[characterIndex] = (char)random.Next('a', 'z' + 1);

                labels[systemIndex] = new string(characters);
                Assert.IsTrue(serverSystem.TryGetComponent(out SteamLargeLabelComponent serverComponent));
                serverComponent.Label.Value = labels[systemIndex];
                systemIds[systemIndex] = serverSystem.Id;
            }

            yield return new WaitForSecondsRealtime(0.5f);

            GameObject clientObject = NewInactiveObject("SteamClient");
            UnityCoreManager clientCoreManager = clientObject.AddComponent<UnityCoreManager>();
            UnityTransportManager clientTransportManager = clientObject.AddComponent<UnityTransportManager>();
            SteamTransport clientSteamTransport = clientObject.AddComponent<SteamTransport>();
            clientSteamTransport.RemoteAddress = $"127.0.0.1:{port}";
            clientObject.SetActive(true);

            yield return WaitForTask(clientTransportManager.EnsureAddedAsync());

            CoreManager client = clientCoreManager.CoreManager;
            SteamNetworkingTransport clientTransport = (SteamNetworkingTransport)client.TransportManager.Transports[0];
            clientTransport.Addressing = SteamAddressing.IpAddress;
            clientTransport.SendRateKilobytesPerSecond = sendRateKilobytesPerSecond;

            long sentBytesBeforeJoin = server.TransportManager.TotalBytesSent;
            float joinStartedAt = Time.realtimeSinceStartup;

            yield return WaitForTask(clientTransportManager.StartClientAsync());
            yield return WaitUntil(() => CountLabelsHeld(client, systemIds, labels) == SystemCount, JoinSeconds);

            joinResult.HeldCount = CountLabelsHeld(client, systemIds, labels);
            joinResult.Seconds = Time.realtimeSinceStartup - joinStartedAt;
            joinResult.StateBytes = (long)SystemCount * LabelKibibytes * 1024;
            joinResult.SentBytes = server.TransportManager.TotalBytesSent - sentBytesBeforeJoin;
            joinResult.ReceivedBytes = client.TransportManager.TotalBytesReceived;
            joinResult.BudgetKilobytes = maximumObjectKilobytesPerTick;
            joinResult.SendRateKilobytesPerSecond = sendRateKilobytesPerSecond;

            foreach (Connection remoteClient in server.TransportManager.ActiveConnections)
            {
                joinResult.LossRepairCount = ReadInternalCount(remoteClient, "LossRepairServedCount");
                joinResult.RecoveryCount = ReadInternalCount(remoteClient, "RecoveryServedCount");
            }

            Debug.Log($"[SteamLargeJoin] {joinResult}");
        }

        /// <summary>
        /// Counts the systems the client holds with the server's label, resolving each by Id on every read.
        /// </summary>
        private static int CountLabelsHeld(CoreManager client, uint[] systemIds, string[] labels)
        {
            int heldCount = 0;

            for (int systemIndex = 0; systemIndex < systemIds.Length; systemIndex++)
            {
                if (client.SystemManager.TryGetSystemReference(systemIds[systemIndex], out NetworkSystem clientSystem) && clientSystem.TryGetComponent(out SteamLargeLabelComponent clientComponent) && clientComponent.Label.Value == labels[systemIndex])
                    heldCount++;
            }

            return heldCount;
        }

        /// <summary>
        /// Reads one of a Connection's internal repair counts, which this assembly cannot name directly.
        /// </summary>
        private static uint ReadInternalCount(Connection connection, string propertyName)
        {
            PropertyInfo propertyInfo = typeof(Connection).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);

            return propertyInfo is null ? 0 : (uint)propertyInfo.GetValue(connection);
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
        private static IEnumerator WaitForTask(Task task) => WaitUntil(() => task.IsCompleted, MaximumWaitSeconds);

        /// <summary>
        /// Waits until the condition holds, or the wait runs out.
        /// </summary>
        private static IEnumerator WaitUntil(Func<bool> condition, float seconds)
        {
            float deadlineSeconds = Time.realtimeSinceStartup + seconds;

            while (!condition() && Time.realtimeSinceStartup < deadlineSeconds)
                yield return null;
        }

        /// <summary>
        /// What a join cost.
        /// </summary>
        private sealed class JoinResult
        {
            /// <summary>
            /// The server's object pacing budget, in kilobytes a tick.
            /// </summary>
            public byte BudgetKilobytes;
            /// <summary>
            /// The rate Steam sent at, in kilobytes a second, or zero for Steam's own.
            /// </summary>
            public uint SendRateKilobytesPerSecond;
            /// <summary>
            /// How many values the client held when the join ended.
            /// </summary>
            public int HeldCount;
            /// <summary>
            /// How long the join took, in seconds.
            /// </summary>
            public float Seconds;
            /// <summary>
            /// The width of the world's values together.
            /// </summary>
            public long StateBytes;
            /// <summary>
            /// Everything the server sent from the client connecting.
            /// </summary>
            public long SentBytes;
            /// <summary>
            /// Everything the client received.
            /// </summary>
            public long ReceivedBytes;
            /// <summary>
            /// How many loss repairs the server served the client.
            /// </summary>
            public uint LossRepairCount;
            /// <summary>
            /// How many timeout recoveries the server served the client.
            /// </summary>
            public uint RecoveryCount;

            /// <summary>
            /// Whether the client held every value.
            /// </summary>
            public bool IsComplete => HeldCount == SystemCount;

            /// <summary>
            /// What the server sent against the state's own size.
            /// </summary>
            public double SentToStateRatio => StateBytes == 0 ? 0 : (double)SentBytes / StateBytes;

            /// <inheritdoc/>
            public override string ToString() => $"[{SystemCount}] x [{LabelKibibytes}] KiB at [{BudgetKilobytes}] kB a tick over Steam at [{SendRateKilobytesPerSecond}] KiB/s (0 is Steam's own, 256): held [{HeldCount}] after [{Seconds:F1}] s, state [{StateBytes}] bytes, sent [{SentBytes}] ({SentToStateRatio:F2}x), received [{ReceivedBytes}], loss repairs [{LossRepairCount}], timeout recoveries [{RecoveryCount}].";
        }
    }
}
#endif
