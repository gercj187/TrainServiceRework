using System.Collections.Generic;
using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Types;

namespace TrainServiceRework.Multiplayer
{
    /// <summary>
    /// Multiplayer bridge (own DLL, only loaded when dv-multiplayer is active – see ImmersiveServiceMultiplayer).
    /// Host: runs all service jobs, sends settings + state. Clients: only display.
    /// </summary>
    public class MpBridge : IImmersiveServiceMpBridge
    {
        private static IServer server;
        private bool hooked;

        public void Init(string modId)
        {
            if (hooked || !MultiplayerAPI.IsMultiplayerLoaded)
                return;

            hooked = true;

            // must be installed on host and clients (display lines, lamps, lever lock)
            MultiplayerAPI.Instance.SetModCompatibility(modId, MultiplayerCompatibility.All);

            MultiplayerAPI.ServerStarted += OnServerStarted;
            MultiplayerAPI.ServerStopped += OnServerStopped;
            MultiplayerAPI.ClientStarted += OnClientStarted;
            MultiplayerAPI.ClientStopped += OnClientStopped;

            // server / client may already be running (load order not guaranteed)
            if (MultiplayerAPI.Server != null)
                OnServerStarted(MultiplayerAPI.Server);

            if (MultiplayerAPI.Client != null)
                OnClientStarted(MultiplayerAPI.Client);
        }

        public bool IsClientOnly()
        {
            return MultiplayerAPI.IsMultiplayerLoaded
                && MultiplayerAPI.Instance != null
                && MultiplayerAPI.Instance.IsConnected
                && !MultiplayerAPI.Instance.IsHost;
        }

        // ------------------------------------------------------------------ Host

        private static void OnServerStarted(IServer startedServer)
        {
            server = startedServer;

            // players joining later get the host settings and all running service jobs
            startedServer.OnPlayerReady += player =>
            {
                if (server == null || player == null)
                    return;

                server.SendSerializablePacketToPlayer(ImmersiveServiceConfigPacket.FromConfig(ImmersiveServiceSystem.Config), player, true);
                server.SendSerializablePacketToPlayer(ImmersiveServiceStatePacket.FromSnapshot(ImmersiveServiceSystem.BuildSnapshot()), player, true);
            };

            Main.Log("MULTIPLAYER -> server started");
        }

        private static void OnServerStopped()
        {
            server = null;
        }

        public void BroadcastConfig()
        {
            if (server == null)
                return;

            server.SendSerializablePacketToAll(ImmersiveServiceConfigPacket.FromConfig(ImmersiveServiceSystem.Config), true, true, null);
        }

        public void SendState(List<KeyValuePair<string, List<ServiceJobView>>> snapshot)
        {
            if (server == null)
                return;

            server.SendSerializablePacketToAll(ImmersiveServiceStatePacket.FromSnapshot(snapshot), true, true, null);
        }

        // ------------------------------------------------------------------ Client

        private void OnClientStarted(IClient client)
        {
            client.RegisterSerializablePacket<ImmersiveServiceConfigPacket>(p =>
            {
                if (IsClientOnly())
                    ImmersiveServiceSystem.ApplyHostConfig(p.ToConfig());
            });

            client.RegisterSerializablePacket<ImmersiveServiceStatePacket>(p =>
            {
                if (IsClientOnly())
                    ImmersiveServiceSystem.ApplyRemoteState(p.Stations);
            });

            Main.Log("MULTIPLAYER -> client started");
        }

        private static void OnClientStopped()
        {
            ImmersiveServiceSystem.ClearHostConfig();
        }
    }
}
