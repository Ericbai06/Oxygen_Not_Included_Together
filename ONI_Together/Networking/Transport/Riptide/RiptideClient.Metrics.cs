using System;
using System.Net;
using Riptide;
using Riptide.Utils;
using ONI_Together.DebugTools;
using ONI_Together.Networking.Packets.Architecture;
using Shared.Profiling;
using ONI_Together.Misc;
using System.Collections.Concurrent;
using ONI_Together.Menus;
using System.Collections.Generic;
using UnityEngine;
using System.Collections;
using ONI_Together.Networking.States;
using ONI_Together.UI;
using Steamworks;
using System.Threading;
using static ONI_Together.STRINGS.UI.MP_OVERLAY;

namespace ONI_Together.Networking.Transport.Lan
{
    public partial class RiptideClient : TransportClient
    {
        public override NetworkIndicatorsScreen.NetworkState GetJitterState()
        {
            using var _ = Profiler.Scope();

            if (_client == null || !_client.IsConnected)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            int ping = _client.RTT;
            if (ping <= 0)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            _pingSamples.Enqueue(ping);
            while (_pingSamples.Count > JITTER_SAMPLE_COUNT)
                _pingSamples.Dequeue();

            if (_pingSamples.Count < 5)
                return NetworkIndicatorsScreen.NetworkState.DEGRADED;

            float mean = 0f;
            foreach (var p in _pingSamples)
                mean += p;
            mean /= _pingSamples.Count;

            float variance = 0f;
            foreach (var p in _pingSamples)
            {
                float diff = p - mean;
                variance += diff * diff;
            }

            float jitter = Mathf.Sqrt(variance / _pingSamples.Count);

            if (jitter <= 10f)
                return NetworkIndicatorsScreen.NetworkState.GOOD;

            if (jitter <= 30f)
                return NetworkIndicatorsScreen.NetworkState.DEGRADED;

            return NetworkIndicatorsScreen.NetworkState.BAD;
        }

        public override NetworkIndicatorsScreen.NetworkState GetLatencyState()
        {
            using var _ = Profiler.Scope();

            if (_client == null || !_client.IsConnected)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            int ping = _client.SmoothRTT;
            if (ping <= 0)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            if (ping <= NetworkConfig.PingRanges.DEGRADED)
                return NetworkIndicatorsScreen.NetworkState.GOOD;

            if (ping <= NetworkConfig.PingRanges.BAD)
                return NetworkIndicatorsScreen.NetworkState.DEGRADED;

            return NetworkIndicatorsScreen.NetworkState.BAD;
        }

        public override NetworkIndicatorsScreen.NetworkState GetPacketlossState()
        {
            using var _ = Profiler.Scope();

            var metrics = Metrics;
            if (metrics == null)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            float lossRate = metrics.RollingNotifyLossRate; // 0–1
            float quality = 1f - lossRate;

            if (quality >= 0.95f)
                return NetworkIndicatorsScreen.NetworkState.GOOD;

            if (quality >= 0.85f)
                return NetworkIndicatorsScreen.NetworkState.DEGRADED;

            return NetworkIndicatorsScreen.NetworkState.BAD;
        }

        public override NetworkIndicatorsScreen.NetworkState GetServerPerformanceState()
        {
            using var _ = Profiler.Scope();

            // Until this is improved later just assume good.
            return NetworkIndicatorsScreen.NetworkState.GOOD;

            if (_client == null || !_client.IsConnected)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            var metrics = Metrics;
            if (metrics == null)
                return NetworkIndicatorsScreen.NetworkState.BAD;

            var reliableSends = metrics.RollingReliableSends;

            double meanResends = reliableSends.Mean;
            double resendStdDev = reliableSends.StandardDev;

            float lossRate = metrics.RollingNotifyLossRate;
            float remoteQuality = 1f - lossRate;


            DebugConsole.Log(
                $"[NET] Resends(mean={meanResends:F2}, std={resendStdDev:F2}) | " +
                $"Loss={lossRate:P2} | Quality={remoteQuality:P2}"
            );

            if (meanResends >= 2.0 ||           // On average needs 2+ sends per reliable
                resendStdDev >= 1.0 ||          // Highly unstable resend behavior
                remoteQuality <= 0.85f)         // Bad server quality
            {
                return NetworkIndicatorsScreen.NetworkState.BAD;
            }

            if (meanResends >= 1.2 ||            // Frequent retransmits
                resendStdDev >= 0.5 ||           // Congestion spikes
                remoteQuality <= 0.95f)          // Degraded server quality
            {
                return NetworkIndicatorsScreen.NetworkState.DEGRADED;
            }

            return NetworkIndicatorsScreen.NetworkState.GOOD;
        }

        IEnumerator WaitForConnectionSuccess(int timeout, long connectionEpoch)
        {
            using var _ = Profiler.Scope();

            float timer = 0f;

            bool wasSuccessful = false;
            while (timer < timeout)
            {
				if (!IsCurrentConnectionEpoch(connectionEpoch))
					yield break;
                _client?.Update(); // Update needs to happen during this process so that the client can acknowledge the connection and trigger the Connected event
                if (_client != null && _client.IsConnected)
                {
                    DebugConsole.Log("[LanClient] Connection successful");
                    MultiplayerOverlay.Close();
                    wasSuccessful = true;
                    yield break;
                }

                timer += Time.deltaTime;
                yield return null;
            }

            if (!wasSuccessful)
            {
				if (!IsCurrentConnectionEpoch(connectionEpoch))
					yield break;
				CleanupRiptide(connectionEpoch);
				OnReturnToMenu?.Invoke(
					CLIENT.RIPTIDE.CONNECTION_FAILED,
					CLIENT.RIPTIDE.CONNECTION_FAILED_DESC);
            } else
            {
                yield return null;
            }
        }

        void CleanupRiptide(long connectionEpoch = 0)
        {
            using var _ = Profiler.Scope();
			if (connectionEpoch > 0 && !IsCurrentConnectionEpoch(connectionEpoch))
				return;
			ClearIncomingPackets();
			ResetClientMembership();

            // Timeout reached — double check we didn't connect at the last frame
            if (_client != null && !_client.IsConnected)
            {
                DebugConsole.LogWarning("[LanClient] Connection timed out");

                /*
                if (MultiplayerSession.IsClient)
                {
                    // Display lost connection to host and return to the main menu
                    NetworkConfig.TransportClient.OnReturnToMenu.Invoke("Connection lost.", "Timed out");
                }
                */

                try
                {
                    _client.Disconnect();

                    _client.Connected -= OnConnectedToServer;
                    _client.Disconnected -= OnDisconnectedFromServer;
                    _client.MessageReceived -= OnMessageRecievedFromServer;
                    _client.ClientConnected -= OnOtherClientConnected;
                    _client.ClientDisconnected -= OnOtherClientDisconnected;

                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[LanClient] Error during timeout cleanup: {ex}");
                }

                _client = null;
            }

            MultiplayerSession.HostUserID = Utils.NilUlong();
            MultiplayerSession.InSession = false;
			EndConnectionEpoch(connectionEpoch > 0
				? connectionEpoch
				: Interlocked.Read(ref _activeConnectionEpoch));
        }

        /*IEnumerator Handshake()
        {
            // Recycle the handshake packet
            HandshakePacket handshake = new HandshakePacket();
            while (_client != null && _client.IsConnected)
            {
                if (_client.Connection == null)
                {
                    Debug.Log("[Handshake] Connection is null, waiting...");
                    yield return null;
                    continue;
                }

                Debug.Log("[Handshake] Sending handshake packet...");
                NetworkConfig.TransportPacketSender.SendToConnection(_client.Connection, handshake, SteamNetworkingSend.Reliable);

                yield return new WaitForSeconds(1f);

                if (_client.IsNotConnected)
                {
                    Debug.Log("[Handshake] Client disconnected, stopping handshake coroutine.");
                    break;
                }
            }
            yield return null;
        }*/

        private (string reason, string message) GetDisconnectInfo(DisconnectedEventArgs e)
        {
            switch (e.Reason)
            {
                case DisconnectReason.NeverConnected:
                    return (
                        CLIENT.RIPTIDE.CONNECTION_FAILED,
                        CLIENT.RIPTIDE.CONNECTION_FAILED_DESC
                    );

                case DisconnectReason.ConnectionRejected:
                    return (
                        CLIENT.RIPTIDE.CONNECTION_REJECTED,
                        CLIENT.RIPTIDE.CONNECTION_REJECTED_DESC
                    );

                case DisconnectReason.TransportError:
                    return (
                        CLIENT.RIPTIDE.NETWORK_ERROR,
                        CLIENT.RIPTIDE.NETWORK_ERROR_DESC
                    );

                case DisconnectReason.TimedOut:
                    return (
                        CLIENT.RIPTIDE.CONNECTION_TIMED_OUT,
                        CLIENT.RIPTIDE.CONNECTION_TIMED_OUT_DESC
                    );

                case DisconnectReason.Kicked:
                    return (
                        CLIENT.RIPTIDE.KICKED,
                        CLIENT.RIPTIDE.KICKED_DESC
                    );

                case DisconnectReason.ServerStopped:
                    return (
                        CLIENT.RIPTIDE.SERVER_CLOSED,
                        CLIENT.RIPTIDE.SERVER_CLOSED_DESC
                    );

                case DisconnectReason.PoorConnection:
                    return (
                        CLIENT.RIPTIDE.CONNECTION_UNSTABLE,
                        CLIENT.RIPTIDE.CONNECTION_UNSTABLE_DESC
                    );

                case DisconnectReason.Disconnected:
                    return ("", ""); // client initiated

                default:
                    return (
                        CLIENT.RIPTIDE.UNKNOWN,
                        CLIENT.RIPTIDE.UNKNOWN_DESC
                    );
            }
        }

        public override int GetPing()
        {
            using var _ = Profiler.Scope();

            if (_client == null || !_client.IsConnected)
                return -1;

            return _client.SmoothRTT;
        }
    }
}
