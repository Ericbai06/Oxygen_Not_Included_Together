using Riptide;
using Riptide.Utils;
using ONI_Together_DedicatedServer.ONI;
using Shared.Profiling;

namespace ONI_Together_DedicatedServer.Transports
{
    public class DedicatedRiptideServer : DedicatedTransportServer
    {
        public Server? _server;

        public Dictionary<ulong, ONI.Player> ConnectedPlayers = new Dictionary<ulong, ONI.Player>(); // clientId, Player

        public DedicatedRiptideServer()
        {
            using var _ = Profiler.Scope();

            RiptideLogger.Initialize(Console.WriteLine, false);
        }

        public override void Start()
        {
            throw new NotSupportedException(
                "Dedicated server is disabled: authenticated designated-host handshake is not implemented.");
        }

        private void OnClientConnected(object? sender, ServerConnectedEventArgs e)
        {
            using var _ = Profiler.Scope();

            ulong clientId = e.Client.Id;
            if(!ConnectedPlayers.ContainsKey(clientId))
            {
                ONI.Player player = new ONI.Player(e.Client, ConnectedPlayers.Count == 0); // If there are no connected clients we are the master
                ConnectedPlayers.Add(clientId, player);
                Console.Write($"A new player joined the server. {player.ClientID} : {player.IsMaster}");
            }
        }

        private void OnClientDisconnected(object? sender, ServerDisconnectedEventArgs e)
        {
            using var _ = Profiler.Scope();

            ulong clientId = e.Client.Id;
            bool wasMaster = false;
            if(ConnectedPlayers.TryGetValue(clientId, out ONI.Player? player))
            {
                wasMaster = player.IsMaster;
                ConnectedPlayers.Remove(clientId);
            }
            Console.Write($"A player disconnected from the server. {clientId} : {wasMaster}");

            if (!wasMaster) // We wasn't the master we don't care
                return;

            Console.WriteLine("\nThe master disconnected! Attempting to assign a new master!");
            if (_server?.Clients.Length > 0)
            {
                // Find the client with the smallest ping
                Connection? newMasterClient = _server.Clients.Where(c => c.SmoothRTT >= 0).OrderBy(c => c.SmoothRTT).FirstOrDefault();

                if (newMasterClient != null && ConnectedPlayers.TryGetValue(newMasterClient.Id, out ONI.Player newMaster))
                {
                    newMaster.UpdateMasterState(true);
                    Console.WriteLine($"New master assigned: Client {newMasterClient.Id} with ping {newMasterClient.SmoothRTT}");

                    // Notify this client that they are now the master, TODO: Send a migration packet
                }
            }
            else
            {
                Console.WriteLine("No other clients connected. No master assigned.");
            }
        }

        private void OnServerMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            using var _ = Profiler.Scope();

            ulong clientId = e.FromConnection.Id;
            byte[] rawData = e.Message.GetBytes();
            int size = rawData.Length;

            if (ConnectedPlayers.TryGetValue(clientId, out ONI.Player player))
            {
                if (rawData.Length < sizeof(int))
                {
                    Console.WriteLine($"Rejected truncated packet from client {clientId}");
                    return;
                }
                int packetType = BitConverter.ToInt32(rawData, 0);

                if (packetType == Utils.DEDICATED_SERVER_PACKET_ID)
                {
                    Console.WriteLine($"Rejected nested dedicated wrapper from client {clientId}");
                    return;
                }

                Console.WriteLine(
                    $"\nServer received packet from {clientId}, " +
                    $"PacketType={packetType}, Size={size} bytes"
                );

                MessageSendMode sendMode = e.Message.SendMode == MessageSendMode.Reliable
                    ? MessageSendMode.Reliable
                    : MessageSendMode.Unreliable;
                byte[] relayedPacketData = CreateRelayEnvelope(
                    clientId, player.IsMaster, rawData);

                ForwardRelay(player, relayedPacketData, sendMode);
            }
        }

        private void ForwardRelay(
            ONI.Player sender, byte[] relayEnvelope, MessageSendMode sendMode)
        {
            if (sender.IsMaster)
            {
                Console.WriteLine("Broadcasting master packet to clients");
                foreach (ONI.Player client in ConnectedPlayers.Values.Where(player => !player.IsMaster))
                    SendRelay(client.Connection, relayEnvelope, sendMode);
                return;
            }

            Console.WriteLine("Received packet from client, sending to master!");
            ONI.Player master = ConnectedPlayers.Values.FirstOrDefault(player => player.IsMaster);
            if (master != null)
                SendRelay(master.Connection, relayEnvelope, sendMode);
        }

        private static byte[] CreateRelayEnvelope(
            ulong senderId,
            bool senderIsHost,
            byte[] rawTransportFrame)
        {
            int packetType = BitConverter.ToInt32(rawTransportFrame, 0);
            return Utils.SerializePacketForSending(Utils.DEDICATED_SERVER_PACKET_ID, writer =>
            {
                writer.Write(packetType);
                writer.Write(senderId);
                writer.Write(senderIsHost);
                writer.Write(rawTransportFrame.Length);
                writer.Write(rawTransportFrame);
            });
        }

        private static void SendRelay(Connection connection, byte[] data, MessageSendMode sendMode)
        {
            Riptide.Message message = Riptide.Message.Create(sendMode, 1);
            message.AddBytes(data);
            connection.Send(message);
        }

        public override void Stop()
        {
            using var _ = Profiler.Scope();

            if (!IsRunning())
                return;

            _server.Stop();
            _server = null;
        }

        public override bool IsRunning()
        {
            using var _ = Profiler.Scope();

            if (_server == null)
                return false;

            return _server.IsRunning;
        }

        public override void Update()
        {
            using var _ = Profiler.Scope();

            _server?.Update();
        }

        public override Dictionary<ulong, ONI.Player> GetPlayers()
        {
            using var _ = Profiler.Scope();

            return ConnectedPlayers;
        }
    }
}
