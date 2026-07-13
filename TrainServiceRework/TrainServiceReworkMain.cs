using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace TrainServiceRework
{
    public class Settings : UnityModManager.ModSettings
    {
        public bool EnableLogs = false;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }

    public static class Main
    {
        private static Harmony? harmony;
        private static UnityModManager.ModEntry? modEntry;

        public static Settings Settings { get; private set; } = new Settings();

        public static bool Load(UnityModManager.ModEntry entry)
        {
            modEntry = entry;

            try
            {
                Settings = UnityModManager.ModSettings.Load<Settings>(entry);

                if (Settings == null)
                {
                    Settings = new Settings();
                    Settings.Save(entry);
                }

                Settings.Save(entry);

                harmony = new Harmony(entry.Info.Id);
                harmony.PatchAll();

                entry.Logger.Log("mod loaded successfully");

                if (Settings.EnableLogs)
                {
                    entry.Logger.Log("Debug logging enabled.");
                }

                return true;
            }
            catch (System.Exception ex)
            {
                entry.Logger.Error(
                    "mod failed to load: " + ex);

                return false;
            }
        }

        public static void Log(string message)
        {
            if (!Settings.EnableLogs)
                return;

            Debug.Log("[TrainServiceRework] " + message);
        }

        public static void LogWarning(string message)
        {
            Debug.LogWarning("[TrainServiceRework] " + message);
        }

        public static void LogError(string message)
        {
            Debug.LogError("[TrainServiceRework] " + message);
        }
    }
}