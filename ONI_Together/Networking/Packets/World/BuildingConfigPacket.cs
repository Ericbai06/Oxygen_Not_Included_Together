using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.World.Handlers;
using ONI_Together.DebugTools;
using System.IO;
using UnityEngine;
using Shared.Profiling;
using Shared.Interfaces.Networking;

namespace ONI_Together.Networking.Packets.World
{
	internal struct BuildingConfigMetadata
	{
		internal int NetId;
		internal int Cell;
		internal int DeterministicId;
		internal BuildingConfigType ConfigType;
		internal int SliderIndex;
		internal int ReferenceNetId;
		internal float Value;
		internal string StringValue;
	}

	public enum BuildingConfigType : byte
	{
		Float = 0,      // Standard float value (valve flow, thresholds)
		Boolean = 1,    // Checkbox values
		SliderIndex = 2, // Slider with index (for multi-slider controls)
		RecipeQueue = 3, // Fabricator recipe queue (ConfigHash = recipe ID hash, Value = count)
		String = 4       // String value (tag names, text fields)
	}

	public partial class BuildingConfigPacket : IPacket, IViewportCullable
	{
		private const int MaxCell = 16 * 1024 * 1024;
		private const int MaxSliderIndex = 1024;
		private const int MaxStringLength = 1024;

		private ulong Sender;
		public int NetId;
		public ulong TargetLifecycleRevision;
		public ulong StateRevision;
		public ulong ClientRequestId;
		public ulong BaseStateRevision;
		public int Cell; // Deterministic location-based identification
		public int DeterministicBuildingId;
		public int ConfigHash; // Hash of the property name (e.g. "Threshold", "Logic")
		public float Value;
		public BuildingConfigType ConfigType = BuildingConfigType.Float;
		public int SliderIndex = 0; // For ISliderControl multi-sliders
		public int ReferenceNetId; // Optional signed host-assigned entity reference; zero means none
		public string StringValue = ""; // For tag names and text fields
		public string SecondaryStringValue = ""; // Paired string payloads that must apply atomically

		private static int _applyDepth;
#if DEBUG
		private static BuildingConfigPacket _applyingEvidencePacket;
		private static bool _originalBlockObserved;
#endif
		public static bool IsApplyingPacket
		{
			get
			{
#if DEBUG
				if (_applyDepth > 0 && MultiplayerSession.IsClient
				    && !_originalBlockObserved && _applyingEvidencePacket != null)
				{
					_originalBlockObserved = true;
					_applyingEvidencePacket.LogOriginalBlockedEvidence();
				}
#endif
				return _applyDepth > 0;
			}
		}
        
		// Delay refreshing because things like storage lockers cause lag
		private static float _lastRefreshTime = -999f;
        private const float REFRESH_COOLDOWN = 0.1f; // ~30 frames at 60fps, consistent regardless of FPS

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			PrepareForSerialize();
			if (!IsValidMetadata(GetMetadata())
			    || (SecondaryStringValue?.Length ?? 0) > MaxStringLength
			    || !HasValidAuthorityFields())
				throw new InvalidDataException("Invalid building config metadata");

			writer.Write(Sender);
			writer.Write(NetId);
			writer.Write(TargetLifecycleRevision);
			writer.Write(StateRevision);
			writer.Write(ClientRequestId);
			writer.Write(BaseStateRevision);
			writer.Write(Cell);
			writer.Write(DeterministicBuildingId);
			writer.Write(ConfigHash);
			writer.Write(Value);
			writer.Write((byte)ConfigType);
			writer.Write(SliderIndex);
			writer.Write(ReferenceNetId);
			writer.Write(StringValue ?? "");
			writer.Write(SecondaryStringValue ?? "");
		}

		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			Sender = reader.ReadUInt64();
			NetId = reader.ReadInt32();
			TargetLifecycleRevision = reader.ReadUInt64();
			StateRevision = reader.ReadUInt64();
			ClientRequestId = reader.ReadUInt64();
			BaseStateRevision = reader.ReadUInt64();
			Cell = reader.ReadInt32();
			DeterministicBuildingId = reader.ReadInt32();
			ConfigHash = reader.ReadInt32();
			Value = reader.ReadSingle();
			ConfigType = (BuildingConfigType)reader.ReadByte();
			SliderIndex = reader.ReadInt32();
			ReferenceNetId = reader.ReadInt32();
			StringValue = reader.ReadString();
			SecondaryStringValue = reader.ReadString();
			if (!IsValidMetadata(GetMetadata())
			    || SecondaryStringValue.Length > MaxStringLength
			    || !HasValidAuthorityFields())
				throw new InvalidDataException("Invalid building config metadata");
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();
			DispatchWithAuthority(PacketHandler.CurrentContext);
		}

		private void BindAuthoritativeIdentity()
		{
			if (NetId == 0 || !NetworkIdentityRegistry.TryGet(NetId, out NetworkIdentity identity)
			    || identity == null)
				return;

			Cell = Grid.PosToCell(identity.gameObject);
			DeterministicBuildingId = NetIdHelper.GetDeterministicBuildingId(identity.gameObject);
		}

		private BuildingConfigMetadata GetMetadata() => new()
		{
			NetId = NetId,
			Cell = Cell,
			DeterministicId = DeterministicBuildingId,
			ConfigType = ConfigType,
			SliderIndex = SliderIndex,
			ReferenceNetId = ReferenceNetId,
			Value = Value,
			StringValue = StringValue
		};

		internal static bool IsValidMetadata(BuildingConfigMetadata metadata)
			=> metadata.NetId != 0 && metadata.DeterministicId != 0
			   && metadata.Cell >= 0 && metadata.Cell < MaxCell
			   && metadata.ConfigType >= BuildingConfigType.Float
			   && metadata.ConfigType <= BuildingConfigType.String
			   && metadata.SliderIndex >= 0 && metadata.SliderIndex <= MaxSliderIndex
			   && !float.IsNaN(metadata.Value) && !float.IsInfinity(metadata.Value)
			   && (metadata.StringValue?.Length ?? 0) <= MaxStringLength;

		internal static bool IsBooleanValue(float value) => value == 0f || value == 1f;

		internal static bool IsIntegralValue(float value)
			=> value >= int.MinValue && value <= int.MaxValue && value == (int)value;

		internal static bool IsInRange(float value, float minimum, float maximum)
			=> value >= minimum && value <= maximum;

		internal static void BeginApplyingPacket()
		{
			_applyDepth++;
		}

		internal static void EndApplyingPacket()
		{
			if (_applyDepth > 0)
				_applyDepth--;
		}

		internal static void ResetSessionState()
		{
			_applyDepth = 0;
#if DEBUG
			_applyingEvidencePacket = null;
			_originalBlockObserved = false;
#endif
			_lastRefreshTime = -999f;
			ResetAuthorityState();
		}

		public int GetViewportCell()
		{
			if (MultiplayerSession.IsHost && MultiplayerSession.InSession)
				PrepareForSerialize();
			else
				BindAuthoritativeIdentity();
			return Cell;
		}

		internal static void ResetApplyingPacketForTests() => ResetSessionState();

        /// <summary>
        /// Applies the configuration to the target building.
        /// All handlers are now in the BuildingConfigHandlerRegistry.
        /// </summary>
        private bool ApplyConfig(GameObject go)
		{
			using var _ = Profiler.Scope();

			if (go == null) return false;

            // All handlers are now in the registry
            if (BuildingConfigHandlerRegistry.TryHandle(go, this))
			{
				DebugConsole.Log($"[BuildingConfigPacket] Handled by registry for {go.name}");
				return true;
			}

			// Log unhandled configs for debugging
			DebugConsole.LogWarning($"[BuildingConfigPacket] Unhandled config: Hash={ConfigHash}, Type={ConfigType}, Value={Value}, String={StringValue} on {go.name}");
			return false;
		}

        private void RefreshSideScreenIfOpen(GameObject go)
        {
            using var _ = Profiler.Scope();
            if (go == null) return;

            if (Time.unscaledTime - _lastRefreshTime < REFRESH_COOLDOWN) return;
            _lastRefreshTime = Time.unscaledTime;

            try
            {
                if (go.TryGetComponent<KSelectable>(out var selectable) && SelectTool.Instance.selected == selectable)
                {
                    SelectTool.Instance.Select(null, true);
                    SelectTool.Instance.Select(selectable, true);
                }
            }
            catch (System.Exception e)
            {
                DebugConsole.Log($"[BuildingConfigPacket] UI refresh failed: {e.Message}");
            }
        }
    }
}
