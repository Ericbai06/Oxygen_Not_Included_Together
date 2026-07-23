#if DEBUG
using System.Collections.Generic;
using ONI_Together.DebugTools;
using UnityEngine;

namespace ONI_Together.Networking
{
	internal static class ProductionFaultInputGates
	{
		internal static IFaultInputMutation MissingPersonality(ref Personality personality)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"duplicant.personality-missing");
			if (mutation.Applied) personality = null;
			return mutation;
		}

		internal static IFaultInputMutation MinionBeforeController(ref bool minionFirst)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"duplicant.set-minion-before-controller");
			if (mutation.Applied) minionFirst = true;
			return mutation;
		}

		internal static IFaultInputMutation PreviewFlatulence(ref bool preview)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"duplicant.preview-flatulence");
			if (mutation.Applied) preview = true;
			return mutation;
		}

		internal static IFaultInputMutation DestroyedMinionObject(ref GameObject gameObject)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"duplicant.destroyed-add-component");
			if (mutation.Applied && gameObject != null)
				Object.Destroy(gameObject);
			return mutation;
		}

		internal static IFaultInputMutation UnregisteredWorkable(ref bool registered)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"work.workable-unregistered");
			if (mutation.Applied) registered = false;
			return mutation;
		}

		internal static IFaultInputMutation MissingWorkTarget(ref GameObject target)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"work.target-missing");
			if (mutation.Applied) target = null;
			return mutation;
		}

		internal static IFaultInputMutation MissingOriginalDigElement(ref Element element)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"work.original-dig-element-null");
			if (mutation.Applied) element = null;
			return mutation;
		}

		internal static IFaultInputMutation ClientNativeStart(ref bool nativeStart)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"work.client-native-start");
			if (mutation.Applied) nativeStart = true;
			return mutation;
		}

		internal static IFaultInputMutation MissingSelectedElements(ref IList<Tag> tags)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"building.selected-elements-null");
			if (mutation.Applied) tags = null;
			return mutation;
		}

		internal static IFaultInputMutation DeferredReplacementDestroy(ref bool deferred)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"building.destroy-deferred");
			if (mutation.Applied) deferred = true;
			return mutation;
		}

		internal static IFaultInputMutation MissingDlcPrefab(ref GameObject prefab)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"dlc.prefab-missing");
			if (mutation.Applied) prefab = null;
			return mutation;
		}

		internal static IFaultInputMutation StateBeforeStartSm(ref bool beforeStart)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume(
				"dlc.state-before-start-sm");
			if (mutation.Applied) beforeStart = true;
			return mutation;
		}

		internal static IFaultInputMutation AquaticFamily(ref string family)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume("dlc.family-aquatic");
			if (mutation.Applied) family = "Aquatic";
			return mutation;
		}

		internal static IFaultInputMutation BionicFamily(ref string family)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume("dlc.family-bionic");
			if (mutation.Applied) family = "Bionic";
			return mutation;
		}

		internal static IFaultInputMutation FrostyFamily(ref string family)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume("dlc.family-frosty");
			if (mutation.Applied) family = "Frosty";
			return mutation;
		}

		internal static IFaultInputMutation PrehistoricFamily(ref string family)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume("dlc.family-prehistoric");
			if (mutation.Applied) family = "Prehistoric";
			return mutation;
		}

		internal static IFaultInputMutation SpacedOutFamily(ref string family)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume("dlc.family-spaced-out");
			if (mutation.Applied) family = "SpacedOut";
			return mutation;
		}

		internal static IFaultInputMutation CommonFamily(ref string family)
		{
			FaultInputMutation mutation = FaultInjectionUnitySeams.Consume("dlc.family-common");
			if (mutation.Applied) family = "Common";
			return mutation;
		}
	}
}
#endif
