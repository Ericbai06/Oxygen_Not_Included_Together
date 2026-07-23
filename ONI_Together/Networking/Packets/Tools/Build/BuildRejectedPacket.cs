using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Interfaces.Networking;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.Tools.Build
{
	public sealed class BuildRejectedPacket : IPacket, IHostOnlyPacket
	{
		public BuildOperationId OperationId;
		public BuildRejectionReason Reason;
		public string Message = string.Empty;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			if (!OperationId.IsValid || Reason == BuildRejectionReason.Unknown ||
				Message == null || Message.Length > BuildRequestValidator.MaxIdLength)
				throw new InvalidDataException("Invalid build rejection payload");
			BuildRequestPacket.WriteOperationId(writer, OperationId);
			writer.Write((byte)Reason);
			writer.Write(Message);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			OperationId = BuildRequestPacket.ReadOperationId(reader);
			Reason = (BuildRejectionReason)reader.ReadByte();
			Message = BuildWire.ReadBoundedString(reader, BuildRequestValidator.MaxIdLength);
			if (!OperationId.IsValid || Reason == BuildRejectionReason.Unknown)
				throw new InvalidDataException("Invalid build rejection payload");
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			if (MultiplayerSession.IsHost || !PacketHandler.CurrentContext.SenderIsHost)
				return;
			DebugConsole.LogWarning(
				$"[BuildRejected] operation={OperationId} reason={Reason} message={Message}");
		}

		internal static BuildRejectedPacket FromDomain(BuildRejected rejected)
			=> new()
			{
				OperationId = rejected.OperationId,
				Reason = rejected.Reason,
				Message = rejected.Message
			};
	}
}
