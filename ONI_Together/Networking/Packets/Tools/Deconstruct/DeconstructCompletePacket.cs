using System.IO;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.Tools.Deconstruct
{
	public sealed class DeconstructCompletePacket : IPacket, IHostOnlyPacket
	{
		public int NetId;
		public ulong Revision;

		public DeconstructCompletePacket()
		{
		}

		public DeconstructCompletePacket(int netId)
		{
			NetId = netId;
			Revision = NetworkIdentityRegistry.EndLifecycle(netId);
		}

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			ValidateWire();
			writer.Write(NetId);
			writer.Write(Revision);
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			NetId = reader.ReadInt32();
			Revision = reader.ReadUInt64();
			ValidateWire();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			ulong current = NetworkIdentityRegistry.GetLastLifecycleRevision(NetId);
			bool senderAccepted = !MultiplayerSession.IsHost
			                      && PacketHandler.CurrentContext.SenderIsHost;
			if (!ShouldApply(senderAccepted, current, Revision)
			    || !NetworkIdentityRegistry.TryAcceptLifecycleRevision(
				    NetId, Revision, tombstone: true))
				return;
			StorageItemPacket.CancelPending(NetId);
			GroundItemPickedUpPacket.CancelPending(NetId);
			SpawnPrefabPacket.CancelPendingBinding(NetId);
			if (!NetworkIdentityRegistry.TryGetComponent(
				    NetId, out Deconstructable deconstructable))
				return;
			if (!deconstructable.HasBeenDestroyed)
				deconstructable.ForceDestroyAndGetMaterials();
			DebugConsole.Log($"[DeconstructCompletePacket] Applied NetId={NetId}");
		}

		internal static bool ShouldApply(
			bool senderAccepted, ulong current, ulong incoming)
			=> senderAccepted && NetworkIdentityRegistry.IsNewerRevision(current, incoming);

		private void ValidateWire()
		{
			if (NetId == 0 || Revision == 0)
				throw new InvalidDataException("Invalid deconstruct lifecycle metadata");
		}
	}
}
