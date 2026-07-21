#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets;
using ONI_Together.Networking.Packets.DLC.SpacedOut;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Patches.DLC.SpacedOut;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	public sealed partial class SoakHashDomainKeyframePacket : IPacket, IHostOnlyPacket
	{
		private const int MaxStorageCount = 64;
		private const int MaxStorageSnapshotBytes = 4 * 1024 * 1024;
		private const int MaxTotalSnapshotBytes = 12 * 1024 * 1024;

		public int RunId;
		public int SampleId;
		public int EntryIndex;
		public int NetId;
		public SpawnPrefabPacket LifecycleSnapshot = new();
		public int WorldId;
		public Vector3 Position;
		public bool HasPosition;
		public bool FlipX;
		public bool FlipY;
		public NavType NavType;
		public long PositionSequence;
		public bool HasClusterLocation;
		public int ClusterQ;
		public int ClusterR;
		public bool HasRocketSettings;
		public RocketSettingsPacketData RocketSettings;
		public ulong StorageRevision;
		public List<byte[]> StorageSnapshots = new();

		private sealed class PreparedKeyframe
		{
			internal SoakHashDomainKeyframePacket Packet;
			internal NetworkIdentity Identity;
			internal Storage[] Storages;
		}

		public void Serialize(BinaryWriter writer)
		{
			Validate();
			writer.Write(RunId);
			writer.Write(SampleId);
			writer.Write(EntryIndex);
			writer.Write(NetId);
			LifecycleSnapshot.Serialize(writer);
			writer.Write(WorldId);
			writer.Write(Position);
			writer.Write(HasPosition);
			if (HasPosition)
			{
				writer.Write(FlipX);
				writer.Write(FlipY);
				writer.Write((byte)NavType);
				writer.Write(PositionSequence);
			}
			writer.Write(HasClusterLocation);
			if (HasClusterLocation)
			{
				writer.Write(ClusterQ);
				writer.Write(ClusterR);
			}
			writer.Write(HasRocketSettings);
			if (HasRocketSettings)
				RocketSettings.Serialize(writer);
			writer.Write(StorageRevision);
			writer.Write((byte)StorageSnapshots.Count);
			foreach (byte[] snapshot in StorageSnapshots)
			{
				writer.Write(snapshot.Length);
				writer.Write(snapshot);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			RunId = reader.ReadInt32();
			SampleId = reader.ReadInt32();
			EntryIndex = reader.ReadInt32();
			NetId = reader.ReadInt32();
			LifecycleSnapshot = new SpawnPrefabPacket();
			LifecycleSnapshot.Deserialize(reader);
			WorldId = reader.ReadInt32();
			Position = reader.ReadVector3();
			HasPosition = reader.ReadBoolean();
			if (HasPosition)
			{
				FlipX = reader.ReadBoolean();
				FlipY = reader.ReadBoolean();
				NavType = (NavType)reader.ReadByte();
				PositionSequence = reader.ReadInt64();
			}
			HasClusterLocation = reader.ReadBoolean();
			if (HasClusterLocation)
			{
				ClusterQ = reader.ReadInt32();
				ClusterR = reader.ReadInt32();
			}
			HasRocketSettings = reader.ReadBoolean();
			RocketSettings = HasRocketSettings
				? RocketSettingsPacketData.Deserialize(reader)
				: null;
			StorageRevision = reader.ReadUInt64();
			int storageCount = reader.ReadByte();
			StorageSnapshots = new List<byte[]>(storageCount);
			int totalBytes = 0;
			for (int index = 0; index < storageCount; index++)
			{
				int length = reader.ReadInt32();
				ValidateSnapshotLength(length, totalBytes);
				byte[] snapshot = reader.ReadBytes(length);
				if (snapshot.Length != length)
					throw new EndOfStreamException("Soak storage keyframe is truncated");
				StorageSnapshots.Add(snapshot);
				totalBytes += length;
			}
			Validate();
		}

		public void OnDispatched()
		{
			if (MultiplayerSession.IsHost)
				return;
			if (SoakHashDomainKeyframeTracker.RecordPacket(this))
				SoakStateHashProbe.SendKeyframeProgress();
		}

		internal byte[] SerializeBody()
		{
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(
				       stream, System.Text.Encoding.UTF8, leaveOpen: true))
				Serialize(writer);
			return stream.ToArray();
		}

		internal static SoakHashDomainKeyframePacket DeserializeBody(byte[] body)
		{
			if (body == null || body.Length <= 0 || body.Length > PacketHandler.MaxPacketSize)
				throw new InvalidDataException("Invalid soak keyframe body length");
			using var stream = new MemoryStream(body, writable: false);
			using var reader = new BinaryReader(stream);
			var packet = new SoakHashDomainKeyframePacket();
			packet.Deserialize(reader);
			if (stream.Position != stream.Length)
				throw new InvalidDataException("Trailing soak keyframe body bytes");
			return packet;
		}

		internal static List<SoakHashDomainKeyframePacket> CaptureAll(
			int runId, int sampleId)
		{
			PrepareHashDomainIdentityMembership();
			var packets = new List<SoakHashDomainKeyframePacket>();
			NetworkIdentity[] identities = NetworkIdentityRegistry.AllIdentities
			         .Where(identity => !identity.IsNullOrDestroyed()
			                            && !identity.gameObject.IsNullOrDestroyed()
			                            && identity.NetId != 0)
			         .OrderBy(identity => identity.NetId).ToArray();
			foreach (NetworkIdentity identity in identities)
			{
					RemoteMotionPresenter position = identity.GetComponent<RemoteMotionPresenter>();
					ClusterGridEntity cluster = identity.GetComponent<ClusterGridEntity>();
					RocketModuleCluster module = identity.GetComponent<RocketModuleCluster>();
					RocketControlStation station = identity.GetComponent<RocketControlStation>();
					RocketSettingsPacketData rocketSettings = null;
					bool hasRocketSettings = module != null && RocketSettingsSync.TryCapture(
						module.CraftInterface?.GetClusterDestinationSelector(), out rocketSettings);
					if (!hasRocketSettings && station != null)
						hasRocketSettings = RocketSettingsSync.TryCapture(station, out rocketSettings);
				Storage[] storages = identity.GetComponents<Storage>();
				SpawnPrefabPacket lifecycle = SpawnPrefabPacket.FromIdentity(identity);
				if (lifecycle == null)
					throw new InvalidDataException("Could not capture lifecycle keyframe");
				lifecycle.Revision = NetworkIdentityRegistry.GetLastLifecycleRevision(
					identity.NetId);
				lifecycle.IsActive = identity.gameObject.activeSelf;
				lifecycle.WorldId = identity.gameObject.GetMyWorldId();
				lifecycle.Position = identity.transform.position;
				if (lifecycle.Revision == 0
				    || NetworkIdentityRegistry.IsLifecycleTombstoned(identity.NetId))
					throw new InvalidDataException("Live identity has no authoritative lifecycle");
				var packet = new SoakHashDomainKeyframePacket
				{
					RunId = runId,
					SampleId = sampleId,
					EntryIndex = packets.Count,
					NetId = identity.NetId,
					LifecycleSnapshot = lifecycle,
					WorldId = identity.gameObject.GetMyWorldId(),
					Position = identity.transform.position,
					HasPosition = position != null,
					HasClusterLocation = cluster != null,
					ClusterQ = cluster?.Location.q ?? 0,
					ClusterR = cluster?.Location.r ?? 0,
					HasRocketSettings = hasRocketSettings,
					RocketSettings = rocketSettings,
					StorageRevision = storages.Length == 0
						? 0
						: NetworkIdentityRegistry.NextAuthorityRevision(),
				};
				if (position != null)
					packet.CapturePosition(position);
				foreach (Storage storage in storages)
					packet.StorageSnapshots.Add(CaptureStorage(storage));
				packet.Validate();
				packets.Add(packet);
			}
			return packets;
		}

		private static void PrepareHashDomainIdentityMembership()
		{
			for (int pass = 0; pass < 8; pass++)
			{
				int before = NetworkIdentityRegistry.Count;
				foreach (NetworkIdentity identity in NetworkIdentityRegistry.AllIdentities.ToArray())
				{
					if (identity.IsNullOrDestroyed() || identity.gameObject.IsNullOrDestroyed())
						continue;
					foreach (Storage storage in identity.GetComponents<Storage>())
						StorageSnapshotSync.PrepareIdentityMembership(storage);
				}
				if (NetworkIdentityRegistry.Count == before)
				{
					NetworkIdentityRegistry.RetireUnstableElementLifecyclesForSnapshot();
					return;
				}
			}
			throw new InvalidDataException("Storage identity membership did not stabilize");
		}

		private void CapturePosition(RemoteMotionPresenter presenter)
		{
			Position = presenter.transform.position;
			FlipX = presenter.AnimController != null && presenter.AnimController.FlipX;
			FlipY = presenter.AnimController != null && presenter.AnimController.FlipY;
			NavType = presenter.Navigator != null
			          && presenter.Navigator.CurrentNavType != NavType.NumNavTypes
				? presenter.Navigator.CurrentNavType
				: NavType.Floor;
			PositionSequence = RemoteMotionPresenter.NextHostSequence();
		}

		private static byte[] CaptureStorage(Storage storage)
		{
			var values = new Dictionary<string, Variant>();
			StorageSnapshotSync.Encode(storage, values);
			if (!values.TryGetValue("stor", out Variant snapshot)
			    || snapshot.ByteArray == null)
				throw new InvalidDataException("Could not capture soak storage keyframe");
			return snapshot.ByteArray;
		}

		internal static bool TryApplyAll(
			IReadOnlyList<SoakHashDomainKeyframePacket> packets,
			IReadOnlyList<NetworkIdentityRegistry.LifecycleRevisionSnapshotEntry> baseline)
		{
			if (!TryReconcileLifecycle(packets, baseline))
				return false;
			if (!TryPrepareAll(packets, out List<PreparedKeyframe> prepared))
			{
				DebugConsole.LogWarning("[SoakKeyframe] domain preflight failed");
				return false;
			}
			List<StorageSnapshotSync.SnapshotRequest> requests =
				BuildStorageRequests(prepared);
			if (!StorageSnapshotSync.TryPrepareBatch(
				    requests, out StorageSnapshotSync.SnapshotBatch batch))
			{
				DebugConsole.LogWarning("[SoakKeyframe] storage batch preflight failed");
				return false;
			}
			KeyframeStorageRevisionCommit revisionCommit =
				SetStorageApplyDecisions(prepared, requests);
			bool applied = TryApplyPreparedKeyframes(prepared, batch);
			if (applied)
			{
				NetworkIdentityRegistry.LifecycleMembershipValidationResult membership =
					NetworkIdentityRegistry.ValidateCurrentLifecycleMembership(baseline);
				applied = membership.IsValid;
				if (!applied)
				{
					DebugConsole.LogWarning(
						$"[SoakKeyframe] final lifecycle mismatch missing={membership.MissingLiveCount} " +
						$"unexpected={membership.UnexpectedLiveCount} tombstoned={membership.TombstonedLiveCount} " +
						$"unassigned={membership.UnassignedLiveCount}");
				}
			}
			bool committed = revisionCommit.TryComplete(applied);
			if (!committed)
				DebugConsole.LogWarning(
					$"[SoakKeyframe] storage revision commit failed; applied={applied}");
			return committed;
		}

		private static bool TryApplyPreparedKeyframes(
			IReadOnlyList<PreparedKeyframe> prepared,
			StorageSnapshotSync.SnapshotBatch batch)
		{
			if (!batch.Apply())
			{
				DebugConsole.LogWarning("[SoakKeyframe] storage batch apply failed");
				return false;
			}
			foreach (PreparedKeyframe keyframe in prepared)
			{
				if (!keyframe.Packet.TryApplyWorldTransform(keyframe.Identity))
				{
					DebugConsole.LogWarning(
						$"[SoakKeyframe] world transform apply failed for NetId " +
						$"{keyframe.Packet.NetId}");
					return false;
				}
			}
			foreach (PreparedKeyframe keyframe in prepared)
			{
				if (!keyframe.Packet.TryApplyNonPosition(keyframe.Identity))
				{
					DebugConsole.LogWarning(
						$"[SoakKeyframe] cluster/rocket apply failed for NetId " +
						$"{keyframe.Packet.NetId}");
					return false;
				}
			}
			return true;
		}

		private static bool TryPrepareAll(
			IReadOnlyList<SoakHashDomainKeyframePacket> packets,
			out List<PreparedKeyframe> prepared)
		{
			prepared = new List<PreparedKeyframe>(packets?.Count ?? 0);
			if (packets == null)
				return false;
			var netIds = new HashSet<int>();
			int runId = packets.Count == 0 ? 0 : packets[0].RunId;
			int sampleId = packets.Count == 0 ? 0 : packets[0].SampleId;
			foreach (SoakHashDomainKeyframePacket packet in packets)
			{
				if (packet.RunId != runId || packet.SampleId != sampleId
				    || !netIds.Add(packet.NetId))
				{
					DebugConsole.LogWarning(
						$"[SoakKeyframe] invalid or duplicate domain entry " +
						$"{packet?.NetId ?? 0}");
					return false;
				}
				if (!packet.TryPrepare(out PreparedKeyframe keyframe))
				{
					DebugConsole.LogWarning(
						$"[SoakKeyframe] domain entry preflight failed for NetId " +
						$"{packet.NetId}: {packet.DescribePrepareFailure()}");
					return false;
				}
				prepared.Add(keyframe);
			}
			return true;
		}

	}
}
#endif
