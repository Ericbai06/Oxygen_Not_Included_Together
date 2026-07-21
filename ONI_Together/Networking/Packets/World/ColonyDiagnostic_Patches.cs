using HarmonyLib;
using ONI_Together.Networking.Packets.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shared.Profiling;
using static ColonyDiagnostic;

namespace ONI_Together.Networking.Packets.World
{
	internal class ColonyDiagnostic_Patches
	{
		private static readonly Dictionary<string, DiagnosticResult> CachedResults = [];
		private static readonly Dictionary<string, long> ClientRevisions = [];
		private static readonly Dictionary<string, long> HostRevisions = [];

		internal static void ResetSessionState()
		{
			CachedResults.Clear();
			ClientRevisions.Clear();
			HostRevisions.Clear();
		}

		internal static void OnPacketReceived(DiagnosticPacket diagnosticPacket)
		{
			using var _ = Profiler.Scope();

			if (diagnosticPacket == null || string.IsNullOrEmpty(diagnosticPacket.DiagnosticType))
				return;
			ClientRevisions.TryGetValue(diagnosticPacket.DiagnosticType, out long current);
			if (!DiagnosticPacket.ShouldApplyRevision(current, diagnosticPacket.Revision))
				return;
			ClientRevisions[diagnosticPacket.DiagnosticType] = diagnosticPacket.Revision;
			CachedResults[diagnosticPacket.DiagnosticType] = diagnosticPacket.ToResult();
		}

		private static long NextHostRevision(string typeName)
		{
			HostRevisions.TryGetValue(typeName, out long current);
			long next = checked(current + 1);
			HostRevisions[typeName] = next;
			return next;
		}

		[HarmonyPatch(typeof(ColonyDiagnostic), nameof(ColonyDiagnostic.Evaluate))]
		public class ColonyDiagnostic_Evaluate_Patch
		{
			public static bool Prefix(ColonyDiagnostic __instance, ref DiagnosticResult __result)
			{
				using var _ = Profiler.Scope();

				if (!MultiplayerSession.IsClient)
					return true;
				var typeName = __instance.GetType().Name;
				if (!CachedResults.TryGetValue(typeName, out var cached))
					__result = new DiagnosticResult(DiagnosticResult.Opinion.Normal, "");
				else
					__result = cached;
				return false;
			}
			public static void Postfix(ColonyDiagnostic __instance, DiagnosticResult __result)
			{
				using var _ = Profiler.Scope();

				if (MultiplayerSession.IsHostInSession)
				{
					var typeName = __instance.GetType().Name;
					PacketSender.SendToAllClients(
						new DiagnosticPacket(typeName, __result, NextHostRevision(typeName)),
						DiagnosticPacket.SendMode);
				}
			}
		}

	}
}
