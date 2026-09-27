using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace NightSignal.Net
{
    /// <summary>Creates the single NetworkManager used by the race server or race client (no scene management).</summary>
    public static class NetBootstrap
    {
        public static NetworkManager Ensure()
        {
            if (NetworkManager.Singleton != null) return NetworkManager.Singleton;
            var go = new GameObject("NetworkManager");
            Object.DontDestroyOnLoad(go);
            var nm = go.AddComponent<NetworkManager>();
            var utp = go.AddComponent<UnityTransport>();
            utp.MaxPacketQueueSize = 512; // 128 overflowed on the server while six clients connected and loaded
            // The match message carries every human's frozen build and livery (a livery is ≤ 5,120 bytes in its wire form,
            // typically well under 1 KB): six worst-case liveries exceed the 6 KB default for one fragmented message.
            utp.MaxPayloadSize = 64 * 1024;
            nm.NetworkConfig = new NetworkConfig
            {
                NetworkTransport = utp,
                TickRate = 60,
                ConnectionApproval = true,
                EnableSceneManagement = false,
                ClientConnectionBufferTimeout = 15,
            };
            return nm;
        }

        public static UnityTransport Transport(NetworkManager nm) => (UnityTransport)nm.NetworkConfig.NetworkTransport;

        /// <summary>Race clock in microseconds for a tick (0 at the start tick).</summary>
        public static long RaceMicros(int tick, int startTick) => (long)(tick - startTick) * 1_000_000L / 60L;
    }
}
