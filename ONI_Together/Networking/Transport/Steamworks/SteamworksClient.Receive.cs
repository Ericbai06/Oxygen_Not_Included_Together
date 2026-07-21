using System;
using System.Runtime.InteropServices;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using Steamworks;
using UnityEngine;

namespace ONI_Together.Networking.Transport.Steam
{
	public partial class SteamworksClient
	{
		private const int MinimumMessagesPerUpdate = 128;

		public override void OnMessageRecieved()
		{
			using var _ = Profiler.Scope();
			if (Connection.HasValue)
				ProcessIncomingMessages(Connection.Value);
		}

		private static void ProcessIncomingMessages(HSteamNetConnection connection)
		{
			using var _ = Profiler.Scope();
			var scope = Profiler.Scope();
			int configured = Mathf.Clamp(
				Configuration.GetClientProperty<int>("MaxMessagesPerPoll"), 1, 1024);
			int budget = ReceiveBudgetForTests(configured);
			var messages = new IntPtr[Math.Min(configured, budget)];
			int processed = 0;
			int totalBytes = 0;
			int received;
			do
			{
				int requested = Math.Min(messages.Length, budget - processed);
				received = SteamNetworkingSockets.ReceiveMessagesOnConnection(
					connection, messages, requested);
				for (int index = 0; index < received; index++)
					totalBytes += ProcessIncomingMessage(messages[index]);
				processed += received;
			}
			while (ShouldContinueReceiveForTests(
				received, messages.Length, processed, budget));
			scope.End(processed, totalBytes);
		}

		private static int ProcessIncomingMessage(IntPtr pointer)
		{
			try
			{
				var message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(pointer);
				byte[] data = new byte[message.m_cbSize];
				Marshal.Copy(message.m_pData, data, 0, message.m_cbSize);
				ulong senderId = message.m_identityPeer.GetSteamID64();
				if (TryCreateHostDispatchContext(
					    message.m_conn, senderId, out DispatchContext context))
					PacketHandler.HandleIncoming(data, context);
				else
					DebugConsole.LogWarning("[GameClient] Rejected message from a stale host connection");
				return message.m_cbSize;
			}
			catch (Exception exception)
			{
				DebugConsole.LogWarning(
					$"[GameClient] Failed to handle incoming packet: {exception}");
				return 0;
			}
			finally
			{
				SteamNetworkingMessage_t.Release(pointer);
			}
		}

		internal static int ReceiveBudgetForTests(int configured)
			=> Mathf.Clamp(configured, MinimumMessagesPerUpdate, 1024);

		internal static bool ShouldContinueReceiveForTests(
			int received, int requested, int processed, int budget)
			=> received == requested && processed < budget;
	}
}
