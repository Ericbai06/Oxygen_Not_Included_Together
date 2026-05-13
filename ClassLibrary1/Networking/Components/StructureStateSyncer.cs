using System;
using System.Collections.Generic;
using KSerialization;
using ONI_MP.DebugTools;
using ONI_MP.Networking.Packets.World;
using ONI_MP.Patches.GamePatches;
using ONI_MP.Patches.World;
using Shared.Profiling;
using UnityEngine;
using static EnergyGenerator;
using static STRINGS.UI.OVERLAYS;

namespace ONI_MP.Networking.Components
{
	public class StructureStateSyncer : KMonoBehaviour
	{
		public enum StructureType
		{
			UNCATEGORIZED,
			BATTERY,          // Battery
			GENERATOR,        // ManualGenerator, EnergyGenerator etc
            STORAGE_CONTAINER // StorageLocker etc
		}

        public enum GeneratorType
        {
            UNKNOWN,
            MANUAL,
            ENERGY,
            MODULE,
            STATERPILLER
        }

		private float sendInterval = 0.5f; // Sync every 500ms
		private float timer;

		private Battery battery;
		private Generator generator;
        private Storage storage;

		private Operational operational;
		private int cell;

		private float lastSentValue;
		private bool lastSentActive;
        private float[] lastOptionalValues;

		// Grace period
		private bool _initialized = false;
		private float _initializationTime;
		private const float INITIAL_DELAY = 5f;

		public StructureType structureType = StructureType.UNCATEGORIZED;

        // Generator specific
        public GeneratorType generatorType = GeneratorType.UNKNOWN;
        public object generatorInstance;

		public override void OnSpawn()
		{
			using var _ = Profiler.Scope();

			base.OnSpawn();
            cell = Grid.PosToCell(this);
            operational = GetComponent<Operational>();
        }

		public void InitalizeAsStructure(StructureType structureType)
		{
			this.structureType = structureType;
			switch(structureType)
			{
				case StructureType.BATTERY:
					battery = GetComponent<Battery>();
					break;
				case StructureType.GENERATOR:
					generator = GetComponent<Generator>();
                    generatorType = DetermineGeneratorType(this.gameObject, out var gen);
                    generatorInstance = gen;
                    switch(generatorType)
                    {
                        case GeneratorType.ENERGY:
                            EnergyGenerator eg = generatorInstance as EnergyGenerator;
                            storage = eg.storage;
                            break;
                    }
                    break;
                case StructureType.STORAGE_CONTAINER:
                    storage = GetComponent<Storage>();
                    break;
                case StructureType.UNCATEGORIZED:
                default:
                    break;
			}
		}

		private void Update()
		{
			using var _ = Profiler.Scope();

            if (!MultiplayerSession.InSession)
                return;

			if (MultiplayerSession.IsHost)
			{
				// Skip if no clients connected
			  //  if (!MultiplayerSession.SessionHasPlayers)
					//return;

				// Grace period after world load
				if (!_initialized)
				{
					_initializationTime = Time.unscaledTime;
					_initialized = true;
					return;
				}

				if (Time.unscaledTime - _initializationTime < INITIAL_DELAY)
					return;

				HostUpdate();
			}
		}

        private void HostUpdate()
        {
            using var _ = Profiler.Scope();

            try
            {
                timer += Time.unscaledDeltaTime;
                if (timer < sendInterval) return;
                timer = 0f;

                float currentValue = 0f;
                bool currentActive = false;
                float[] optionalValues = [];

                if (operational != null)
                    currentActive = operational.IsActive;

                switch (structureType)
                {
                    case StructureType.BATTERY:
                        if (battery != null)
                        {
                            currentValue = battery.JoulesAvailable;
                        }
                        break;

                    case StructureType.GENERATOR:
                        if (generator != null)
                        {
                            switch(generatorType)
                            {
                                case GeneratorType.ENERGY:
                                    EnergyGenerator gen = generatorInstance as EnergyGenerator;
                                    if (gen != null)
                                    {
                                        // Coal generator
                                        if (gen.hasMeter)
                                        {
                                            //EncodeStorageContents(storage, out var storageContents);

                                            InputItem inputItem = gen.formula.inputs[0];
                                            float mass = storage.GetMassAvailable(inputItem.tag);
                                            float storedMass = inputItem.maxStoredMass;

                                            optionalValues = new float[2/* + storageContents.Length*/];
                                            optionalValues[0] = mass;
                                            optionalValues[1] = storedMass;

                                            //Array.Copy(storageContents, optionalValues, storageContents.Length);
                                        }
                                    }
                                    break;
                                default:
                                    break;
                            }
                            currentValue = generator.JoulesAvailable;
                        }
                        break;

                    case StructureType.STORAGE_CONTAINER:
                        if (storage != null)
                        {
                            currentValue = storage.MassStored();
                            EncodeStorageContents(storage, out var contents);
                            optionalValues = contents;
                        }
                        break;

                    case StructureType.UNCATEGORIZED:
                    default:
                        break;
                }

                // Sync if changed significantly
                if (Mathf.Abs(currentValue - lastSentValue) > 0.1f || currentActive != lastSentActive || OptionalValuesChanged(optionalValues, lastOptionalValues))
                {
                    lastSentValue = currentValue;
                    lastSentActive = currentActive;
                    lastOptionalValues = optionalValues;

                    var packet = new StructureStatePacket
                    {
                        Cell = cell,
                        Value = currentValue,
                        IsActive = currentActive,
                        StructureType = structureType,
                        GeneratorType = generatorType,
                        OptionalValues = optionalValues
                    };

                    PacketSender.SendToAllClients(packet, PacketSendMode.Unreliable);
                }
            }
            catch (System.Exception)
            {
                // Silent fail - Structure may not be ready
            }
        }

        private bool OptionalValuesChanged(float[] a, float[] b, float epsilon = 0.01f)
        {
            if (a == null && b == null) return false;
            if (a == null || b == null) return true;
            if (a.Length != b.Length) return true;

            for (int i = 0; i < a.Length; i++)
            {
                if (Mathf.Abs(a[i] - b[i]) > epsilon)
                    return true;
            }

            return false;
        }

        public static void HandlePacket(StructureStatePacket packet)
		{
			using var _ = Profiler.Scope();
			if (!Grid.IsValidCell(packet.Cell)) return;

			GameObject go = Grid.Objects[packet.Cell, (int)ObjectLayer.Building];
			if (go == null) return;

            switch(packet.StructureType)
            {
                case StructureType.BATTERY:
                    ApplyBatteryState(go, packet);
                    break;
                case StructureType.GENERATOR:
                    ApplyGeneratorState(go, packet);
                    break;
                case StructureType.STORAGE_CONTAINER:
                    ApplyStorageState(go, packet);
                    break;
                case StructureType.UNCATEGORIZED:
                default:
                    break;
            }
            ApplyOperationalState(go, packet);
		}

        #region Battery
        private static void ApplyBatteryState(GameObject go, StructureStatePacket packet)
        {
            var battery = go.GetComponent<Battery>();
            if (battery == null) return;

            battery.joulesAvailable = packet.Value;
            RefreshBatteryTracker(go);
			UpdateBatteryMeter(battery, packet.Value);
        }

        private static void UpdateBatteryMeter(Battery battery, float joules)
		{
			try
			{
                var meter = battery.meter;
                if (meter == null) return;

                if (battery.capacity <= 0f) return;

                float percent = Mathf.Clamp01(joules / battery.capacity);
                meter.SetPositionPercent(percent);
            } catch(Exception ex)
			{
                DebugConsole.LogError($"[StructureStateSyncer] Meter update failed: {ex}");
            }
		}

        private static void RefreshBatteryTracker(GameObject go)
        {
            var tracker = go.GetComponent<BatteryTracker>();
            if (tracker == null) return;

            using var allowClientRefresh = BatteryTrackerPatch.AllowClientRefresh();
            tracker.UpdateData();
        }
        #endregion

        #region Generator
        public static void ApplyGeneratorState(GameObject go, StructureStatePacket packet)
        {
            var generator = go.GetComponent<Generator>();
            if(generator == null) return;

            switch(packet.GeneratorType)
            {
                case GeneratorType.ENERGY:
                    if (packet.OptionalValues.Length > 0)
                    {
                        EnergyGenerator gen = generator as EnergyGenerator;
                        if (gen != null)
                        {
                            float mass = packet.OptionalValues[0];
                            float storedMass = packet.OptionalValues[1];
                            UpdateEnergyGeneratorMeter(gen, mass, storedMass);

                            // Storage data (WIP)
                            if (packet.OptionalValues.Length > 2)
                            {
                                float[] storageData = new float[packet.OptionalValues.Length - 2];
                                Array.Copy(packet.OptionalValues, 2, storageData, 0, storageData.Length);
                                RebuildStorageFromData(gen.storage, storageData);
                            }
                        }
                    }
                    break;
                // Don't need to do anything with these yet
                case GeneratorType.MANUAL:
                case GeneratorType.MODULE:
                case GeneratorType.STATERPILLER:
                default:
                    break;
            }

            generator.AssignJoulesAvailable(packet.Value);
        }

        private static void UpdateEnergyGeneratorMeter(EnergyGenerator generator, float mass, float storedMass)
        {
            if (generator.hasMeter)
            {
                float meterPercent = Mathf.Clamp01(mass / storedMass);
                generator.meter.SetPositionPercent(meterPercent);
            }
        }

        private static GeneratorType DetermineGeneratorType(GameObject go, out object generator)
        {
            ManualGenerator manualGenerator = go.GetComponent<ManualGenerator>();
            if (manualGenerator != null)
            {
                generator = manualGenerator;
                return GeneratorType.MANUAL;
            }

            EnergyGenerator energyGenerator = go.GetComponent<EnergyGenerator>();
            if (energyGenerator != null)
            {
                generator = energyGenerator;
                return GeneratorType.ENERGY;
            }

            ModuleGenerator moduleGenerator = go.GetComponent<ModuleGenerator>();
            if (moduleGenerator != null)
            {
                generator = moduleGenerator;
                return GeneratorType.MODULE;
            }

                StaterpillarGenerator statepillerGenerator = go.GetComponent<StaterpillarGenerator>();
            if (moduleGenerator != null)
            {
                generator = statepillerGenerator;
                return GeneratorType.STATERPILLER;
            }

            generator = null;
            return GeneratorType.UNKNOWN;
        }
        #endregion

        #region Storage
        private static void ApplyStorageState(GameObject go, StructureStatePacket packet)
        {
            var storage = go.GetComponent<Storage>();
            if (storage == null || packet.OptionalValues.Length < 2) return;

            RebuildStorageFromData(storage, packet.OptionalValues);
        }

        private static void EncodeStorageContents(Storage storage, out float[] optionalValues)
        {
            var entries = new Dictionary<SimHashes, float>();
            for (int i = 0; i < storage.items.Count; i++)
            {
                if (storage.items[i] == null) continue;
                var pe = storage.items[i].GetComponent<PrimaryElement>();
                if (pe == null || pe.Mass <= 0f) continue;
                entries.TryGetValue(pe.ElementID, out var existing);
                entries[pe.ElementID] = existing + pe.Mass;
            }

            optionalValues = new float[2 + entries.Count * 2];
            optionalValues[0] = storage.capacityKg;
            optionalValues[1] = entries.Count;
            int idx = 2;
            foreach (var kv in entries)
            {
                optionalValues[idx++] = BitConverter.ToSingle(BitConverter.GetBytes((int)kv.Key), 0);
                optionalValues[idx++] = kv.Value;
            }
        }

        private static void RebuildStorageFromData(Storage storage, float[] data)
        {
            if (storage == null || data.Length < 2) return;

            storage.ConsumeAllIgnoringDisease(); // Empty the storage

            int count = (int)data[1];
            if (count == 0)
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                byte[] hashBytes = BitConverter.GetBytes(data[2 + i * 2]);
                int hash = BitConverter.ToInt32(hashBytes, 0);
                var element = (SimHashes)hash;
                float mass = data[2 + i * 2 + 1];

                if (mass > 0f && ElementLoader.FindElementByHash(element) != null)
                    storage.AddElement(element, mass, 293f, 0, 0);
            }
        }

        #endregion
        private static void ApplyOperationalState(GameObject go, StructureStatePacket packet)
		{
            var operational = go.GetComponent<Operational>();
            if (operational == null) return;

            operational.SetActive(packet.IsActive);
        }
	}
}
