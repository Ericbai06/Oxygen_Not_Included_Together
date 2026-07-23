using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Synchronization;
using Shared.Interfaces.Networking;
using Shared.Profiling;

namespace ONI_Together.Networking.Packets.World
{
	public sealed class ResourceCountPacket : IPacket, IHostOnlyPacket
	{
		private const int MaxResourceCount = 16384;
		private const int MaxTagLength = 256;
		private static ulong _clientRevision;
		private static long _clientEpoch;

		public ulong Revision;
		public Dictionary<string, float> Resources = new Dictionary<string, float>();
#if DEBUG
		internal string ScenarioActionProfile = string.Empty;
#endif

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();
			if (Revision == 0)
				Revision = NetworkIdentityRegistry.NextAuthorityRevision();
			Validate();
			writer.Write(Revision);
			writer.Write(Resources.Count);
			foreach (KeyValuePair<string, float> resource in Resources)
			{
				writer.Write(resource.Key);
				writer.Write(resource.Value);
			}
#if DEBUG
			writer.Write(ScenarioActionProfile ?? string.Empty);
#endif
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();
			Revision = reader.ReadUInt64();
			int count = reader.ReadInt32();
			if (Revision == 0 || count < 0 || count > MaxResourceCount)
				throw new InvalidDataException("Invalid resource snapshot metadata");
			Resources.Clear();
			for (int index = 0; index < count; index++)
			{
				string key = reader.ReadString();
				float value = reader.ReadSingle();
				if (string.IsNullOrEmpty(key) || key.Length > MaxTagLength
				    || !IsFinite(value) || value < 0f || !Resources.TryAdd(key, value))
					throw new InvalidDataException("Invalid resource snapshot entry");
			}
#if DEBUG
			ScenarioActionProfile = reader.ReadString();
#endif
		}

		public void OnDispatched()
		{
#if DEBUG
			if (!string.IsNullOrEmpty(ScenarioActionProfile))
			{
				if (ScenarioActionReceiverGate.TryEnter(ScenarioActionProfile, "inventory"))
					InventoryActionFlow.ExecuteClient(this);
				return;
			}
			ApplyRuntimePacket();
#else
			ApplyRuntimePacket();
#endif
		}

		internal bool ApplyRuntimePacket()
		{
			using var _ = Profiler.Scope();
			DispatchContext context = PacketHandler.CurrentContext;
			if (MultiplayerSession.IsHost || !context.SenderIsHost
			    || !PacketHandler.IsCurrentDispatchContext(context))
				return false;

			if (_clientEpoch != context.SessionEpoch)
			{
				_clientEpoch = context.SessionEpoch;
				_clientRevision = 0;
			}
			if (!NetworkIdentityRegistry.IsNewerRevision(_clientRevision, Revision))
				return false;

			_clientRevision = Revision;
			ResourceSyncer.ClientResources = new Dictionary<string, float>(Resources);
			ApplyDiscoveries();
			return true;
		}

		private void ApplyDiscoveries()
		{
			if (DiscoveredResources.Instance == null)
				return;
			foreach (string resource in Resources.Keys)
			{
				Tag tag = TagManager.Create(resource);
				if (!DiscoveredResources.Instance.IsDiscovered(tag))
					DiscoveredResources.Instance.Discover(tag);
			}
		}

		private void Validate()
		{
			if (Revision == 0 || Revision > long.MaxValue || Resources.Count > MaxResourceCount
			    || Resources.Any(resource => string.IsNullOrEmpty(resource.Key)
			       || resource.Key.Length > MaxTagLength || !IsFinite(resource.Value)
			       || resource.Value < 0f))
				throw new InvalidDataException("Invalid resource snapshot");
		}

		internal static string CanonicalState(IReadOnlyDictionary<string, float> resources)
		{
			var state = new StringBuilder();
			foreach (KeyValuePair<string, float> resource in resources.OrderBy(
				         entry => entry.Key, StringComparer.Ordinal))
			{
				if (state.Length > 0)
					state.Append(',');
				state.Append(resource.Key).Append(':')
					.Append(resource.Value.ToString("R", CultureInfo.InvariantCulture));
			}
			return state.Length == 0 ? "empty" : state.ToString();
		}

#if DEBUG
		internal static TypedEvidenceEnvelope CreateEvidence(
			string phase, long revision, IReadOnlyDictionary<string, float> resources,
			string entryId)
		{
			var values = new List<InventoryResourceState>(resources.Count);
			foreach (KeyValuePair<string, float> resource in resources.OrderBy(
				         entry => entry.Key, StringComparer.Ordinal))
				values.Add(new InventoryResourceState
				{
					Tag = resource.Key,
					Amount = resource.Value,
				});
			return TypedEvidenceRuntimeContext.Create(
				"inventory", phase, revision, new InventoryTarget(),
				new InventoryState { Resources = values }, entryId);
		}
#endif

		private static bool IsFinite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);
	}
}
