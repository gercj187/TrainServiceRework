// File: TrainServiceReworkJobKindsApi.cs
//
// Public API for other mods (e.g. ZG2): the job kinds TSR brings along.
//
// The TSR jobs are vanilla jobs underneath (DH = Transport/FH, MH = EmptyHaul/LH, SO/SM/SS = ShuntingUnload).
// The kind is encoded in the job ID (e.g. "SM-DH-82"). Other mods should NOT parse the ID themselves but ask this API,
// so new kinds appear in their settings automatically.
//
// Only primitive / vanilla types are used, so the API can be called via reflection without referencing TSR:
//
//   Type? api = AccessTools.TypeByName("TrainServiceRework.JobKinds");          // null = TSR not installed
//   string[] codes = (string[])api.GetMethod("GetKindCodes").Invoke(null, null);  // "DH", "MH", "SO", "SM", "SS"
//   string? code   = (string?)api.GetMethod("GetKindCode", new[] { typeof(Job) }).Invoke(null, new object[] { job });
//   string  name   = (string)api.GetMethod("GetLocalizedName").Invoke(null, new object[] { code });
//   bool    haul   = (bool)api.GetMethod("IsHaul").Invoke(null, new object[] { code });
//
// Codes are stable and are never renamed (safe to store in settings files).

using System;
using DV.Localization;
using DV.Logic.Job;
using DV.ThingTypes;

namespace TrainServiceRework
{
    public static class JobKinds
    {
        /// <summary>Raised by new API versions only when methods are added; existing methods never change.</summary>
        public const int ApiVersion = 1;

        /// <summary>Prefix other mods can use for their own setting keys / display (e.g. "TSR:DH").</summary>
        public const string ModId = "TrainServiceRework";

        public const string DamagedHaul = "DH";
        public const string MaintenanceHaul = "MH";
        public const string ShuntingOut = "SO";
        public const string ShuntingMaintenance = "SM";
        public const string ShuntingSorting = "SS";

        private static readonly string[] codes = { DamagedHaul, MaintenanceHaul, ShuntingOut, ShuntingMaintenance, ShuntingSorting };

        /// <summary>All job kinds TSR brings along (copy, safe to modify).</summary>
        public static string[] GetKindCodes() => (string[])codes.Clone();

        /// <summary>Kind code of a job, or null if it is no TSR job.</summary>
        public static string? GetKindCode(Job? job) => job == null ? null : GetKindCodeFromJobId(job.ID);

        /// <summary>Kind code from a job ID (e.g. "SM-DH-82" → "DH"; old IDs "SH"/"MS" → "DH"/"SM"), or null if it is no TSR job.</summary>
        public static string? GetKindCodeFromJobId(string? jobId)
        {
            switch (SpecialJobBookletColorHelper.GetServiceKind(jobId))
            {
                case SpecialJobBookletColorKind.DamagedFreight: return DamagedHaul;
                case SpecialJobBookletColorKind.RepairEmptyHaul: return MaintenanceHaul;
                case SpecialJobBookletColorKind.ShuntingOut:
                case SpecialJobBookletColorKind.Decommission: return ShuntingOut;      // old test version (AR)
                case SpecialJobBookletColorKind.Maintenance:
                case SpecialJobBookletColorKind.Overhaul: return ShuntingMaintenance;          // old test version (IS)
                case SpecialJobBookletColorKind.Sorting: return ShuntingSorting;
                default: return null;
            }
        }

        /// <summary>True if the job is one of the TSR job kinds.</summary>
        public static bool IsTsrJob(Job? job) => GetKindCode(job) != null;

        /// <summary>English name as printed on the booklet (e.g. "MAINTENANCE HAUL"). Empty for unknown codes.</summary>
        public static string GetEnglishName(string? code)
        {
            switch (code)
            {
                case DamagedHaul: return "DAMAGED HAUL";
                case MaintenanceHaul: return "MAINTENANCE HAUL";
                case ShuntingOut: return "SHUNTING OUT";
                case ShuntingMaintenance: return "SHUNTING MAINTENANCE";
                case ShuntingSorting: return "SHUNTING SORTING";
                default: return string.Empty;
            }
        }

        /// <summary>Name in the current game language (falls back to English). Empty for unknown codes.</summary>
        public static string GetLocalizedName(string? code)
        {
            string key = TranslationKey(code);

            if (key.Length == 0)
                return string.Empty;

            try
            {
                string text = LocalizationAPI.L(key);

                if (!string.IsNullOrEmpty(text) && text != key)
                    return text;
            }
            catch (Exception)
            {
                // localization not ready yet
            }

            return GetEnglishName(code);
        }

        /// <summary>I2 translation key of the name (for mods that localize themselves). Empty for unknown codes.</summary>
        public static string TranslationKey(string? code)
        {
            switch (code)
            {
                case DamagedHaul: return TrainServiceReworkTranslations.DamagedFreightJobTypeKey;
                case MaintenanceHaul: return TrainServiceReworkTranslations.RepairEmptyHaulJobTypeKey;
                case ShuntingOut: return TrainServiceReworkTranslations.DecommissionJobTypeKey;
                case ShuntingMaintenance: return TrainServiceReworkTranslations.MaintenanceJobTypeKey;
                case ShuntingSorting: return TrainServiceReworkTranslations.SortingJobTypeKey;
                default: return string.Empty;
            }
        }

        /// <summary>Vanilla job type the kind is built on (DH = Transport, MH = EmptyHaul, SO/SM/SS = ShuntingUnload), null for unknown codes.</summary>
        public static JobType? GetBaseJobType(string? code)
        {
            switch (code)
            {
                case DamagedHaul: return JobType.Transport;
                case MaintenanceHaul: return JobType.EmptyHaul;
                case ShuntingOut:
                case ShuntingMaintenance:
                case ShuntingSorting: return JobType.ShuntingUnload;
                default: return null;
            }
        }

        /// <summary>True for kinds that drive from one station to another (DH, MH), false for shunting inside a station.</summary>
        public static bool IsHaul(string? code) => code == DamagedHaul || code == MaintenanceHaul;
    }
}
