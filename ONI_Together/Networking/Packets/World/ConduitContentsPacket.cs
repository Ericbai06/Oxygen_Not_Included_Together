using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using System.Collections.Generic;
using System.IO;

namespace ONI_Together.Networking.Packets.World
{
	// One entry per pipe cell with non-trivially-changed contents.
	// ConduitType: 0 = gas, 1 = liquid. Solid pipes (rails) deferred — their
	// SolidConduitFlow.ConduitContents references a host-local pickupable handle
	// that does not survive serialization.
	public struct ConduitCellUpdate
	{
		public int Cell;
		public byte ConduitType;
		public ulong Revision;
		public int Element;        // SimHashes (int-backed enum)
		public float Mass;
		public float Temperature;
		public byte DiseaseIdx;
		public int DiseaseCount;
	}

	public class ConduitContentsPacket : IPacket, Shared.Interfaces.Networking.IHostOnlyPacket
	{
		internal const int MaxUpdateCount = 50;
		public const byte CONDUIT_GAS = 0;
		public const byte CONDUIT_LIQUID = 1;
		private static readonly Dictionary<(int Cell, byte Type), ulong> clientRevisions = new();

		public List<ConduitCellUpdate> Updates = new List<ConduitCellUpdate>();

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(Updates.Count);
			foreach (var u in Updates)
			{
				ValidateUpdate(u);
				writer.Write(u.Cell);
				writer.Write(u.ConduitType);
				writer.Write(u.Revision);
				writer.Write(u.Element);
				writer.Write(u.Mass);
				writer.Write(u.Temperature);
				writer.Write(u.DiseaseIdx);
				writer.Write(u.DiseaseCount);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			int count = reader.ReadInt32();
			if (count < 0 || count > MaxUpdateCount)
				throw new InvalidDataException($"Invalid conduit update count: {count}");
			Updates = new List<ConduitCellUpdate>(count);
			for (int i = 0; i < count; i++)
			{
				var update = new ConduitCellUpdate
				{
					Cell = reader.ReadInt32(),
					ConduitType = reader.ReadByte(),
					Revision = reader.ReadUInt64(),
					Element = reader.ReadInt32(),
					Mass = reader.ReadSingle(),
					Temperature = reader.ReadSingle(),
					DiseaseIdx = reader.ReadByte(),
					DiseaseCount = reader.ReadInt32(),
				};
				ValidateUpdate(update);
				Updates.Add(update);
			}
		}

		private static void ValidateUpdate(ConduitCellUpdate update)
		{
			if (update.Revision == 0)
				throw new InvalidDataException("Conduit revision must be non-zero");
			if (update.ConduitType != CONDUIT_GAS && update.ConduitType != CONDUIT_LIQUID)
				throw new InvalidDataException($"Invalid conduit type: {update.ConduitType}");
		}

		internal static bool TryAcceptRevision(int cell, byte conduitType, ulong revision)
		{
			var key = (cell, conduitType);
			ulong current = clientRevisions.TryGetValue(key, out ulong value) ? value : 0;
			if (!NetworkIdentityRegistry.IsNewerRevision(current, revision))
				return false;
			clientRevisions[key] = revision;
			return true;
		}

		internal static void ResetClientRevisionState() => clientRevisions.Clear();

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (MultiplayerSession.IsHost) return;

			ConduitFlowSyncer.Instance?.OnContentsReceived(this);
		}
	}
}
