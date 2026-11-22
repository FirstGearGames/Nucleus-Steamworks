#if UNITY_INCLUDE_TESTS
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace Nucleus.Integrations.Unity.Tests
{
    /// <summary>
    /// Carries datagrams between one client and a server on this machine, and can stop carrying them at once, so a client can vanish
    /// without anything it sends afterwards, its close included, reaching the server, as a crashed game's link does.
    /// </summary>
    /// <remarks>Each direction is carried on a background thread of its own. Destroying the component closes both sockets, which ends them.</remarks>
    public class SteamUdpRelay : MonoBehaviour
    {
        /// <summary>
        /// The port a client dials to reach the server through this relay.
        /// </summary>
        public ushort Port => (ushort)((IPEndPoint)_clientSideUdpClient.Client.LocalEndPoint).Port;

        /// <summary>
        /// The socket the client's datagrams arrive on and the server's leave from.
        /// </summary>
        private UdpClient _clientSideUdpClient;
        /// <summary>
        /// The socket the client's datagrams leave from for the server, and the server's arrive on.
        /// </summary>
        private UdpClient _serverSideUdpClient;
        /// <summary>
        /// Where the client last sent from, which is where the server's datagrams are carried to.
        /// </summary>
        private volatile IPEndPoint _clientEndPoint;
        /// <summary>
        /// Whether datagrams are still carried, cleared by <see cref="Cut"/>.
        /// </summary>
        private volatile bool _isCarrying = true;

        /// <summary>
        /// Opens the relay's sockets and starts carrying datagrams to and from a server listening on this machine.
        /// </summary>
        /// <param name="serverPort">The port the server listens on.</param>
        public void Begin(ushort serverPort)
        {
            _clientSideUdpClient = new(new IPEndPoint(IPAddress.Loopback, 0));
            _serverSideUdpClient = new(new IPEndPoint(IPAddress.Loopback, 0));
            _serverSideUdpClient.Connect(IPAddress.Loopback, serverPort);

            new Thread(CarryToServer) { IsBackground = true }.Start();
            new Thread(CarryToClient) { IsBackground = true }.Start();
        }

        /// <summary>
        /// Stops carrying datagrams either way, without a word to either end.
        /// </summary>
        public void Cut() => _isCarrying = false;

        /// <summary>
        /// Carries the client's datagrams to the server until the relay is destroyed.
        /// </summary>
        private void CarryToServer()
        {
            IPEndPoint remoteEndPoint = new(IPAddress.Any, 0);

            while (TryReceive(_clientSideUdpClient, ref remoteEndPoint, out byte[] datagram))
            {
                if (!_isCarrying)
                    continue;

                _clientEndPoint = remoteEndPoint;
                _serverSideUdpClient.Send(datagram, datagram.Length);
            }
        }

        /// <summary>
        /// Carries the server's datagrams to the client until the relay is destroyed.
        /// </summary>
        private void CarryToClient()
        {
            IPEndPoint remoteEndPoint = new(IPAddress.Any, 0);

            while (TryReceive(_serverSideUdpClient, ref remoteEndPoint, out byte[] datagram))
            {
                IPEndPoint clientEndPoint = _clientEndPoint;

                if (_isCarrying && clientEndPoint is not null)
                    _clientSideUdpClient.Send(datagram, datagram.Length, clientEndPoint);
            }
        }

        /// <summary>
        /// Closes both sockets, which ends the threads carrying datagrams.
        /// </summary>
        private void OnDestroy()
        {
            _isCarrying = false;
            _clientSideUdpClient?.Close();
            _serverSideUdpClient?.Close();
        }

        /// <summary>
        /// Waits for the next datagram on a socket.
        /// </summary>
        /// <param name="udpClient">The socket.</param>
        /// <param name="remoteEndPoint">Where the datagram came from.</param>
        /// <param name="datagram">The datagram.</param>
        /// <returns>True when a datagram arrived; false once the socket is closed.</returns>
        /// <remarks>A datagram this relay sent to a port that has since closed makes Windows fail the next receive, which is retried.</remarks>
        private static bool TryReceive(UdpClient udpClient, ref IPEndPoint remoteEndPoint, out byte[] datagram)
        {
            while (true)
            {
                try
                {
                    datagram = udpClient.Receive(ref remoteEndPoint);

                    return true;
                }
                catch (SocketException socketException) when (socketException.SocketErrorCode is SocketError.ConnectionReset)
                {
                    // Retried, as the remarks say.
                }
                catch (Exception)
                {
                    datagram = null;

                    return false;
                }
            }
        }
    }
}
#endif
