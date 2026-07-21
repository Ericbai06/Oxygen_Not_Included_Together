using System.Collections;
using System.Collections.Generic;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using UnityEngine;

namespace ONI_Together.Networking
{
	internal readonly struct ProtocolRejectionKey
	{
		internal readonly ulong ClientId;
		internal readonly object Connection;
		internal readonly long ConnectionGeneration;

		internal ProtocolRejectionKey(
			ulong clientId, object connection, long connectionGeneration)
		{
			ClientId = clientId;
			Connection = connection;
			ConnectionGeneration = connectionGeneration;
		}

		internal bool Matches(ProtocolRejectionKey other)
			=> ClientId == other.ClientId
			   && ReferenceEquals(Connection, other.Connection)
			   && ConnectionGeneration == other.ConnectionGeneration;
	}

	internal static class ProtocolRejectionBarrier
	{
		private const float DisconnectDelaySeconds = 2f;
		private static readonly Dictionary<ulong, ProtocolRejectionKey> Pending = new();

		internal static void Begin(
			ulong clientId, object connection, long connectionGeneration)
		{
			var key = new ProtocolRejectionKey(clientId, connection, connectionGeneration);
			Pending[clientId] = key;
			if (Global.Instance != null)
			{
				Global.Instance.StartCoroutine(ExpireAfterDelay(key));
				return;
			}
			GameScheduler.Instance?.Schedule(
				"ONI Together protocol rejection", DisconnectDelaySeconds, _ => Expire(key));
		}

		internal static bool TryAcknowledge(DispatchContext context, int hostProtocolVersion)
		{
			if (!Pending.TryGetValue(context.SenderId, out ProtocolRejectionKey expected)
			    || !IsValidAck(expected, hostProtocolVersion, context))
				return false;
			Pending.Remove(context.SenderId);
			NetworkConfig.TransportServer?.KickClient(context.SenderId);
			return true;
		}

		internal static bool IsValidAck(
			ProtocolRejectionKey expected,
			int hostProtocolVersion,
			DispatchContext context)
			=> !context.SenderIsHost
			   && context.SenderId == expected.ClientId
			   && context.ConnectionGeneration == expected.ConnectionGeneration
			   && ProtocolCompatibility.SupportsVersion(hostProtocolVersion)
			   && IsCurrentConnection(expected);

		internal static void Reset() => Pending.Clear();

		internal static void ExpireForTests(ProtocolRejectionKey key) => Expire(key);

		private static IEnumerator ExpireAfterDelay(ProtocolRejectionKey key)
		{
			yield return new WaitForSecondsRealtime(DisconnectDelaySeconds);
			Expire(key);
		}

		private static void Expire(ProtocolRejectionKey key)
		{
			if (!Pending.TryGetValue(key.ClientId, out ProtocolRejectionKey expected)
			    || !expected.Matches(key))
				return;
			Pending.Remove(key.ClientId);
			if (!IsCurrentConnection(key))
				return;
			DebugConsole.LogWarning(
				$"[ProtocolRejection] ACK timeout for client {key.ClientId}; disconnecting");
			NetworkConfig.TransportServer?.KickClient(key.ClientId);
		}

		private static bool IsCurrentConnection(ProtocolRejectionKey key)
			=> MultiplayerSession.ConnectedPlayers.TryGetValue(
				   key.ClientId, out MultiplayerPlayer player)
			   && player.IsCurrentConnection(key.Connection, key.ConnectionGeneration);
	}
}
