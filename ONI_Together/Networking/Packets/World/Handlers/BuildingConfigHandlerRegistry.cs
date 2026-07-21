using System.Collections.Generic;
using UnityEngine;
using ONI_Together.DebugTools;
using Shared.Profiling;
using Shared;

namespace ONI_Together.Networking.Packets.World.Handlers
{
	public enum BuildingConfigMutationSemantics : byte
	{
		StateAssignment = 0,
		MustExecuteAction = 1,
	}

	/// <summary>
	/// Registry for building configuration handlers.
	/// Maps ConfigHash values to their respective handlers for fast lookup.
	/// </summary>
	public static class BuildingConfigHandlerRegistry
	{
		private static readonly Dictionary<int, IBuildingConfigHandler> _handlersByHash = new Dictionary<int, IBuildingConfigHandler>();
		private static readonly Dictionary<int, BuildingConfigMutationSemantics> _semanticsByHash = new();
		private static readonly List<IBuildingConfigHandler> _allHandlers = new List<IBuildingConfigHandler>();
		private static bool _initialized = false;

		/// <summary>
		/// Initializes the registry by discovering and registering all handlers.
		/// Called automatically on first use.
		/// </summary>
		public static void Initialize()
		{
			using var _ = Profiler.Scope();

			if (_initialized) return;
			_initialized = true;

			// Register all handlers here
			// Each handler will be registered for each of its supported ConfigHashes
			RegisterStateHandler(new ActivationRangeHandler());
			RegisterStateHandler(new ThresholdSwitchHandler());
			RegisterStateHandler(new SliderControlHandler());
			RegisterStateHandler(new CapacityHandler());
			RegisterStateHandler(new DoorHandler());
			RegisterStateHandler(new TimerSensorHandler());
			RegisterStateHandler(new AlarmHandler());
			RegisterStateHandler(new GeoTunerHandler());
			RegisterStateHandler(new MissileLauncherHandler());
			RegisterStateHandler(new FilterableHandler());
			RegisterStateHandler(new StorageFilterHandler());
			RegisterStateHandler(new ReceptacleHandler());
			RegisterStateHandler(new MiscBuildingHandler());
			RegisterStateHandler(new AccessControlHandler());
			RegisterStateHandler(new CraftingHandler());
			RegisterStateHandler(new CometDetectorHandler());
			RegisterStateHandler(new ToggleableHandler());
			RegisterActionHandler(new UprootHandler());
			RegisterActionHash(NetworkingHash.ForConfigKey("DoorUnseal"));
			RegisterActionHash(NetworkingHash.ForConfigKey("CounterReset"));

			DebugConsole.Log($"[BuildingConfigHandlerRegistry] Initialized with {_allHandlers.Count} handlers, {_handlersByHash.Count} hash mappings");
		}

		/// <summary>
		/// Registers a handler for all of its supported ConfigHashes.
		/// </summary>
		public static void RegisterHandler(IBuildingConfigHandler handler)
			=> RegisterHandler(handler, BuildingConfigMutationSemantics.StateAssignment);

		private static void RegisterStateHandler(IBuildingConfigHandler handler)
			=> RegisterHandler(handler, BuildingConfigMutationSemantics.StateAssignment);

		private static void RegisterActionHandler(IBuildingConfigHandler handler)
			=> RegisterHandler(handler, BuildingConfigMutationSemantics.MustExecuteAction);

		private static void RegisterHandler(
			IBuildingConfigHandler handler,
			BuildingConfigMutationSemantics semantics)
		{
			using var _ = Profiler.Scope();

			_allHandlers.Add(handler);

			foreach (var hash in handler.SupportedConfigHashes)
			{
				if (_handlersByHash.ContainsKey(hash))
				{
					DebugConsole.Log($"[BuildingConfigHandlerRegistry] Warning: ConfigHash {hash} already registered, overwriting");
				}
				_handlersByHash[hash] = handler;
				_semanticsByHash[hash] = semantics;
			}
		}

		private static void RegisterActionHash(int configHash)
			=> _semanticsByHash[configHash] = BuildingConfigMutationSemantics.MustExecuteAction;

		public static BuildingConfigMutationSemantics GetMutationSemantics(int configHash)
		{
			if (!_initialized) Initialize();
			return _semanticsByHash.TryGetValue(configHash, out var semantics)
				? semantics
				: BuildingConfigMutationSemantics.StateAssignment;
		}

		/// <summary>
		/// Attempts to handle a building configuration packet.
		/// </summary>
		/// <param name="go">Target GameObject</param>
		/// <param name="packet">The configuration packet</param>
		/// <returns>True if the configuration was handled by a registered handler</returns>
		public static bool TryHandle(GameObject go, BuildingConfigPacket packet)
		{
			using var _ = Profiler.Scope();

			if (!_initialized) Initialize();

			// Fast path: lookup by hash
			if (_handlersByHash.TryGetValue(packet.ConfigHash, out var handler))
			{
				if (handler.TryApplyConfig(go, packet))
				{
					return true;
				}
			}

			// Fallback: iterate all handlers (for handlers that check component existence)
			foreach (var h in _allHandlers)
			{
				if (h.TryApplyConfig(go, packet))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Clears all registered handlers. Primarily for testing.
		/// </summary>
		public static void Clear()
		{
			using var _ = Profiler.Scope();

			_handlersByHash.Clear();
			_semanticsByHash.Clear();
			_allHandlers.Clear();
			_initialized = false;
		}
	}
}
