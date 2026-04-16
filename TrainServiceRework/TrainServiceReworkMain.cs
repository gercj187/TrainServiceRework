// File: TrainServiceReworkMain.cs

using HarmonyLib;
using UnityModManagerNet;

namespace TrainServiceRework
{
    public static class Main
    {
        private static Harmony? harmony;

        public static bool Load(UnityModManager.ModEntry entry)
        {
            try
            {
                harmony = new Harmony(entry.Info.Id);
                harmony.PatchAll();

                entry.Logger.Log("TrainServiceRework loaded successfully");

                return true;
            }
            catch (System.Exception ex)
            {
                entry.Logger.Error("TrainServiceRework failed to load: " + ex);
                return false;
            }
        }
    }
}