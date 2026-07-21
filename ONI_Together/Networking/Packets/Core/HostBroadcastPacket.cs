using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using System;
using System.IO;
using System.Threading;
using Shared.Interfaces.Networking;
using Shared.Profiling;
using UnityEngine;

namespace ONI_Together.Networking.Packets.Core
{
	internal interface IHostAuthoritativeRelay
	{
	}

	/// <summary>
	/// used by clients to broadcast a packet to all other clients via the host
	/// </summary>
	internal class HostBroadcastPacket : IPacket
	{
		internal enum RelayDomain : byte
		{
			MustExecute,
			LatestState,
		}

		internal readonly struct RelayDispatchActions
		{
			internal readonly Func<IPacket, DispatchContext, bool> Dispatch;
			internal readonly Action<IPacket, ulong> FanOut;

			internal RelayDispatchActions(
				Func<IPacket, DispatchContext, bool> dispatch,
				Action<IPacket, ulong> fanOut)
			{
				Dispatch = dispatch;
				FanOut = fanOut;
			}
		}

		private sealed class CachedRelay
		{
			internal IPacket Packet { get; set; }
			internal DispatchContext Context { get; set; }
		}

		public const int MaxInnerPacketBytes = 1024 * 1024;
		private const int RelayWireOverheadBytes =
			sizeof(int) + sizeof(int) + sizeof(ulong) + sizeof(ulong) + sizeof(int);
		private static readonly HostBroadcastReorder<CachedRelay> Reorder =
			new(DispatchCachedRelay, KickSender);
		private static long _nextMustExecuteSequence;
		private static long _nextLatestStateSequence;

		public static void ResetSessionState()
		{
			Reorder.Reset();
			ResetClientRequestSequences();
		}

		internal static void ResetClientRequestSequences()
		{
			Interlocked.Exchange(ref _nextMustExecuteSequence, 0);
			Interlocked.Exchange(ref _nextLatestStateSequence, 0);
		}

		public HostBroadcastPacket() { }
		public HostBroadcastPacket(IPacket innerPacket, ulong sender)
		{
			using var _ = Profiler.Scope();

			InnerPacketId = API_Helper.GetHashCode(innerPacket.GetType());
			using var ms = new MemoryStream();
			using var writer = new BinaryWriter(ms);
			innerPacket.Serialize(writer);
			InnerPacketData = ms.ToArray();
			SenderId = sender;
			RequestId = NextClientRequestSequence(GetRelayDomain(innerPacket));
		}

		private static ulong NextClientRequestSequence(RelayDomain domain)
		{
			long sequence = domain == RelayDomain.MustExecute
				? Interlocked.Increment(ref _nextMustExecuteSequence)
				: Interlocked.Increment(ref _nextLatestStateSequence);
			return unchecked((ulong)sequence);
		}

		internal static PacketSendMode GetRelaySendMode(IPacket packet)
			=> packet is PlayerCursorPacket
				? PacketSendMode.Unreliable
				: PacketSendMode.Reliable;

		internal static int GetRelayWireSize(IPacket innerPacket)
		{
			if (innerPacket == null)
				return int.MaxValue;
			using var stream = new MemoryStream();
			using (var writer = new BinaryWriter(
				       stream, System.Text.Encoding.UTF8, leaveOpen: true))
				innerPacket.Serialize(writer);
			return checked(RelayWireOverheadBytes + (int)stream.Length);
		}

		internal static bool TryFitUnreliableRelay(PlayerCursorPacket cursor)
		{
			if (cursor == null)
				return false;
			cursor.EnforceUtilityPathBound();
			int wireSize = GetRelayWireSize(cursor);
			if (wireSize < PacketSender.MAX_PACKET_SIZE_UNRELIABLE)
				return true;
			if (cursor.HasUtilityPath && cursor.UtilityPathData != null)
			{
				int excessBytes = wireSize - (PacketSender.MAX_PACKET_SIZE_UNRELIABLE - 1);
				int keepCount = cursor.UtilityPathData.Length - (excessBytes + sizeof(uint) - 1) / sizeof(uint);
				cursor.TrimUtilityPathTo(keepCount);
			}
			return GetRelayWireSize(cursor) < PacketSender.MAX_PACKET_SIZE_UNRELIABLE;
		}

		internal static void BindSenderConnectionGeneration(IPacket packet, long generation)
		{
			if (packet is PlayerCursorPacket cursor)
				cursor.SenderConnectionGeneration = generation;
		}

		internal static void DropConnectionState(ulong senderId, long generation)
			=> Reorder.DropConnectionState(senderId, generation);

		internal static void CheckReorderTimeouts(float now)
			=> Reorder.CheckTimeouts(
				now, Math.Max(1f, Configuration.Instance.Host.TimeoutSeconds));


		int InnerPacketId;
		public ulong SenderId;
		public ulong RequestId;
		byte[] InnerPacketData = Array.Empty<byte>();

		public void Serialize(BinaryWriter writer)
		{
			using var _ = Profiler.Scope();

			writer.Write(InnerPacketId);
			writer.Write(SenderId);
			writer.Write(RequestId);
			writer.Write(InnerPacketData.Length);
			writer.Write(InnerPacketData);
		}
		public void Deserialize(BinaryReader reader)
		{
			using var _ = Profiler.Scope();

			InnerPacketId = reader.ReadInt32();
			SenderId = reader.ReadUInt64();
			RequestId = reader.ReadUInt64();
			int dataLength = reader.ReadInt32();
			if (dataLength < 0 || dataLength > MaxInnerPacketBytes)
				throw new InvalidDataException($"Invalid host-broadcast payload length: {dataLength}");
			if (reader.BaseStream.CanSeek && reader.BaseStream.Length - reader.BaseStream.Position < dataLength)
				throw new EndOfStreamException("Host-broadcast payload is truncated");
			InnerPacketData = reader.ReadBytes(dataLength);
			if (InnerPacketData.Length != dataLength)
				throw new EndOfStreamException("Host-broadcast payload is truncated");
		}

		public void OnDispatched()
		{
			using var _ = Profiler.Scope();

			DispatchContext context = PacketHandler.CurrentContext;
			if (!ValidateOuterAuthority(context))
				return;
			if (!TryDeserializeInner(context, out IPacket innerPacket))
				return;

			RelayDomain domain = GetRelayDomain(innerPacket);
			Reorder.Accept(new SequencedRelay<CachedRelay>
			{
				SenderId = context.SenderId,
				Generation = context.ConnectionGeneration,
				Domain = domain,
				Sequence = RequestId,
				Bytes = InnerPacketData.Length,
				Value = new CachedRelay { Packet = innerPacket, Context = context },
			}, Time.unscaledTime);
		}

		private bool ValidateOuterAuthority(DispatchContext context)
		{
			if (!MultiplayerSession.IsHost)
			{
				DebugConsole.LogWarning("[HostBroadcastPacket] clients cannot receive relay wrappers");
				return false;
			}
			if (context.SenderIsHost || context.SenderId != SenderId
			    || !PacketHandler.IsCurrentDispatchContext(context))
			{
				DebugConsole.LogWarning(
					$"[HostBroadcastPacket] invalid sender context: transport={context.SenderId}, wire={SenderId}");
				return false;
			}
			if (MultiplayerSession.GetPlayer(context.SenderId)?.ProtocolVerified == true)
				return true;
			DebugConsole.LogWarning($"[HostBroadcastPacket] rejected unverified sender {context.SenderId}");
			return false;
		}

		private bool TryDeserializeInner(DispatchContext context, out IPacket innerPacket)
		{
			innerPacket = null;
			if (!PacketRegistry.HasRegisteredPacket(InnerPacketId))
			{
				DebugConsole.LogWarning("[HostBroadcastPacket] unknown inner packet id found, cannot rebroadcast: "+InnerPacketId);
				return false;
			}
			innerPacket = PacketRegistry.Create(InnerPacketId);
			if (innerPacket is not IClientRelayable
			    && !PacketRegistry.CanClientDispatchModApi(innerPacket, relayed: true))
			{
				DebugConsole.LogWarning($"[HostBroadcastPacket] {innerPacket.GetType().Name} is not client-relayable");
				return false;
			}
			using var ms = new MemoryStream(InnerPacketData);
			using var reader = new BinaryReader(ms);
			innerPacket.Deserialize(reader);
			if (reader.BaseStream.Position != reader.BaseStream.Length)
			{
				DebugConsole.LogWarning($"[HostBroadcastPacket] trailing inner payload for {innerPacket.GetType().Name}");
				return false;
			}
			if (!IsInnerSenderValid(innerPacket, context.SenderId))
			{
				DebugConsole.LogWarning($"[HostBroadcastPacket] payload sender mismatch for {innerPacket.GetType().Name}: transport={context.SenderId}");
				return false;
			}
			BindSenderConnectionGeneration(innerPacket, context.ConnectionGeneration);
			return true;
		}

		internal static bool IsInnerSenderValid(IPacket innerPacket, ulong transportSenderId)
			=> innerPacket is not ISenderBoundRelay senderBound || senderBound.RelaySenderId == transportSenderId;

		private static RelayDomain GetRelayDomain(IPacket packet)
			=> packet is PlayerCursorPacket ? RelayDomain.LatestState : RelayDomain.MustExecute;

		internal static bool DispatchVerifiedRelayAndFanOut(
			IPacket innerPacket,
			DispatchContext transportContext,
			RelayDispatchActions actions)
		{
			if (!actions.Dispatch(innerPacket, transportContext.AsVerifiedHostBroadcast()))
				return false;

			if (innerPacket is not IHostAuthoritativeRelay)
				actions.FanOut(innerPacket, transportContext.SenderId);
			return true;
		}

		private static bool DispatchCachedRelay(CachedRelay relay)
		{
			try
			{
				DebugConsole.Log(
					"[HostBroadcastPacket] dispatching " + relay.Packet.GetType().Name);
				return DispatchVerifiedRelayAndFanOut(
					relay.Packet,
					relay.Context,
					new RelayDispatchActions(
						PacketHandler.DispatchNested,
						static (packet, senderId) => PacketSender.SendToAllExcluding(
							packet, [MultiplayerSession.HostUserID, senderId],
							GetRelaySendMode(packet))));
			}
			catch (Exception ex)
			{
				DebugConsole.LogWarning(
					$"[HostBroadcastPacket] relay dispatch failed for {relay.Context.SenderId}: {ex}");
				return false;
			}
		}

		private static void KickSender(ulong senderId)
		{
			DebugConsole.LogWarning(
				$"[HostBroadcastPacket] relay reorder failed for {senderId}; disconnecting");
			NetworkConfig.TransportServer?.KickClient(senderId);
		}

	}
}
