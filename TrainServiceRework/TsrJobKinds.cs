// File: TsrJobKinds.cs  (for ZG2 – optional TrainServiceRework integration, no reference to TSR needed)
//
// Usage in ZG2:
//   TsrJobKinds.Init();                       // once, after all mods are loaded (e.g. first frame / LateInit)
//   foreach (string code in TsrJobKinds.Codes) // add to the configurable job type list, e.g. as "TSR:DH"
//       ... TsrJobKinds.GetName(code), TsrJobKinds.IsHaul(code) (default: check hauls, skip shunting)
//   string? code = TsrJobKinds.GetKindCode(job); // null = normal vanilla job → use job.jobType as before
//
// If TSR is not installed everything returns empty / null and ZG2 behaves as before.

using System;
using System.Reflection;
using DV.Logic.Job;
using HarmonyLib;

namespace Zg2   // adjust to the ZG2 namespace
{
    public static class TsrJobKinds
    {
        private static bool initialized;
        private static MethodInfo? getKindCode;
        private static MethodInfo? getLocalizedName;
        private static MethodInfo? isHaul;

        public static bool Available { get; private set; }
        public static string[] Codes { get; private set; } = Array.Empty<string>();

        public static void Init()
        {
            if (initialized)
                return;

            initialized = true;

            try
            {
                // only search when the mod is active, so AccessTools does not log "type not found"
                var mod = UnityModManagerNet.UnityModManager.FindMod("TrainServiceRework");
                if (mod == null || !mod.Enabled)
                    return;

                Type? api = AccessTools.TypeByName("TrainServiceRework.JobKinds");
                if (api == null)
                    return;

                getKindCode = api.GetMethod("GetKindCode", new[] { typeof(Job) });
                getLocalizedName = api.GetMethod("GetLocalizedName", new[] { typeof(string) });
                isHaul = api.GetMethod("IsHaul", new[] { typeof(string) });

                if (api.GetMethod("GetKindCodes")?.Invoke(null, null) is string[] codes && getKindCode != null)
                {
                    Codes = codes;
                    Available = true;
                }
            }
            catch (Exception)
            {
                Available = false;
            }
        }

        /// <summary>TSR kind code ("DH", "MH", "SO", "SM", "SS") or null for vanilla jobs.</summary>
        public static string? GetKindCode(Job? job)
        {
            if (!Available || job == null)
                return null;

            try { return getKindCode!.Invoke(null, new object[] { job }) as string; }
            catch (Exception) { return null; }
        }

        public static string GetName(string code)
        {
            try { return getLocalizedName?.Invoke(null, new object[] { code }) as string ?? code; }
            catch (Exception) { return code; }
        }

        public static bool IsHaul(string code)
        {
            try { return isHaul?.Invoke(null, new object[] { code }) is bool b && b; }
            catch (Exception) { return false; }
        }
    }
}
