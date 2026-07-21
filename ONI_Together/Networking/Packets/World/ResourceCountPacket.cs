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
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			DispatchContext context = PacketHandler.CurrentContext;
			if (MultiplayerSession.IsHost || !context.SenderIsHost
			    || !PacketHandler.IsCurrentDispatchContext(context))
				return;

			if (_clientEpoch != context.SessionEpoch)
			{
				_clientEpoch = context.SessionEpoch;
				_clientRevision = 0;
			}
			string state = CanonicalState(Resources);
			if (!NetworkIdentityRegistry.IsNewerRevision(_clientRevision, Revision))
			{
#if DEBUG
				IntegrationScenarioEvidenceCore.Log(
					"inventory", Revision == _clientRevision ? "revision-duplicate" : "revision-out-of-order",
					(long)Revision, false, state);
#endif
				return;
			}

			_clientRevision = Revision;
			ResourceSyncer.ClientResources = new Dictionary<string, float>(Resources);
			ApplyDiscoveries();
#if DEBUG
			IntegrationScenarioEvidenceCore.Log("inventory", "revision-accepted", (long)Revision, true, state);
			IntegrationScenarioEvidenceCore.Log("inventory", "client-apply", (long)Revision, true, state);
			IntegrationScenarioEvidenceCore.Log("inventory", "final-state", (long)Revision, true, state);
#endif
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

		private static bool IsFinite(float value)
			=> !float.IsNaN(value) && !float.IsInfinity(value);
	}
}
