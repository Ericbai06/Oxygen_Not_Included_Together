using System;
using System.Runtime.InteropServices;
using ONI_Together.DebugTools;
using ONI_Together.Misc;
using ONI_Together.Networking.Packets.Architecture;
using ONI_Together.Networking.Packets.Core;
using Shared.Profiling;
using Steamworks;

namespace ONI_Together.Networking.Transport.Steam
{
    public class SteamworksPacketSender : TransportPacketSender
    {
        public override bool SendPacket(object conn, SerializedPacket packet, PacketSendMode sendType = PacketSendMode.ReliableImmediate)
        {
            using var _ = Profiler.Scope();

            if (conn is not HSteamNetConnection connection)
                return false;

            return SendRaw(connection, packet, sendType);
        }

        private bool SendRaw(
            HSteamNetConnection connection,
			SerializedPacket packet,
			PacketSendMode sendType)
        {
			byte[] bytes = packet.Bytes;
			if (bytes.Length >= Utils.MaxSteamNetworkingSocketsMessageSizeSend)
				return false;
            IntPtr unmanagedPointer = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, unmanagedPointer, bytes.Length);

				EResult result;
				try
				{
					result = SteamNetworkingSockets.SendMessageToConnection(
						connection,
						unmanagedPointer,
						(uint)bytes.Length,
						ConvertSendType(sendType),
						out _);
				}
				catch
				{
					SyncStats.RecordNativeSend(
						packet.PacketType, bytes.Length, success: false);
					throw;
				}

                bool sent = result == EResult.k_EResultOK;
			SyncStats.RecordNativeSend(packet.PacketType, bytes.Length, sent);

                if (!sent)
                {
					DebugConsole.LogWarning(
						$"[Sockets] Failed to send {packet.PacketType} to " +
						$"{connection.m_HSteamNetConnection} ({Utils.FormatBytes(bytes.Length)} | result: {result})");
                }
                else
                {
                    PacketTracker.TrackSent(new PacketTracker.PacketTrackData
                    {
						packet = packet.Packet,
                        size = bytes.Length
                    });
                    //DebugConsole.Log($"[Sockets] Sent {packet.Type} to conn {conn} ({Utils.FormatBytes(bytes.Length)})");
                }
                return sent;
            }
            finally
            {
                Marshal.FreeHGlobal(unmanagedPointer);
            }
        }

        public int ConvertSendType(PacketSendMode mode)
        {
            int result = 0;

            // Reliable / Unreliable
            if ((mode & PacketSendMode.Reliable) == PacketSendMode.Reliable)
                result |= 8;  // k_nSteamNetworkingSend_Reliable
            else
                result |= 0;  // k_nSteamNetworkingSend_Unreliable (implicitly 0)

            // Immediate (flush) corresponds to NoNagle behavior
            if ((mode & PacketSendMode.Immediate) == PacketSendMode.Immediate)
                result |= 1;  // k_nSteamNetworkingSend_NoNagle

            // NoDelay (drop if can't send soon)
            if ((mode & PacketSendMode.NoDelay) == PacketSendMode.NoDelay)
                result |= 4;  // k_nSteamNetworkingSend_NoDelay

            return result;
        }

    }
}
