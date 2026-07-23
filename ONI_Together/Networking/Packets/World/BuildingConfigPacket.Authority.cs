using ONI_Together.DebugTools;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World.Handlers;
using Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Threading;

namespace ONI_Together.Networking.Packets.World
{
	public partial class BuildingConfigPacket
	{
		private struct ConfigKey : IEquatable<ConfigKey>
		{
			internal int NetId;
			internal ulong LifecycleRevision;
			internal int ConfigHash;
			internal int SliderIndex;

			internal static ConfigKey From(BuildingConfigPacket packet) => new()
			{
				NetId = packet.NetId,
				LifecycleRevision = packet.TargetLifecycleRevision,
				ConfigHash = packet.ConfigHash,
				SliderIndex = packet.SliderIndex
			};

			public bool Equals(ConfigKey other)
				=> NetId == other.NetId && LifecycleRevision == other.LifecycleRevision
				   && ConfigHash == other.ConfigHash && SliderIndex == other.SliderIndex;

			public override bool Equals(object obj) => obj is ConfigKey other && Equals(other);

			public override int GetHashCode()
			{
				unchecked
				{
					int hash = NetId;
					hash = (hash * 397) ^ LifecycleRevision.GetHashCode();
					hash = (hash * 397) ^ ConfigHash;
					return (hash * 397) ^ SliderIndex;
				}
			}
		}

		private readonly struct RequestStreamKey : IEquatable<RequestStreamKey>
		{
			private readonly ulong _senderId;
			private readonly long _connectionGeneration;

			internal RequestStreamKey(ulong senderId, long connectionGeneration)
			{
				_senderId = senderId;
				_connectionGeneration = connectionGeneration;
			}

			public bool Equals(RequestStreamKey other)
				=> _senderId == other._senderId
				   && _connectionGeneration == other._connectionGeneration;

			public override bool Equals(object obj)
				=> obj is RequestStreamKey other && Equals(other);

			public override int GetHashCode()
				=> unchecked((_senderId.GetHashCode() * 397) ^ _connectionGeneration.GetHashCode());
		}

		private static readonly Dictionary<ConfigKey, ulong> HostRevisions = [];
		private static readonly Dictionary<ConfigKey, ulong> ClientRevisions = [];
		private static readonly Dictionary<ConfigKey, BuildingConfigPacket> HostSnapshots = [];
		private static readonly Dictionary<RequestStreamKey, ulong> RequestCursors = [];
		private static long _nextClientRequestId;

		private void PrepareForSerialize()
		{
			if (HasValidAuthorityFields())
				return;
			if (!MultiplayerSession.InSession)
				throw new InvalidDataException("Building config authority requires a live session");
			if (MultiplayerSession.IsHost)
				PrepareHostLocalOutcome();
			else
				PrepareClientRequest();
		}

		private void PrepareHostLocalOutcome()
		{
			BindAuthoritativeIdentity();
			if (!TryGetCurrentIdentity(NetId, 0, out NetworkIdentity identity))
				throw new InvalidDataException("Building config host identity is not current");
			TargetLifecycleRevision = identity.LifecycleRevision;
			Sender = NetworkConfig.GetLocalID();
			ClientRequestId = 0;
			BaseStateRevision = 0;
			StateRevision = NextHostRevision(ConfigKey.From(this));
			RememberHostSnapshot();
		}

		private void PrepareClientRequest()
		{
			BindAuthoritativeIdentity();
			if (!TryGetCurrentIdentity(NetId, 0, out NetworkIdentity identity))
				throw new InvalidDataException("Building config client identity is not current");
			TargetLifecycleRevision = identity.LifecycleRevision;
			Sender = NetworkConfig.GetLocalID();
			ClientRequestId = NextClientRequestId();
			ConfigKey key = ConfigKey.From(this);
			BaseStateRevision = GetRevision(ClientRevisions, key);
			StateRevision = 0;
		}

		private static ulong NextClientRequestId()
		{
			long next = Interlocked.Increment(ref _nextClientRequestId);
			if (next <= 0)
				throw new InvalidOperationException("Building config request id space exhausted");
			return (ulong)next;
		}

		private bool HasValidAuthorityFields()
		{
			if (Sender == 0 || TargetLifecycleRevision == 0
			    || TargetLifecycleRevision > long.MaxValue
			    || StateRevision > long.MaxValue || ClientRequestId > long.MaxValue
			    || BaseStateRevision > long.MaxValue)
				return false;
			bool request = ClientRequestId != 0;
			return request
				? StateRevision == 0
				: StateRevision != 0 && BaseStateRevision == 0;
		}

		private void DispatchWithAuthority(DispatchContext context)
		{
			if (MultiplayerSession.IsHost && !context.SenderIsHost)
			{
				HandleClientRequest(context);
				return;
			}
			if (MultiplayerSession.IsClient && context.SenderIsHost)
			{
				ApplyHostOutcome(context);
				return;
			}
			DebugConsole.LogWarning("[BuildingConfigPacket] rejected invalid authority direction");
		}

		private void HandleClientRequest(DispatchContext context)
		{
			if (!IsCurrentClientRequest(context) || !AcceptRequestStream(context, ClientRequestId))
				return;
			ConfigKey key = ConfigKey.From(this);
			if (!TryGetCurrentIdentity(NetId, TargetLifecycleRevision, out NetworkIdentity identity))
				return;
			if (BaseStateRevision != GetRevision(HostRevisions, key))
			{
				TrySendHostCorrection(key, context.SenderId);
				return;
			}
			BuildingConfigMutationSemantics semantics =
				BuildingConfigHandlerRegistry.GetMutationSemantics(ConfigHash);
			if (!ApplyPacket(identity))
				return;
			PromoteRequestToOutcome(identity, semantics);
			SendAcceptedOutcome(context.SenderId);
		}

		private void SendAcceptedOutcome(ulong requesterId)
		{
			PacketSender.SendToPlayer(requesterId, this);
			PacketSender.SendToAllExcluding(this,
				[MultiplayerSession.HostUserID, requesterId]);
		}

		private bool IsCurrentClientRequest(DispatchContext context)
		{
			MultiplayerPlayer player = MultiplayerSession.GetPlayer(context.SenderId);
			return ClientRequestId != 0 && StateRevision == 0 && Sender == context.SenderId
			       && context.ConnectionGeneration > 0 && PacketHandler.IsCurrentDispatchContext(context)
			       && player != null && player.ProtocolVerified
			       && SyncBarrier.IsExactReady(player.readyState);
		}

		private static bool AcceptRequestStream(DispatchContext context, ulong requestId)
		{
			var key = new RequestStreamKey(context.SenderId, context.ConnectionGeneration);
			if (!RequestCursors.TryGetValue(key, out ulong current))
			{
				if (requestId != 1)
					return false;
				RequestCursors[key] = requestId;
				return true;
			}
			if (current == long.MaxValue || requestId != current + 1)
				return false;
			RequestCursors[key] = requestId;
			return true;
		}

		private void PromoteRequestToOutcome(
			NetworkIdentity identity,
			BuildingConfigMutationSemantics semantics)
		{
			TargetLifecycleRevision = identity.LifecycleRevision;
			Sender = NetworkConfig.GetLocalID();
			ClientRequestId = 0;
			BaseStateRevision = 0;
			StateRevision = NextHostRevision(ConfigKey.From(this));
			if (semantics == BuildingConfigMutationSemantics.StateAssignment)
				RememberHostSnapshot();
		}

		private void ApplyHostOutcome(DispatchContext context)
			=> TryApplyHostOutcome(context);

		private bool TryApplyHostOutcome(DispatchContext context)
		{
			if (ClientRequestId != 0 || StateRevision == 0 || Sender != context.SenderId
			    || !PacketHandler.IsCurrentDispatchContext(context))
				return false;
			if (!TryGetCurrentIdentity(NetId, TargetLifecycleRevision, out NetworkIdentity identity))
				return false;
			ConfigKey key = ConfigKey.From(this);
			if (!NetworkIdentityRegistry.IsNewerRevision(GetRevision(ClientRevisions, key), StateRevision)
			    || !ApplyPacket(identity))
				return false;
			ClientRevisions[key] = StateRevision;
			return true;
		}

#if DEBUG
		internal void PrepareProfileSend() => PrepareForSerialize();

		internal bool ApplyProfileClient()
			=> MultiplayerSession.IsClient && PacketHandler.CurrentContext.SenderIsHost
			   && TryApplyHostOutcome(PacketHandler.CurrentContext);
#endif

		private bool ApplyPacket(NetworkIdentity identity)
		{
			BeginApplyingPacket();
			try
			{
				if (!ApplyConfig(identity.gameObject))
					return false;
				RefreshSideScreenIfOpen(identity.gameObject);
				return true;
			}
			finally
			{
				EndApplyingPacket();
			}
		}

#if DEBUG
		private string GetBuildingConfigEvidenceScenario()
		{
			if (ConfigHash == NetworkingHash.ForConfigKey("DoorState")
			    || ConfigHash == NetworkingHash.ForConfigKey("DoorUnseal"))
				return "door";
			if (ConfigHash == NetworkingHash.ForConfigKey("UprootPlant"))
				return "uproot";
			if (ConfigHash == NetworkingHash.ForConfigKey("QueueToggleable")
			    || ConfigHash == NetworkingHash.ForConfigKey("ToggleableChange"))
				return "toggle";
			return "building-config";
		}

		private void LogBuildingConfigEvidence(
			string phase, ulong revision, string entryId)
		{
			string scenario = GetBuildingConfigEvidenceScenario();
			IntegrationScenarioEvidenceCore.Log(
				TypedEvidenceRuntimeContext.Create(
					scenario: scenario, phase: phase, revision: (long)revision,
					target: GetBuildingConfigEvidenceTarget(scenario),
					state: GetBuildingConfigEvidenceState(scenario),
					entryId: entryId));
		}

		private ITypedEvidenceTarget GetBuildingConfigEvidenceTarget(string scenario)
		{
			return scenario switch
			{
				"door" => new DoorTarget { TargetNetId = NetId },
				"uproot" => new UprootTarget { TargetNetId = NetId },
				"toggle" => new ToggleTarget { TargetNetId = NetId },
				_ => new BuildingConfigTarget { TargetNetId = NetId },
			};
		}

		private ITypedEvidenceState GetBuildingConfigEvidenceState(string scenario)
		{
			long lifecycle = (long)TargetLifecycleRevision;
			long stateRevision = (long)StateRevision;
			return scenario switch
			{
				"door" => new DoorState
				{
					LifecycleRevision = lifecycle, StateRevision = stateRevision,
					Control = GetDoorControl(),
				},
				"uproot" => new UprootState
				{
					LifecycleRevision = lifecycle, StateRevision = stateRevision,
					Uprooted = Value > 0.5f,
				},
				"toggle" => new ONI_Together.DebugTools.ToggleState
				{
					LifecycleRevision = lifecycle, StateRevision = stateRevision,
					Toggled = Value > 0.5f,
				},
				_ => new BuildingConfigState
				{
					LifecycleRevision = lifecycle,
					BaseRevision = (long)GetEvidenceBaseRevision(),
					StateRevision = stateRevision,
					ConfigKind = ConfigHash.ToString(CultureInfo.InvariantCulture),
					ConfigValue = GetConfigValue(),
				},
			};
		}

		private ulong GetEvidenceBaseRevision()
			=> BaseStateRevision != 0 || StateRevision == 0
				? BaseStateRevision : StateRevision - 1;

		private string GetDoorControl()
		{
			if (ConfigHash == NetworkingHash.ForConfigKey("DoorUnseal"))
				return "Unseal";
			return Enum.GetName(typeof(Door.ControlState), (int)Value)
			       ?? ((int)Value).ToString(CultureInfo.InvariantCulture);
		}

		private string GetConfigValue()
		{
			if (ConfigType == BuildingConfigType.String)
				return StringValue ?? string.Empty;
			if (ConfigType == BuildingConfigType.Boolean)
				return Value > 0.5f ? "true" : "false";
			return Value.ToString("R", CultureInfo.InvariantCulture);
		}

		private void LogOriginalBlockedEvidence()
			=> LogBuildingConfigEvidence(
				"client-original-blocked", StateRevision,
				"sync:f60e38b805c1052cff0fec0d");

		private void LogHostBuildingConfigEvidence(string entryId)
		{
			LogBuildingConfigEvidence("host-submit", StateRevision, entryId);
			LogBuildingConfigEvidence("final-state", StateRevision, entryId);
		}

		private string GetLocalBuildingConfigSendEntryId()
		{
			if (ConfigHash == NetworkingHash.ForConfigKey("DoorState")) return "sync:b55ce4ea9939fa1b6b296c23";
			if (ConfigHash == NetworkingHash.ForConfigKey("DoorUnseal")) return "sync:2ae0aa3184b40e660ee6d2c2";
			if (ConfigHash == NetworkingHash.ForConfigKey("UprootPlant")) return "sync:9dce9681c9df9ef0bf16b56e";
			if (ConfigHash == NetworkingHash.ForConfigKey("QueueToggleable")) return "sync:968603477131150d6f009d57";
			if (ConfigHash == NetworkingHash.ForConfigKey("ToggleableChange")) return "sync:de5ca17eee1407e93107184b";
			if (ConfigHash == NetworkingHash.ForConfigKey("Capacity")) return "sync:b44f795d15c4ae277c89cd4a";
			if (ConfigHash == NetworkingHash.ForConfigKey("Threshold")) return "sync:04ad01ef664d38411a873683";
			if (ConfigHash == NetworkingHash.ForConfigKey("ThresholdDirection")) return "sync:337c4446de4c5a02c9e0053f";
			if (ConfigHash == NetworkingHash.ForConfigKey("Checkbox")) return "sync:e1bc60fbb54bc74780042e43";
			return "sync:6eaef0b4077bfdc8e29a6aff";
		}

		private void LogAppliedBuildingConfigEvidence(ConfigKey key)
		{
			const string entryId = "sync:f60e38b805c1052cff0fec0d";
			LogBuildingConfigEvidence("revision-accepted", StateRevision, entryId);
			LogBuildingConfigEvidence("client-apply", StateRevision, entryId);
			LogBuildingConfigEvidence("final-state", StateRevision, entryId);
			ulong current = GetRevision(ClientRevisions, key);
			if (!NetworkIdentityRegistry.IsNewerRevision(current, current))
				LogBuildingConfigEvidence("revision-duplicate", current, entryId);
			ulong older = current - 1;
			if (!NetworkIdentityRegistry.IsNewerRevision(current, older))
				LogBuildingConfigEvidence("revision-out-of-order", older, entryId);
		}
#endif

		private static bool TryGetCurrentIdentity(
			int netId,
			ulong expectedLifecycle,
			out NetworkIdentity identity)
		{
			identity = null;
			if (!NetworkIdentityRegistry.TryGet(netId, out NetworkIdentity candidate)
			    || candidate == null || candidate.LifecycleRevision == 0
			    || candidate.IsLifecycleTerminal || !NetworkIdentityRegistry.IsRegistered(candidate, netId)
			    || NetworkIdentityRegistry.IsLifecycleTombstoned(netId))
				return false;
			ulong lifecycle = NetworkIdentityRegistry.GetLastLifecycleRevision(netId);
			if (lifecycle != candidate.LifecycleRevision
			    || expectedLifecycle != 0 && lifecycle != expectedLifecycle)
				return false;
			identity = candidate;
			return true;
		}

		private static ulong NextHostRevision(ConfigKey key)
		{
			ulong current = GetRevision(HostRevisions, key);
			if (current == long.MaxValue)
				throw new InvalidOperationException("Building config revision space exhausted");
			ulong next = current + 1;
			HostRevisions[key] = next;
			return next;
		}

		private static ulong GetRevision(Dictionary<ConfigKey, ulong> revisions, ConfigKey key)
			=> revisions.TryGetValue(key, out ulong revision) ? revision : 0;

		private void RememberHostSnapshot()
		{
			if (BuildingConfigHandlerRegistry.GetMutationSemantics(ConfigHash)
			    == BuildingConfigMutationSemantics.StateAssignment)
				HostSnapshots[ConfigKey.From(this)] = Clone();
		}

		private BuildingConfigPacket Clone() => new()
		{
			Sender = Sender,
			NetId = NetId,
			TargetLifecycleRevision = TargetLifecycleRevision,
			StateRevision = StateRevision,
			Cell = Cell,
			DeterministicBuildingId = DeterministicBuildingId,
			ConfigHash = ConfigHash,
			Value = Value,
			ConfigType = ConfigType,
			SliderIndex = SliderIndex,
			ReferenceNetId = ReferenceNetId,
			StringValue = StringValue,
			SecondaryStringValue = SecondaryStringValue
		};

		private static bool TrySendHostCorrection(ConfigKey key, ulong playerId)
			=> HostSnapshots.TryGetValue(key, out BuildingConfigPacket snapshot)
			   && PacketSender.SendToPlayer(playerId, snapshot);

		internal static void SendPeriodicSnapshots()
		{
			if (!MultiplayerSession.IsHost)
				return;
			foreach (BuildingConfigPacket snapshot in HostSnapshots.Values)
				PacketSender.SendToAllClients(snapshot);
		}

		internal static void ResetClientRevisionState()
			=> ClientRevisions.Clear();

		private static void ResetAuthorityState()
		{
			HostRevisions.Clear();
			ClientRevisions.Clear();
			HostSnapshots.Clear();
			RequestCursors.Clear();
			Interlocked.Exchange(ref _nextClientRequestId, 0);
		}

		internal static ulong GetClientRevisionForTests(
			int netId,
			ulong lifecycleRevision,
			int configHash)
		{
			var packet = new BuildingConfigPacket
			{
				NetId = netId,
				TargetLifecycleRevision = lifecycleRevision,
				ConfigHash = configHash
			};
			return GetRevision(ClientRevisions, ConfigKey.From(packet));
		}
	}
}
