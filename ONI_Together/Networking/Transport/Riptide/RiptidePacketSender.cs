using System;
using System.Collections.Generic;
using Riptide;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using Shared.Profiling;

namespace ONI_Together.Networking.Transport.Lan
{
    public class RiptidePacketSender : TransportPacketSender
    {
        public override bool SendPacket(object conn, SerializedPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate)
        {
            using var _ = Profiler.Scope();

            if (conn is not Connection connection)
                return false;

            if (!connection.IsConnected)
                return false;

			byte[] bytes = packet.Bytes;

			if (bytes.Length > RiptideFrameCodec.MaxNativePayloadBytes)
			{
				if ((sendType & PacketSendMode.Reliable) == 0)
					return false;
				if (!RiptideFrameCodec.TryCreateFrames(bytes, out var frames))
					return false;
				if (!SendAdapterFrames(
					    frames, frame => SendRaw(
						    connection,
						    new SerializedPacket(packet.Packet, frame),
						    sendType)))
					return false;
				TrackLogicalSend(packet.Packet, bytes.Length);
				return true;
			}

			if (!SendRaw(connection, packet, sendType))
				return false;
			TrackLogicalSend(packet.Packet, bytes.Length);
			return true;
        }

		private bool SendRaw(
			Connection connection, SerializedPacket packet, PacketSendMode sendType)
        {
            MessageSendMode sendMode = ConvertSendType(sendType);
			Riptide.Message msg = Riptide.Message.Create(sendMode, 1); // TODO: Test with packet id though I don't think it matters since we handle packets elsewhere
			msg.AddBytes(packet.Bytes);

			try
			{
				if (!TryNativeSend(connection, msg))
					return false;
				SyncStats.RecordNativeSend(packet.PacketType, packet.Bytes.Length, success: true);
			}
			catch
			{
				SyncStats.RecordNativeSend(packet.PacketType, packet.Bytes.Length, success: false);
				throw;
			}
            return true;
        }

		private static void TrackLogicalSend(IPacket packet, int bytes)
			=> PacketTracker.TrackSent(new PacketTracker.PacketTrackData
			{
				packet = packet,
				size = bytes
				});

		internal static bool SendAdapterFrames(
			IReadOnlyList<byte[]> frames, Func<byte[], bool> nativeSend)
		{
			if (frames == null || frames.Count == 0 || nativeSend == null)
				return false;
			foreach (byte[] frame in frames)
			{
				if (!nativeSend(frame))
					return false;
			}
			return true;
		}

		private static bool TryNativeSend(Connection connection, Riptide.Message message)
		{
			if (MultiplayerSession.IsHost)
			{
				Riptide.Server server = RiptideServer.ServerInstance;
				if (server == null)
					return false;
				server.Send(message, connection);
				return true;
			}
			Riptide.Client client = RiptideClient.Client;
			if (client == null)
				return false;
			client.Send(message);
			return true;
		}

        private static MessageSendMode ConvertSendType(PacketSendMode sendType)
        {
            using var _ = Profiler.Scope();

            switch (sendType)
            {
                case PacketSendMode.Reliable:
                case PacketSendMode.ReliableImmediate:
                    return MessageSendMode.Reliable;

                case PacketSendMode.Unreliable:
                case PacketSendMode.UnreliableImmediate:
                case PacketSendMode.UnreliableNoDelay:
                    return MessageSendMode.Unreliable;

                default:
                    // Catch-all for unexpected flag combinations
                    if ((sendType & PacketSendMode.Reliable) != 0)
                        return MessageSendMode.Reliable;

                    return MessageSendMode.Unreliable;
            }
        }
    }
}
