#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets;
using ONI_Together.Networking.Packets.Core;
using ONI_Together.Networking.Packets.DLC.SpacedOut;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Patches.DLC.SpacedOut;
using Shared.Interfaces.Networking;
using UnityEngine;

namespace ONI_Together.Networking.Packets.World
{
	public sealed partial class SoakHashDomainKeyframePacket : IPacket, IHostOnlyPacket
	{
		private bool TryPrepare(out PreparedKeyframe prepared)
		{
			prepared = null;
			if (!NetworkIdentityRegistry.TryGet(NetId, out NetworkIdentity identity)
			    || identity.IsNullOrDestroyed() || identity.gameObject.IsNullOrDestroyed()
			    || HasPosition && identity.GetComponent<RemoteMotionPresenter>() == null
			    || HasClusterLocation && identity.GetComponent<ClusterGridEntity>() == null
			    || HasRocketSettings && !RocketSettingsSync.CanApply(RocketSettings))
				return false;
			Storage[] storages = identity.GetComponents<Storage>();
			if (storages.Length != StorageSnapshots.Count
			    || storages.Length == 0 && StorageRevision != 0)
				return false;
			prepared = new PreparedKeyframe
			{
				Packet = this,
				Identity = identity,
				Storages = storages,
			};
			return true;
		}

		private string DescribePrepareFailure()
		{
			if (!NetworkIdentityRegistry.TryGet(NetId, out NetworkIdentity identity))
				return "identity missing";
			if (identity.IsNullOrDestroyed() || identity.gameObject.IsNullOrDestroyed())
				return "identity destroyed";
			if (HasPosition && identity.GetComponent<RemoteMotionPresenter>() == null)
				return "position component missing";
			if (HasClusterLocation && identity.GetComponent<ClusterGridEntity>() == null)
				return "cluster component missing";
			if (HasRocketSettings && !RocketSettingsSync.CanApply(RocketSettings))
				return "rocket settings target unavailable";
			Storage[] storages = identity.GetComponents<Storage>();
			if (storages.Length != StorageSnapshots.Count)
				return $"storage count {storages.Length}!={StorageSnapshots.Count}";
			return storages.Length == 0 && StorageRevision != 0
				? "storage revision without storage"
				: "unknown";
		}

		private bool TryApplyNonPosition(NetworkIdentity identity)
		{
			if (HasClusterLocation)
			{
				ClusterGridEntity cluster = identity.GetComponent<ClusterGridEntity>();
				AxialI target = AxialCoordinateSync.FromQr(ClusterQ, ClusterR);
				cluster.Location = target;
				if (cluster.Location != target)
					return false;
			}
			return !HasRocketSettings || RocketSettingsSync.TryApply(RocketSettings);
		}

		private bool TryApplyWorldTransform(NetworkIdentity identity)
		{
			bool applied;
			if (HasPosition)
				applied = TryApplyPosition(identity);
			else
			{
				identity.transform.SetPosition(Position);
				applied = identity.transform.position == Position;
			}
			return applied && identity.gameObject.GetMyWorldId() == WorldId;
		}

		private bool TryApplyPosition(NetworkIdentity identity)
		{
			RemoteMotionPresenter presenter = identity.GetComponent<RemoteMotionPresenter>();
			if (presenter == null)
				return false;
			presenter.ApplyAuthoritativeSnapshot(new EntityMotionState
			{
				Target = Position,
				Revision = (ulong)PositionSequence,
				EndNavType = NavType,
				Flags = (FlipX ? EntityMotionFlags.FlipX : EntityMotionFlags.None)
				        | (FlipY ? EntityMotionFlags.FlipY : EntityMotionFlags.None),
			});
			return PositionMatches(presenter);
		}

		private bool PositionMatches(RemoteMotionPresenter presenter)
		{
			bool renderMatches = presenter.transform.position == Position
			                     && (presenter.AnimController == null
			                         || presenter.AnimController.FlipX == FlipX
			                         && presenter.AnimController.FlipY == FlipY)
			                     && (presenter.Navigator == null
			                         || presenter.Navigator.CurrentNavType == NavType);
			return renderMatches && presenter.AuthoritativePosition == Position
			       && presenter.AuthoritativeFlipX == FlipX
			       && presenter.AuthoritativeFlipY == FlipY
			       && presenter.AuthoritativeNavType == NavType;
		}

		private static List<StorageSnapshotSync.SnapshotRequest> BuildStorageRequests(
			IReadOnlyList<PreparedKeyframe> keyframes)
		{
			var requests = new List<StorageSnapshotSync.SnapshotRequest>();
			foreach (PreparedKeyframe keyframe in keyframes)
			{
				for (int index = 0; index < keyframe.Storages.Length; index++)
				{
					requests.Add(new StorageSnapshotSync.SnapshotRequest
					{
						Storage = keyframe.Storages[index],
						Payload = keyframe.Packet.StorageSnapshots[index],
						SnapshotRevision = keyframe.Packet.StorageRevision,
						ApplyChanges = false,
					});
				}
			}
			return requests;
		}

		private static KeyframeStorageRevisionCommit SetStorageApplyDecisions(
			IReadOnlyList<PreparedKeyframe> keyframes,
			IReadOnlyList<StorageSnapshotSync.SnapshotRequest> requests)
		{
			int requestIndex = 0;
			var commit = new Dictionary<int, ulong>();
			foreach (PreparedKeyframe keyframe in keyframes)
			{
				bool apply = keyframe.Storages.Length > 0
				             && NetworkIdentityRegistry.ShouldAcceptStorageSnapshotRevision(
					             keyframe.Packet.NetId, keyframe.Packet.StorageRevision);
				if (apply)
					commit.Add(keyframe.Packet.NetId, keyframe.Packet.StorageRevision);
				for (int index = 0; index < keyframe.Storages.Length; index++)
					requests[requestIndex++].ApplyChanges = apply;
			}
			return new KeyframeStorageRevisionCommit(commit);
		}

		private void Validate()
		{
			SoakHashWire.ValidateMarker(RunId, SampleId, 0f);
			if (EntryIndex < 0 || EntryIndex >= SoakHashDomainKeyframeBeginPacket.MaxEntries
			    || NetId == 0 || LifecycleSnapshot == null
			    || LifecycleSnapshot.NetId != NetId || LifecycleSnapshot.Hash == 0
			    || LifecycleSnapshot.Revision == 0
			    || LifecycleSnapshot.WorldId != WorldId
			    || LifecycleSnapshot.Position != Position
			    || !Finite(Position.x) || !Finite(Position.y) || !Finite(Position.z)
			    || HasPosition && (PositionSequence <= 0 || NavType >= NavType.NumNavTypes)
			    || HasClusterLocation && !RocketSettingsPacketData.CoordinateWithinBounds(
				    ClusterQ, ClusterR)
			    || HasRocketSettings && (RocketSettings == null || !RocketSettings.IsWireValid())
			    || StorageSnapshots.Count > MaxStorageCount
			    || (StorageSnapshots.Count == 0) != (StorageRevision == 0))
				throw new InvalidDataException("Invalid soak hash-domain keyframe metadata");
			int totalBytes = 0;
			foreach (byte[] snapshot in StorageSnapshots)
			{
				ValidateSnapshotLength(snapshot?.Length ?? -1, totalBytes);
				totalBytes += snapshot.Length;
			}
		}

		private static bool Finite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);

		private static void ValidateSnapshotLength(int length, int previousBytes)
		{
			if (length <= 0 || length > MaxStorageSnapshotBytes
			    || previousBytes > MaxTotalSnapshotBytes - length)
				throw new InvalidDataException("Invalid soak storage keyframe length");
		}
	}
}
#endif
