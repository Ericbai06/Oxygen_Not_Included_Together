using System;
using System.Linq;
using ONI_Together.Networking.Components;
using ONI_Together.Networking.Packets.DLC.SpacedOut;
using ONI_Together.Networking.Packets.World;
using Shared.Interfaces.Networking;
using UnityEngine;

#if DEBUG
namespace ONI_Together.Networking
{
	public static partial class NetworkIdentityRegistry
	{
		internal static bool TryPrepareProfileFixture(
			string prefab,
			string fixtureIdentity,
			out NetworkIdentity identity,
			out DlcRuntimeProfileFixture fixture,
			out bool fixtureCreated)
		{
			fixture = null;
			fixtureCreated = false;
			NetworkIdentity[] candidates = identities.Values.Where(value =>
			{
				if (value == null || value.IsNullOrDestroyed()
				    || value.gameObject == null || value.gameObject.IsNullOrDestroyed())
					return false;
				string prefabId = value.gameObject.PrefabID().Name;
				return string.Equals(prefabId, prefab, StringComparison.Ordinal);
			}).OrderBy(value => value.NetId).ToArray();
			identity = candidates.FirstOrDefault(value =>
				string.Equals(value.GetComponent<DlcRuntimeProfileFixture>()?.FixtureIdentity,
					fixtureIdentity, StringComparison.Ordinal));
			if (identity != null)
			{
				fixture = identity.GetComponent<DlcRuntimeProfileFixture>();
				return true;
			}
			if (!CanPrepareScoutRoverFixture(prefab, fixtureIdentity, candidates))
				return false;
			identity = candidates[0];
			fixture = identity.gameObject.AddComponent<DlcRuntimeProfileFixture>();
			fixture.DlcFamily = "SpacedOut";
			fixture.FixtureIdentity = fixtureIdentity;
			fixtureCreated = true;
			return true;
		}

		internal static void RemovePreparedProfileFixture(
			DlcRuntimeProfileFixture fixture)
		{
			if (fixture == null || fixture.IsNullOrDestroyed())
				return;
			fixture.DlcFamily = null;
			fixture.FixtureIdentity = null;
			fixture.enabled = false;
			UnityEngine.Object.Destroy(fixture);
		}

		private static bool CanPrepareScoutRoverFixture(
			string prefab, string fixtureIdentity, NetworkIdentity[] candidates)
			=> candidates.Length == 1
			   && string.Equals(prefab, ScoutRoverConfig.ID, StringComparison.Ordinal)
			   && string.Equals(fixtureIdentity, "rover-7", StringComparison.Ordinal);
	}

	internal static class EntityLifecycleProfileRuntime
	{
		internal static bool SetActiveAndBroadcast(
			NetworkIdentity identity,
			bool active)
		{
			if (identity == null || identity.IsNullOrDestroyed()
			    || identity.NetId == 0 || identity.gameObject.IsNullOrDestroyed())
				return false;
			identity.gameObject.SetActive(active);
			identity.LifecycleRevision = NetworkIdentityRegistry.BeginLifecycle(identity.NetId);
			SpawnPrefabPacket packet = SpawnPrefabPacket.FromIdentity(
				identity, requireExistingPersistentObject: true);
			if (packet == null)
				return false;
			PacketSender.SendToAllClients(packet, PacketSendMode.ReliableImmediate);
			return identity.gameObject.activeSelf == active;
		}
	}

	internal sealed class DlcProfileMutation
	{
		internal StateMachine.Instance Instance;
		internal StateMachine.BaseState Previous;
		internal NetworkIdentity Identity;
		internal bool PreviousWorking;
		internal DlcRuntimeProfileFixture Fixture;
		internal bool RemoveFixture;
	}

	internal sealed class DlcRuntimeProfileFixture : KMonoBehaviour
	{
		public string DlcFamily;
		public string FixtureIdentity;
	}

	internal static class DlcRuntimeProfileRuntime
	{
		internal static DlcProfileMutation TransitionToNext(
			GameObject target,
			string dlcFamily,
			DlcRuntimeProfileFixture fixture,
			bool removeFixture)
		{
			StateMachineController controller = target?.GetComponent<StateMachineController>();
			if (controller == null || fixture == null
			    || !string.Equals(fixture.DlcFamily, dlcFamily, StringComparison.Ordinal))
				return Reject(fixture, removeFixture);
			foreach (StateMachine.Instance instance in controller)
			{
				StateMachine.BaseState previous = instance?.GetCurrentState();
				StateMachine machine = instance?.GetStateMachine();
				if (previous == null || machine is not RobotIdleMonitor roverIdle)
					continue;
				StateMachine.BaseState candidate = ReferenceEquals(previous, roverIdle.idle)
					? roverIdle.working
					: ReferenceEquals(previous, roverIdle.working) ? roverIdle.idle : null;
				if (candidate == null)
					return Reject(fixture, removeFixture);
				bool working = ReferenceEquals(candidate, roverIdle.working);
				instance.GoTo(candidate);
				if (!ReferenceEquals(instance.GetCurrentState(), candidate))
				{
					instance.GoTo(previous);
					return Reject(fixture, removeFixture);
				}
				NetworkIdentity identity = target.GetComponent<NetworkIdentity>();
				if (identity == null || identity.NetId == 0)
				{
					instance.GoTo(previous);
					return Reject(fixture, removeFixture);
				}
				return new DlcProfileMutation
				{
					Instance = instance,
					Previous = previous,
					Identity = identity,
					PreviousWorking = ReferenceEquals(previous, roverIdle.working),
					Fixture = fixture,
					RemoveFixture = removeFixture,
				};
			}
			return Reject(fixture, removeFixture);
		}

		private static DlcProfileMutation Reject(
			DlcRuntimeProfileFixture fixture, bool removeFixture)
		{
			if (removeFixture)
				NetworkIdentityRegistry.RemovePreparedProfileFixture(fixture);
			return null;
		}

		internal static bool Restore(DlcProfileMutation mutation)
		{
			if (mutation?.Instance == null || mutation.Previous == null
			    || mutation.Identity == null || mutation.Identity.IsNullOrDestroyed())
				return false;
			mutation.Instance.GoTo(mutation.Previous);
			bool restored = ReferenceEquals(
				mutation.Instance.GetCurrentState(), mutation.Previous);
			if (restored)
			{
				if (mutation.RemoveFixture)
					NetworkIdentityRegistry.RemovePreparedProfileFixture(mutation.Fixture);
			}
			return restored;
		}

		internal static bool ApplyExplicitState(GameObject target, bool working)
		{
			StateMachineController controller = target?.GetComponent<StateMachineController>();
			if (controller == null)
				return false;
			foreach (StateMachine.Instance instance in controller)
			{
				if (instance?.GetStateMachine() is not RobotIdleMonitor roverIdle)
					continue;
				StateMachine.BaseState targetState = working ? roverIdle.working : roverIdle.idle;
				instance.GoTo(targetState);
				return ReferenceEquals(instance.GetCurrentState(), targetState);
			}
			return false;
		}
	}
}
#endif
