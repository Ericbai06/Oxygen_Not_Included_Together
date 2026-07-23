using ONI_Together.Networking.Components;
using ONI_Together.Patches.DLC.SpacedOut;
using Shared.Interfaces.Networking;

#if DEBUG
namespace ONI_Together.Networking.Packets.DLC.SpacedOut
{
	internal static class RocketProfileRuntime
	{
		internal static RocketSettingsPacketData ApplyNextBoarding(
			int rocketNetId,
			int padNetId)
		{
			if (!ResolveTargets(rocketNetId, padNetId,
				    out RocketSettingsPacketData previous, out LaunchPad pad))
				return null;
			AxialI destination = pad.GetMyWorldLocation();
			RocketSettingsPacketData next = Clone(previous);
			next.HasDestination = true;
			next.DestinationQ = destination.q;
			next.DestinationR = destination.r;
			next.HasPad = true;
			next.PadNetId = padNetId;
			if (RocketSettingsSync.SnapshotsMatch(previous, next))
				return null;
			if (!RocketSettingsSync.TryApplyRequestedSettings(next))
			{
				Restore(previous);
				return null;
			}
			if (!RocketSettingsSync.TryCaptureByTarget(
				    next, out RocketSettingsPacketData applied)
			    || RocketSettingsSync.SnapshotsMatch(previous, applied)
			    || !RocketSettingsSync.SnapshotsMatch(next, applied))
			{
				Restore(previous);
				return null;
			}
			return previous;
		}

		internal static bool ResolveTargets(
			int rocketNetId,
			int padNetId,
			out RocketSettingsPacketData previous,
			out LaunchPad pad)
		{
			var target = new RocketSettingsPacketData
			{
				TargetKind = RocketSettingsTarget.DestinationSelector,
				TargetNetId = rocketNetId,
			};
			pad = null;
			return RocketSettingsSync.TryCaptureByTarget(target, out previous)
			       && NetworkIdentityRegistry.TryGetComponent(padNetId, out pad)
			       && pad != null;
		}

		internal static bool Restore(RocketSettingsPacketData previous)
		{
			if (previous == null || !RocketSettingsSync.TryApplyRequestedSettings(previous)
			    || !RocketSettingsSync.TryCaptureByTarget(
				    previous, out RocketSettingsPacketData applied)
			    || !RocketSettingsSync.SnapshotsMatch(previous, applied))
				return false;
			return true;
		}

		private static void Publish(RocketSettingsPacketData state)
			=> PacketSender.SendToAllClients(
				RocketSettingsStatePacket.CreateAuthoritative(state),
				PacketSendMode.ReliableImmediate);

		private static RocketSettingsPacketData Clone(RocketSettingsPacketData source)
			=> new RocketSettingsPacketData
			{
				TargetKind = source.TargetKind,
				TargetNetId = source.TargetNetId,
				TargetLifecycleRevision = source.TargetLifecycleRevision,
				HasDestination = source.HasDestination,
				DestinationQ = source.DestinationQ,
				DestinationR = source.DestinationR,
				HasPad = source.HasPad,
				PadNetId = source.PadNetId,
				Repeat = source.Repeat,
				RestrictWhenGrounded = source.RestrictWhenGrounded,
				HasCraftState = source.HasCraftState,
				CraftLocationQ = source.CraftLocationQ,
				CraftLocationR = source.CraftLocationR,
				CraftPhase = source.CraftPhase,
				HasCurrentPad = source.HasCurrentPad,
				CurrentPadNetId = source.CurrentPadNetId,
			};
	}
}
#endif
