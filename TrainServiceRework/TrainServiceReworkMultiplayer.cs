// File: TrainServiceReworkMultiplayer.cs
//
// Switch to the multiplayer mod (AMacro dv-multiplayer).
//
// The complete MP code lives in its OWN DLL (TrainServiceRework.Multiplayer.dll), which is only loaded
// when the multiplayer mod is active. Reason: UMM calls Assembly.GetTypes() while loading – if this
// (main) DLL contained types depending on MultiplayerAPI.dll, loading would fail without the multiplayer
// mod installed (ReflectionTypeLoadException, mod doesn't load).
//
// Principle (like dv-multiplayer itself): the host is authoritative. Only the host runs the service jobs,
// saves them and dictates the settings.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityModManagerNet;

namespace TrainServiceRework
{
    // =========================================================
    // CONTRACT FOR TrainServiceRework.Multiplayer.dll
    // (no MPAPI types here – the main DLL has no dependency on MultiplayerAPI)
    // =========================================================
    public interface IImmersiveServiceMpBridge
    {
        void Init(string modId);
        bool IsClientOnly();
        void BroadcastConfig();
        void SendState(List<KeyValuePair<string, List<ServiceJobView>>> snapshot);
    }

    // =========================================================
    // LOADER / FACADE
    // =========================================================
    public static class ImmersiveServiceMultiplayer
    {
        private const string BridgeDll = "TrainServiceRework.Multiplayer.dll";
        private const string BridgeType = "TrainServiceRework.Multiplayer.MpBridge";

        private static IImmersiveServiceMpBridge? bridge;
        private static bool initialized;

        /// <summary>Multiplayer mod active and bridge loaded.</summary>
        public static bool Present => bridge != null;

        /// <summary>Connected but not host → no own service logic, settings hidden.</summary>
        public static bool IsClientOnly
        {
            get
            {
                IImmersiveServiceMpBridge? current = bridge;

                if (current == null)
                    return false;

                try
                {
                    return current.IsClientOnly();
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>Once after all mods are loaded (OnLateUpdate) – load order is not guaranteed.</summary>
        public static void Init(UnityModManager.ModEntry modEntry)
        {
            if (initialized)
                return;

            initialized = true;

            UnityModManager.ModEntry multiplayer = UnityModManager.FindMod("Multiplayer");

            if (multiplayer == null || !multiplayer.Enabled || !multiplayer.Active)
                return;

            string path = Path.Combine(modEntry.Path, BridgeDll);

            if (!File.Exists(path))
            {
                Main.LogWarning($"Multiplayer active, but {BridgeDll} is missing – no synchronisation");
                return;
            }

            try
            {
                Assembly assembly = Assembly.LoadFrom(path);
                Type type = assembly.GetType(BridgeType, true);
                IImmersiveServiceMpBridge? created = Activator.CreateInstance(type) as IImmersiveServiceMpBridge;

                if (created == null)
                {
                    Main.LogWarning($"{BridgeType} does not implement IImmersiveServiceMpBridge");
                    return;
                }

                created.Init(modEntry.Info.Id);
                bridge = created;

                Main.Log("MULTIPLAYER -> detected, immersive service sync active");
            }
            catch (Exception ex)
            {
                bridge = null;
                Main.LogError("MULTIPLAYER -> bridge failed: " + ex);
            }
        }

        public static void BroadcastConfig()
        {
            IImmersiveServiceMpBridge? current = bridge;

            if (current == null)
                return;

            try
            {
                current.BroadcastConfig();
            }
            catch (Exception ex)
            {
                Main.LogWarning("MULTIPLAYER -> sending settings failed: " + ex.Message);
            }
        }

        public static void SendState(List<KeyValuePair<string, List<ServiceJobView>>> snapshot)
        {
            IImmersiveServiceMpBridge? current = bridge;

            if (current == null)
                return;

            try
            {
                current.SendState(snapshot);
            }
            catch (Exception ex)
            {
                Main.LogWarning("MULTIPLAYER -> sending service state failed: " + ex.Message);
            }
        }
    }
}
