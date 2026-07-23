#if DEBUG
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace ONI_Together.DebugTools
{
	public static class TypedEvidenceContract
	{
		private static readonly string[] ScenarioCatalog =
		{
			"remote-dig", "building-lifecycle", "research", "priority", "schedule",
			"building-config", "door", "uproot", "toggle", "inventory", "storage",
			"pickup", "deconstruct", "effect", "chat", "cursor", "animation", "motion",
			"entity-lifecycle", "dlc-runtime", "rocket", "reconnect-world-state",
		};
		private static readonly HashSet<string> Phases = new HashSet<string>(StringComparer.Ordinal)
		{
			"host-submit", "client-apply", "client-original-blocked", "revision-accepted",
			"revision-duplicate", "revision-out-of-order", "final-state", "post-reconnect-state",
		};
		private static readonly Dictionary<string, EvidenceTypes> Types = CreateTypes();
		private static readonly JsonSerializerSettings CanonicalSettings = new JsonSerializerSettings
		{
			ContractResolver = new CamelCasePropertyNamesContractResolver(),
			Formatting = Formatting.None,
			NullValueHandling = NullValueHandling.Include,
		};

		public static IReadOnlyList<string> Scenarios { get; } =
			new ReadOnlyCollection<string>(ScenarioCatalog);

		public static IReadOnlyList<string> Validate(TypedEvidenceEnvelope evidence)
		{
			var errors = new List<string>();
			if (evidence == null)
			{
				errors.Add("evidence is required");
				return errors;
			}
			ValidateEnvelope(evidence, errors);
			if (!Types.TryGetValue(evidence.Scenario ?? string.Empty, out EvidenceTypes types))
				errors.Add("scenario is unknown");
			else
				ValidateTypes(evidence, types, errors);
			return errors;
		}

		public static string ComputeStateHash(ITypedEvidenceState state)
		{
			if (state == null)
				throw new ArgumentNullException(nameof(state));
			return Hash(CanonicalStateJson(state));
		}

		public static string ComputeEnvelopeHash(TypedEvidenceEnvelope evidence)
		{
			if (evidence == null) throw new ArgumentNullException(nameof(evidence));
			return Hash(CanonicalJson(evidence));
		}

		internal static string CanonicalStateJson(ITypedEvidenceState state)
			=> CanonicalJson(state);

		private static string CanonicalJson(object value)
		{
			JsonSerializer serializer = JsonSerializer.Create(CanonicalSettings);
			return SortToken(JToken.FromObject(value, serializer))
				.ToString(Formatting.None);
		}

		private static JToken SortToken(JToken token)
		{
			if (token is JObject value)
			{
				var sorted = new JObject();
				foreach (JProperty property in value.Properties()
					         .OrderBy(item => item.Name, StringComparer.Ordinal))
					sorted.Add(property.Name, SortToken(property.Value));
				return sorted;
			}
			if (token is JArray array)
				return new JArray(array.Select(SortToken));
			return token.DeepClone();
		}

		internal static EvidenceTypes GetTypes(string scenario)
		{
			if (!Types.TryGetValue(scenario ?? string.Empty, out EvidenceTypes types))
				throw new FormatException("Typed evidence scenario is unknown.");
			return types;
		}

		private static void ValidateEnvelope(TypedEvidenceEnvelope value, List<string> errors)
		{
			if (value.SchemaVersion != 1) errors.Add("schemaVersion must be 1");
			Require(value.RunId, "runId", errors);
			RequireHash(value.DllHash, "dllHash", errors);
			if (string.IsNullOrEmpty(value.EntryId) || !value.EntryId.StartsWith("sync:", StringComparison.Ordinal)) errors.Add("entryId must start with sync:");
			if (value.Role != "host" && value.Role != "client") errors.Add("role is invalid");
			if (value.SessionEpoch < 0 || value.ConnectionGeneration < 0 || value.SnapshotGeneration < 0) errors.Add("generation cannot be negative");
			if (!Phases.Contains(value.Phase ?? string.Empty)) errors.Add("phase is invalid");
			Require(value.RevisionDomain, "revisionDomain", errors);
			if (value.Revision < 0 || value.Sequence < 0) errors.Add("revision and sequence cannot be negative");
			ValidateActionAdmission(value, errors);
			if (value.Target == null) errors.Add("target is required");
			if (value.State == null) errors.Add("state is required");
			RequireHash(value.StateHash, "stateHash", errors);
		}

		private static void ValidateActionAdmission(
			TypedEvidenceEnvelope value, List<string> errors)
		{
			bool absent = value.ActionGeneration == 0
			              && value.ActionSequence == 0
			              && string.IsNullOrEmpty(value.ActionCorrelation);
			bool valid = value.ActionGeneration > 0
			             && value.ActionSequence > 0
			             && IsAdmissionToken(value.ActionCorrelation);
			if (!absent && !valid)
				errors.Add("action admission must be absent or complete");
		}

		private static bool IsAdmissionToken(string value)
		{
			if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
			return value.All(character => char.IsLetterOrDigit(character)
				|| character == '-' || character == '_' || character == '.');
		}

		private static void ValidateTypes(
			TypedEvidenceEnvelope value, EvidenceTypes types, List<string> errors)
		{
			if (value.Target != null && value.Target.GetType() != types.TargetType)
				errors.Add("target type does not match scenario");
			if (value.State != null && value.State.GetType() != types.StateType)
				errors.Add("state type does not match scenario");
			if (value.State != null && !string.Equals(
				value.StateHash, ComputeStateHash(value.State), StringComparison.Ordinal))
				errors.Add("stateHash does not match canonical state");
			ValidateRequiredMembers(value.Target, "target", errors);
			ValidateRequiredMembers(value.State, "state", errors);
			ValidateDomain(value, errors);
		}

		private static void ValidateRequiredMembers(object value, string path, List<string> errors)
		{
			if (value == null) return;
			foreach (var property in value.GetType().GetProperties())
			{
				object member = property.GetValue(value, null);
				if (property.PropertyType == typeof(string) && string.IsNullOrEmpty(member as string))
					errors.Add(path + "." + property.Name + " is required");
				else if (!property.PropertyType.IsValueType && member == null)
					errors.Add(path + "." + property.Name + " is required");
			}
		}

		private static void ValidateDomain(TypedEvidenceEnvelope value, List<string> errors)
		{
			if (value.State is MotionState motion)
			{
				if (motion.StartPosition?.Length != 2 || motion.EndPosition?.Length != 2)
					errors.Add("motion positions require two coordinates");
			}
			if (value.State is CursorEvidenceState cursor)
			{
				if (cursor.WorldPosition?.Length != 2 || cursor.ViewPosition?.Length != 2)
					errors.Add("cursor positions require two coordinates");
			}
			if (value.State is InventoryState inventory && inventory.Resources != null)
			{
				string[] tags = inventory.Resources.Select(resource => resource?.Tag).ToArray();
				if (tags.Any(string.IsNullOrEmpty) || !tags.SequenceEqual(tags.OrderBy(tag => tag, StringComparer.Ordinal)) || tags.Distinct(StringComparer.Ordinal).Count() != tags.Length)
					errors.Add("inventory resources must be sorted and unique");
			}
			if (value.Target is DlcRuntimeTarget dlc && !DlcFamilies.Contains(dlc.DlcFamily))
				errors.Add("DLC family is invalid");
		}

		private static readonly HashSet<string> DlcFamilies = new HashSet<string>(StringComparer.Ordinal)
		{
			"Aquatic", "Bionic", "Frosty", "Prehistoric", "SpacedOut", "Common",
		};

		private static void Require(string value, string name, List<string> errors)
		{
			if (string.IsNullOrEmpty(value)) errors.Add(name + " is required");
		}

		private static void RequireHash(string value, string name, List<string> errors)
		{
			if (value == null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal)
			    || value.Substring(7).Any(character => !((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))))
				errors.Add(name + " must be a lowercase sha256 value");
		}

		private static string Hash(string text)
		{
			using (SHA256 sha256 = SHA256.Create())
			{
				byte[] digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
				var result = new StringBuilder("sha256:", 71);
				foreach (byte value in digest) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
				return result.ToString();
			}
		}

		private static Dictionary<string, EvidenceTypes> CreateTypes()
		{
			return new Dictionary<string, EvidenceTypes>(StringComparer.Ordinal)
			{
				["remote-dig"] = EvidenceTypes.Of<RemoteDigTarget, RemoteDigState>(),
				["building-lifecycle"] = EvidenceTypes.Of<BuildingLifecycleTarget, BuildingLifecycleState>(),
				["research"] = EvidenceTypes.Of<ResearchTarget, ResearchState>(),
				["priority"] = EvidenceTypes.Of<PriorityTarget, PriorityState>(),
				["schedule"] = EvidenceTypes.Of<ScheduleTarget, ScheduleState>(),
				["building-config"] = EvidenceTypes.Of<BuildingConfigTarget, BuildingConfigState>(),
				["door"] = EvidenceTypes.Of<DoorTarget, DoorState>(),
				["uproot"] = EvidenceTypes.Of<UprootTarget, UprootState>(),
				["toggle"] = EvidenceTypes.Of<ToggleTarget, ToggleState>(),
				["inventory"] = EvidenceTypes.Of<InventoryTarget, InventoryState>(),
				["storage"] = EvidenceTypes.Of<StorageTarget, StorageState>(),
				["pickup"] = EvidenceTypes.Of<PickupTarget, PickupState>(),
				["deconstruct"] = EvidenceTypes.Of<DeconstructTarget, DeconstructState>(),
				["effect"] = EvidenceTypes.Of<EffectTarget, EffectState>(),
				["chat"] = EvidenceTypes.Of<ChatTarget, ChatState>(),
				["cursor"] = EvidenceTypes.Of<CursorTarget, CursorEvidenceState>(),
				["animation"] = EvidenceTypes.Of<AnimationTarget, AnimationState>(),
				["motion"] = EvidenceTypes.Of<MotionTarget, MotionState>(),
				["entity-lifecycle"] = EvidenceTypes.Of<EntityLifecycleTarget, EntityLifecycleState>(),
				["dlc-runtime"] = EvidenceTypes.Of<DlcRuntimeTarget, DlcRuntimeState>(),
				["rocket"] = EvidenceTypes.Of<RocketTarget, RocketState>(),
				["reconnect-world-state"] = EvidenceTypes.Of<ReconnectWorldStateTarget, ReconnectWorldStateState>(),
			};
		}
	}

	internal sealed class EvidenceTypes
	{
		private EvidenceTypes(Type targetType, Type stateType) { TargetType = targetType; StateType = stateType; }
		internal Type TargetType { get; }
		internal Type StateType { get; }
		internal static EvidenceTypes Of<TTarget, TState>() where TTarget : ITypedEvidenceTarget where TState : ITypedEvidenceState
			=> new EvidenceTypes(typeof(TTarget), typeof(TState));
	}
}
#endif
