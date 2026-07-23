using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Components;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using ONI_Together.Networking.Packets.Tools.Prioritize;
using ONI_Together.DebugTools;

namespace ONI_Together.Networking.Packets.World
{
	public sealed class PrioritizeStatePacket : IPacket, IBulkablePacket, IHostOnlyPacket
	{
		internal const int MaxPriorityCount = 64;
		internal const string RevisionDomain = "priority";

		private struct RevisionCursor
		{
			internal ulong LifecycleRevision;
			internal ulong StateRevision;
		}

		internal struct RequestReceipt
		{
			internal ulong SenderId;
			internal ulong RequestId;
		}

		internal struct HostRequestContext
		{
			internal NetworkIdentity Identity;
			internal Prioritizable Prioritizable;
			internal PrioritySetting Setting;
			internal RequestReceipt Receipt;
		}

		private struct SnapshotCaptureContext
		{
			internal bool AdvanceRevision;
			internal RequestReceipt Receipt;
		}

		private struct PacketBuildContext
		{
			internal PrioritySetting Setting;
			internal ulong StateRevision;
			internal RequestReceipt Receipt;
		}

		private static readonly Dictionary<int, RevisionCursor> HostRevisions = [];
		private static readonly Dictionary<int, RevisionCursor> ClientRevisions = [];
		private static readonly Dictionary<int, ulong> PendingClientRequests = [];
		private static readonly HashSet<ulong> SnapshotRecipients = [];
		private static long nextClientRequestId;
		private static int applyDepth;
		private static int hostMutationDepth;

		public struct PriorityData
		{
			public int NetId;
			public ulong LifecycleRevision;
			public ulong StateRevision;
			public int PriorityClass;
			public int PriorityValue;
		}

		public List<PriorityData> Priorities = new List<PriorityData>();
		public ulong RequestSenderId;
		public ulong ClientRequestId;
		public static bool IsApplying => applyDepth > 0;
		internal static bool IsHostMutationSuppressed => hostMutationDepth > 0;
		public int MaxPackSize => 64;
		public uint IntervalMs => 50;

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			ValidatePacket();
			writer.Write(RequestSenderId);
			writer.Write(ClientRequestId);
			writer.Write(Priorities.Count);
			foreach (var p in Priorities)
			{
				writer.Write(p.NetId);
				writer.Write(p.LifecycleRevision);
				writer.Write(p.StateRevision);
				writer.Write(p.PriorityClass);
				writer.Write(p.PriorityValue);
			}
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			RequestSenderId = reader.ReadUInt64();
			ClientRequestId = reader.ReadUInt64();
			int count = reader.ReadInt32();
			if (count < 0 || count > MaxPriorityCount)
				throw new InvalidDataException($"Invalid priority state count: {count}");
			Priorities = new List<PriorityData>(count);
			for (int i = 0; i < count; i++)
			{
				var priority = new PriorityData
				{
					NetId = reader.ReadInt32(),
					LifecycleRevision = reader.ReadUInt64(),
					StateRevision = reader.ReadUInt64(),
					PriorityClass = reader.ReadInt32(),
					PriorityValue = reader.ReadInt32()
				};
				Validate(priority);
				Priorities.Add(priority);
			}
			ValidatePacket();
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			if (!ShouldApply(MultiplayerSession.IsHost, PacketHandler.CurrentContext.SenderIsHost))
				return;

			AcknowledgeClientRequest();
			try
			{
				applyDepth++;
				foreach (var p in Priorities)
					TryApplyEntry(p);
			}
			finally
			{
				applyDepth--;
			}
		}

		internal static bool ShouldApply(bool localIsHost, bool senderIsHost)
			=> !localIsHost && senderIsHost;

		private void ValidatePacket()
		{
			if (Priorities == null || Priorities.Count <= 0
			    || Priorities.Count > MaxPriorityCount
			    || (RequestSenderId == 0) != (ClientRequestId == 0)
			    || ClientRequestId > long.MaxValue)
				throw new InvalidDataException("Invalid priority state packet");
			foreach (PriorityData priority in Priorities)
				Validate(priority);
		}

		private static void Validate(PriorityData priority)
		{
			if (priority.NetId == 0 || priority.LifecycleRevision == 0
			    || priority.LifecycleRevision > long.MaxValue
			    || priority.StateRevision == 0 || priority.StateRevision > long.MaxValue ||
			    !PriorityAuthority.IsValidStatePriority(priority.PriorityClass, priority.PriorityValue))
				throw new InvalidDataException("Invalid priority state entry");
		}

		private static bool TryApplyEntry(PriorityData priority)
		{
			ulong lifecycle = NetworkIdentityRegistry.GetLastLifecycleRevision(priority.NetId);
			if (lifecycle != priority.LifecycleRevision
			    || NetworkIdentityRegistry.IsLifecycleTombstoned(priority.NetId)
			    || !NetworkIdentityRegistry.TryGet(priority.NetId, out NetworkIdentity identity)
			    || identity.LifecycleRevision != lifecycle
			    || !NetworkIdentityRegistry.IsRegistered(identity, priority.NetId)
			    || !identity.TryGetComponent(out Prioritizable prioritizable))
				return false;
			ulong baseRevision = GetClientRevision(priority.NetId, lifecycle);
			if (!ShouldAcceptClientRevision(priority.NetId, lifecycle, priority.StateRevision))
				return false;

			var setting = new PrioritySetting(
				(PriorityScreen.PriorityClass)priority.PriorityClass, priority.PriorityValue);
			if (!prioritizable.GetMasterPriority().Equals(setting))
				prioritizable.SetMasterPriority(setting);
			ClientRevisions[priority.NetId] = new RevisionCursor
			{
				LifecycleRevision = lifecycle,
				StateRevision = priority.StateRevision
			};
#if DEBUG
			LogAppliedPriorityEvidence(priority, baseRevision);
#endif
			return true;
		}

		private static bool ShouldAcceptClientRevision(
			int netId, ulong lifecycleRevision, ulong stateRevision)
		{
			ulong current = ClientRevisions.TryGetValue(netId, out RevisionCursor cursor)
			                && cursor.LifecycleRevision == lifecycleRevision
				? cursor.StateRevision : 0;
			return NetworkIdentityRegistry.IsNewerRevision(current, stateRevision);
		}

		internal static ulong GetClientRevision(int netId, ulong lifecycleRevision)
			=> ClientRevisions.TryGetValue(netId, out RevisionCursor cursor)
			   && cursor.LifecycleRevision == lifecycleRevision
				? cursor.StateRevision : 0;

		internal static ulong BeginClientRequest(int netId)
		{
			long next = Interlocked.Increment(ref nextClientRequestId);
			if (next <= 0)
				throw new InvalidOperationException("Priority request id space exhausted");
			PendingClientRequests[netId] = (ulong)next;
			return (ulong)next;
		}

		private void AcknowledgeClientRequest()
		{
			if (RequestSenderId != MultiplayerSession.LocalUserID || ClientRequestId == 0)
				return;
			foreach (PriorityData priority in Priorities)
				if (PendingClientRequests.TryGetValue(priority.NetId, out ulong pending)
				    && pending == ClientRequestId)
					PendingClientRequests.Remove(priority.NetId);
		}

		internal static ulong GetHostRevision(int netId, ulong lifecycleRevision)
			=> HostRevisions.TryGetValue(netId, out RevisionCursor cursor)
			   && cursor.LifecycleRevision == lifecycleRevision
				? cursor.StateRevision : 0;

		private static ulong NextHostRevision(int netId, ulong lifecycleRevision, bool advance)
		{
			ulong current = GetHostRevision(netId, lifecycleRevision);
			if (current == 0 || advance)
			{
				if (current == long.MaxValue)
					throw new InvalidOperationException("Priority state revision space exhausted");
				current++;
				HostRevisions[netId] = new RevisionCursor
				{
					LifecycleRevision = lifecycleRevision,
					StateRevision = current
				};
			}
			return current;
		}

		private static bool TryCaptureHostSnapshot(
			NetworkIdentity identity, SnapshotCaptureContext context,
			out PrioritizeStatePacket packet)
		{
			packet = null;
			if (!IsCurrentHostIdentity(identity)
			    || !identity.TryGetComponent(out Prioritizable prioritizable)
			    || !prioritizable.IsPrioritizable())
				return false;
			PrioritySetting setting = prioritizable.GetMasterPriority();
			if (!PriorityAuthority.IsValidStatePriority(
				    (int)setting.priority_class, setting.priority_value))
				return false;
			ulong revision = NextHostRevision(
				identity.NetId, identity.LifecycleRevision, context.AdvanceRevision);
			packet = CreatePacket(identity, new PacketBuildContext
			{
				Setting = setting,
				StateRevision = revision,
				Receipt = context.Receipt
			});
			return true;
		}

		internal static bool IsCurrentHostIdentity(NetworkIdentity identity)
			=> MultiplayerSession.IsHost && identity != null && identity.NetId != 0
			   && identity.LifecycleRevision != 0 && !identity.IsLifecycleTerminal
			   && NetworkIdentityRegistry.IsRegistered(identity, identity.NetId)
			   && NetworkIdentityRegistry.GetLastLifecycleRevision(identity.NetId)
			      == identity.LifecycleRevision
			   && !NetworkIdentityRegistry.IsLifecycleTombstoned(identity.NetId);

		private static PrioritizeStatePacket CreatePacket(
			NetworkIdentity identity, PacketBuildContext context)
		{
			var packet = new PrioritizeStatePacket
			{
				RequestSenderId = context.Receipt.SenderId,
				ClientRequestId = context.Receipt.RequestId
			};
			packet.Priorities.Add(new PriorityData
			{
				NetId = identity.NetId,
				LifecycleRevision = identity.LifecycleRevision,
				StateRevision = context.StateRevision,
				PriorityClass = (int)context.Setting.priority_class,
				PriorityValue = context.Setting.priority_value
			});
			return packet;
		}

		internal static void PublishHostMutation(NetworkIdentity identity)
		{
			if (TryCaptureHostSnapshot(identity, new SnapshotCaptureContext
			    {
				    AdvanceRevision = true
			    }, out PrioritizeStatePacket packet))
			{
#if DEBUG
				LogHostPriorityEvidence(
					packet.Priorities[0], "sync:f307ba1296c754fb5bffc5b1");
#endif
				SendToViewingClients(identity, packet, 0);
			}
		}

		internal static bool ApplyHostRequest(HostRequestContext request)
		{
			try
			{
				hostMutationDepth++;
				request.Prioritizable.SetMasterPriority(request.Setting);
			}
			finally
			{
				hostMutationDepth--;
			}
			if (!TryCaptureHostSnapshot(request.Identity, new SnapshotCaptureContext
			    {
				    AdvanceRevision = true,
				    Receipt = request.Receipt
			    }, out PrioritizeStatePacket ack))
				return false;
#if DEBUG
			LogHostPriorityEvidence(
				ack.Priorities[0], "sync:d9befe16f0438ee10c4e871f");
#endif
			PacketSender.SendToPlayer(request.Receipt.SenderId, ack);
			PrioritizeStatePacket outcome = CreatePacket(request.Identity, new PacketBuildContext
			{
				Setting = request.Prioritizable.GetMasterPriority(),
				StateRevision = ack.Priorities[0].StateRevision
			});
			SendToViewingClients(request.Identity, outcome, request.Receipt.SenderId);
			return true;
		}

#if DEBUG
		private static void LogHostPriorityEvidence(PriorityData priority, string entryId)
		{
			ulong baseRevision = priority.StateRevision - 1;
			LogPriorityEvidence(priority, baseRevision, "host-submit", priority.StateRevision, entryId);
			LogPriorityEvidence(priority, baseRevision, "final-state", priority.StateRevision, entryId);
		}

		private static void LogAppliedPriorityEvidence(PriorityData priority, ulong baseRevision)
		{
			const string entryId = "sync:f60e38b805c1052cff0fec0d";
			LogPriorityEvidence(priority, baseRevision, "revision-accepted", priority.StateRevision, entryId);
			LogPriorityEvidence(priority, baseRevision, "client-apply", priority.StateRevision, entryId);
			LogPriorityEvidence(priority, baseRevision, "final-state", priority.StateRevision, entryId);
			if (!ShouldAcceptClientRevision(priority.NetId, priority.LifecycleRevision,
				    priority.StateRevision))
				LogPriorityEvidence(
					priority, baseRevision, "revision-duplicate", priority.StateRevision, entryId);
			ulong older = priority.StateRevision - 1;
			if (!ShouldAcceptClientRevision(priority.NetId, priority.LifecycleRevision, older))
				LogPriorityEvidence(priority, baseRevision, "revision-out-of-order", older, entryId);
		}

		internal static void LogBlockedPriorityEvidence(
			PrioritizeTargetRequestPacket request, string entryId)
		{
			var priority = new PriorityData
			{
				NetId = request.NetId,
				LifecycleRevision = request.TargetLifecycleRevision,
				StateRevision = request.BasePriorityRevision,
				PriorityClass = request.PriorityClass,
				PriorityValue = request.PriorityValue,
			};
			LogPriorityEvidence(
				priority, request.BasePriorityRevision, "client-original-blocked",
				request.BasePriorityRevision, entryId);
		}

		private static void LogPriorityEvidence(
			PriorityData priority, ulong baseRevision, string phase,
			ulong revision, string entryId)
		{
			IntegrationScenarioEvidenceCore.Log(
				TypedEvidenceRuntimeContext.Create(
					scenario: "priority", phase: phase, revision: (long)revision,
					target: new PriorityTarget { TargetNetId = priority.NetId },
					state: new PriorityState
					{
						LifecycleRevision = (long)priority.LifecycleRevision,
						BaseRevision = (long)baseRevision,
						StateRevision = (long)priority.StateRevision,
						Priority = priority.PriorityValue,
					},
					entryId: entryId));
		}
#endif

		internal static bool SendHostCorrection(
			NetworkIdentity identity, ulong requesterId, ulong clientRequestId)
		{
			return TryCaptureHostSnapshot(identity, new SnapshotCaptureContext
			       {
				       Receipt = new RequestReceipt
				       {
					       SenderId = requesterId,
					       RequestId = clientRequestId
				       }
			       }, out PrioritizeStatePacket packet)
			       && PacketSender.SendToPlayer(requesterId, packet);
		}

		internal static void SendPeriodicSnapshot(NetworkIdentity identity)
		{
			if (TryCaptureHostSnapshot(
				    identity, default, out PrioritizeStatePacket packet))
				SendToViewingClients(identity, packet, 0);
		}

		private static void SendToViewingClients(
			NetworkIdentity identity, PrioritizeStatePacket packet, ulong excludedId)
		{
			int cell = Grid.PosToCell(identity.gameObject);
			if (!Grid.IsValidCell(cell) || WorldStateSyncer.Instance == null)
				return;
			WorldStateSyncer.Instance.GetClientsViewingCell(cell, SnapshotRecipients);
			foreach (ulong playerId in SnapshotRecipients)
				if (playerId != excludedId)
					PacketSender.SendToPlayer(playerId, packet);
		}

		internal static void ResetClientRevisionState()
		{
			ClientRevisions.Clear();
			PendingClientRequests.Clear();
		}

		internal static void ResetSessionState()
		{
			HostRevisions.Clear();
			ResetClientRevisionState();
			SnapshotRecipients.Clear();
			Interlocked.Exchange(ref nextClientRequestId, 0);
			applyDepth = 0;
			hostMutationDepth = 0;
		}
	}
}
