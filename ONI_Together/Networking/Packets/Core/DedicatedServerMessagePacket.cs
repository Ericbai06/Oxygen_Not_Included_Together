using System;
using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.Core
{
    public class DedicatedServerMessagePacket : IPacket, IHostOnlyPacket
    {
        public int PacketID;
        public byte[] PacketData;
        public ulong SenderId;
        public bool SenderIsHost;

        public void Serialize(BinaryWriter writer)
        {
            using var _ = Profiler.Scope();

			Validate();
			writer.Write(PacketID);
            writer.Write(SenderId);
            writer.Write(SenderIsHost);
            writer.Write(PacketData.Length);
            writer.Write(PacketData);
        }

        public void Deserialize(BinaryReader reader)
        {
            using var _ = Profiler.Scope();

            PacketID = reader.ReadInt32();
            SenderId = reader.ReadUInt64();
            SenderIsHost = reader.ReadBoolean();
            int length = reader.ReadInt32();
            if (length < sizeof(int) || length > PacketHandler.MaxPacketSize)
                throw new InvalidDataException($"Invalid dedicated relay payload length: {length}");
            if (reader.BaseStream.CanSeek && reader.BaseStream.Length - reader.BaseStream.Position < length)
                throw new EndOfStreamException("Dedicated relay payload is truncated");
            PacketData = reader.ReadBytes(length);
            if (PacketData.Length != length)
                throw new EndOfStreamException("Dedicated relay payload is truncated");
			Validate();
        }

        public void OnDispatched()
        {
            using var _ = Profiler.Scope();

			Validate();
			DispatchContext transportContext = PacketHandler.CurrentContext;
			DispatchContext? relayContext = PacketHandler.TryCreateDedicatedRelayContext(
				transportContext, SenderId, SenderIsHost);
			if (!relayContext.HasValue)
				throw new InvalidDataException("Invalid dedicated relay sender context");
			DebugConsole.Log("Received a packet from a dedicated server with packet id: " + PacketID);
			if (!PacketHandler.TryHandleIncoming(
				    PacketData,
				    relayContext.Value))
				throw new InvalidDataException("Dedicated relay payload was rejected");
        }

		private void Validate()
		{
			if (SenderId == 0 || PacketData == null || PacketData.Length < sizeof(int)
			    || PacketData.Length > PacketHandler.MaxPacketSize
			    || BitConverter.ToInt32(PacketData, 0) != PacketID
			    || PacketHandler.IsForbiddenDedicatedFrame(PacketData))
				throw new InvalidDataException("Invalid dedicated relay payload");
		}
    }
}
