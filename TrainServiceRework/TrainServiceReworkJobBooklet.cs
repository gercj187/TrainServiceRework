// File: TrainServiceReworkJobBooklet.cs

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DV.Booklets;
using DV.Logic.Job;
using DV.Localization;
using DV.RenderTextureSystem.BookletRender;
using DV.ServicePenalty;
using DV.ThingTypes;

namespace TrainServiceRework
{
    // =========================================================
    // SPECIAL JOB BOOKLET SERVICE
    // =========================================================
    public enum SpecialJobBookletColorKind
    {
        None,
        DamagedFreight,
        RepairEmptyHaul
    }

    // =========================================================
    // SPECIAL JOB BOOKLET SERVICE HELPER
    // =========================================================
    public static class SpecialJobBookletColorHelper
    {
        private const float DamagedFreightCondition = 50f;
        private const float RepairEmptyHaulCondition = 50f;
        private static readonly Dictionary<string, SpecialJobBookletColorKind> specialJobColors = new Dictionary<string, SpecialJobBookletColorKind>();
        private static readonly Dictionary<string, string> jobIdOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [ThreadStatic]
        private static IReadOnlyList<TrainCar>? jobIdGenerationCars;
        private static readonly FieldInfo? idGeneratorExistingJobIdsField = AccessTools.Field(typeof(IdGenerator),"existingJobIds");

        private static bool missingIdGeneratorRegistryLogged;
		
        private static readonly FieldInfo jobTypeField = AccessTools.Field(typeof(FrontPageTemplatePaperData),"jobType");
        private static readonly PropertyInfo jobTypeProperty = AccessTools.Property(typeof(FrontPageTemplatePaperData),"jobType");
        private static readonly FieldInfo frontPageJobIdField = AccessTools.Field(typeof(FrontPageTemplatePaperData),"jobId");
        private static readonly PropertyInfo frontPageJobIdProperty = AccessTools.Property(typeof(FrontPageTemplatePaperData),"jobId");
        private static readonly FieldInfo coverPageJobIdField = AccessTools.Field(typeof(CoverPageTemplatePaperData),"jobId");
        private static readonly PropertyInfo coverPageJobIdProperty = AccessTools.Property(typeof(CoverPageTemplatePaperData),"jobId");
		
        private static readonly FieldInfo? debtDataIdField = AccessTools.Field(typeof(Debt_data),"ID");
        private static readonly FieldInfo? debtDataIdBackingField = AccessTools.Field(typeof(Debt_data),"<ID>k__BackingField");
        private static readonly PropertyInfo? debtDataIdProperty = AccessTools.Property(typeof(Debt_data),"ID");


        private static bool missingJobTypeMemberLogged;
        private static bool missingFrontPageJobIdMemberLogged;
        private static bool missingCoverPageJobIdMemberLogged;
        private static bool missingDebtDataIdMemberLogged;

        // =====================================================
        // REAL JOB-ID GENERATION CONTEXT
        // =====================================================
        public static void BeginJobIdGeneration(JobChainController jobChainController)
        {
            jobIdGenerationCars = null;

            if (jobChainController == null || jobChainController.carsForJobChain == null)
            {
                return;
            }

            try
            {
                jobIdGenerationCars = TrainCar.ExtractTrainCars(jobChainController.carsForJobChain);
            }
            catch (Exception ex)
            {
                Main.LogWarning(
                    $"JOB ID CONTEXT CAR EXTRACTION FAILED | " +
                    $"{ex.Message}");

                jobIdGenerationCars = null;
            }
        }

        public static void EndJobIdGeneration()
        {
            jobIdGenerationCars = null;
        }
		
        // =====================================================
        // REGISTER FROM JOB CHAIN
        // =====================================================
        public static void RegisterFromJobChain(JobChainController jobChainController)
        {
            if (jobChainController == null)
                return;

            Job job = jobChainController.currentJobInChain;

            if (job == null)
                return;

            if (jobChainController.carsForJobChain == null)
                return;

            List<TrainCar> trainCars;

            try
            {
                trainCars = TrainCar.ExtractTrainCars(jobChainController.carsForJobChain);
            }
            catch (Exception ex)
            {
                Main.LogWarning(
                    $"BOOKLET COLOR CAR EXTRACTION FAILED | " +
                    $"Job={job.ID} | " +
                    $"{ex.Message}");

                return;
            }

            RegisterJob(
                job,
                trainCars);
        }

        // =====================================================
        // REGISTER EXISTING JOB
        // =====================================================
        public static void RegisterExistingJob(Job job)
        {
            if (job == null)
                return;

            string jobId = job.ID != null
                    ? job.ID.ToString()
                    : string.Empty;

            if (string.IsNullOrEmpty(jobId))
                return;

            if (specialJobColors.ContainsKey(jobId))
                return;

            if (StationController.allStations == null)
                return;

            foreach (StationController station in StationController.allStations)
            {
                if (station == null)
                    continue;

                StationProceduralJobsController jobsController = station.ProceduralJobsController;

                if (jobsController == null)
                    continue;

                var jobChains = jobsController.GetCurrentJobChains();

                if (jobChains == null)
                    continue;

                foreach (JobChainController jobChain in jobChains)
                {
                    if (jobChain == null)
                        continue;

                    if (jobChain.currentJobInChain != job)
                        continue;

                    RegisterFromJobChain(jobChain);

                    return;
                }
            }
        }

        // =====================================================
        // REGISTER JOB TYPE
        // =====================================================
        private static void RegisterJob(Job job,IReadOnlyList<TrainCar> trainCars)
        {
            if (job == null || trainCars == null || trainCars.Count == 0)
            {
                return;
            }

            string jobId = job.ID != null
                    ? job.ID.ToString()
                    : string.Empty;

            if (string.IsNullOrEmpty(jobId))
                return;

            // =================================================
            // FREIGHT / TRANSPORT
            // =================================================
            if (job.jobType == JobType.Transport)
            {
                if (ContainsCarBelowCondition(trainCars,DamagedFreightCondition))
                {
                    specialJobColors[jobId] = SpecialJobBookletColorKind.DamagedFreight;
                    RememberJobIdOverride(jobId,SpecialJobBookletColorKind.DamagedFreight);

                    Main.Log(
                        $"BOOKLET SERVICE -> SPECIAL HAUL | " +
                        $"Job={jobId} | " +
                        $"Type=Transport | " +
                        $"Cars={GetCarConditionLog(trainCars)}");

                    return;
                }
                specialJobColors.Remove(jobId);

                return;
            }

            // =================================================
            // EMPTY HAUL
            // =================================================
            if (job.jobType == JobType.EmptyHaul)
            {
                if (ContainsCarAtOrBelowCondition(trainCars,RepairEmptyHaulCondition))
                {
                    specialJobColors[jobId] = SpecialJobBookletColorKind.RepairEmptyHaul;
                    RememberJobIdOverride(jobId,SpecialJobBookletColorKind.RepairEmptyHaul);

                    Main.Log(
                        $"BOOKLET SERVICE -> MAINTENANCE HAUL | " +
                        $"Job={jobId} | " +
                        $"Type=EmptyHaul | " +
                        $"Cars={GetCarConditionLog(trainCars)}");

                    return;
                }
                specialJobColors.Remove(jobId);

                return;
            }

            // =================================================
            // EVERYTHING ELSE
            // =================================================
            specialJobColors.Remove(jobId);
        }

        // =====================================================
        // APPLY SPECIAL JOB TEXT / ID TO TEMPLATE DATA
        // =====================================================
        public static void ApplyServiceAppearance(Job_data jobData,List<TemplatePaperData> templateData)
        {
            if (jobData == null || templateData == null || templateData.Count == 0)
            {
                return;
            }

            string jobId =jobData.ID != null
                    ? jobData.ID.ToString()
                    : string.Empty;

            if (string.IsNullOrEmpty(jobId))
                return;

            SpecialJobBookletColorKind colorKind;

            if (!specialJobColors.TryGetValue(jobId,out colorKind))
            {
                colorKind = GetSpecialKindFromJobId(jobId);

                if (colorKind == SpecialJobBookletColorKind.None)
                    return;

                specialJobColors[jobId] = colorKind;
            }

            string localizedJobType;

            switch (colorKind)
            {
                case SpecialJobBookletColorKind.DamagedFreight:
				
                    localizedJobType = LocalizationAPI.L(TrainServiceReworkTranslations.DamagedFreightJobTypeKey);
                    break;

                case SpecialJobBookletColorKind.RepairEmptyHaul:
				
                    localizedJobType = LocalizationAPI.L(TrainServiceReworkTranslations.RepairEmptyHaulJobTypeKey);
                    break;

                default:
                    return;
            }
			
            string displayJobId = GetDisplayJobId(jobId,colorKind);

            // =================================================
            // APPLY FRONT / COVER PAGE APPEARANCE
            // =================================================
            for (int i = 0;i < templateData.Count;i++)
            {
                TemplatePaperData template = templateData[i];

                // =============================================
                // FRONT PAGE
                // =============================================
                if (template is FrontPageTemplatePaperData frontPage)
                {
                    bool nameApplied = TrySetJobType(frontPage,localizedJobType);
                    bool idApplied = TrySetFrontPageJobId(frontPage,displayJobId);

                    if (nameApplied || idApplied)
                    {
                        Main.Log(
                            $"BOOKLET SERVICE APPLIED | " +
                            $"InternalJob={jobId} | " +
                            $"DisplayJob={displayJobId} | " +
                            $"Kind={colorKind} | " +
                            $"Name={localizedJobType}");
                    }

                    continue;
                }

                // =============================================
                // COVER PAGE
                // =============================================
                if (template is CoverPageTemplatePaperData coverPage)
                {
                    TrySetCoverPageJobId(coverPage,displayJobId);
                }
            }
        }

        // =====================================================
        // DISPLAY JOB ID
        // =====================================================
        private static string GetDisplayJobId(string originalJobId,SpecialJobBookletColorKind colorKind)
        {
            if (string.IsNullOrEmpty(originalJobId))
                return originalJobId;

            string expectedJobCode;
            string replacementJobCode;

            switch (colorKind)
            {
                case SpecialJobBookletColorKind.DamagedFreight:
                    expectedJobCode = "FH";
                    replacementJobCode = "SH";
                    break;

                case SpecialJobBookletColorKind.RepairEmptyHaul:
                    expectedJobCode = "LH";
                    replacementJobCode = "MH";
                    break;

                default:
                    return originalJobId;
            }

            string[] parts = originalJobId.Split('-');

            if (parts.Length != 3)
            {
                Main.LogWarning(
                    $"BOOKLET JOB ID FORMAT UNEXPECTED | " +
                    $"Job={originalJobId}");

                return originalJobId;
            }
			
            if (string.Equals(parts[1],replacementJobCode,StringComparison.OrdinalIgnoreCase))
            {
                return originalJobId;
            }

            if (!string.Equals(parts[1],expectedJobCode,StringComparison.OrdinalIgnoreCase))
            {
                Main.LogWarning(
                    $"BOOKLET JOB ID CODE UNEXPECTED | " +
                    $"Job={originalJobId} | " +
                    $"Expected={expectedJobCode} or {replacementJobCode}");

                return originalJobId;
            }

            return
                $"{parts[0]}-" +
                $"{replacementJobCode}-" +
                $"{parts[2]}";
        }

        // =====================================================
        // SET FRONT PAGE JOB ID
        // =====================================================
        private static bool TrySetFrontPageJobId(FrontPageTemplatePaperData frontPage,string displayJobId)
        {
            if (frontPage == null || string.IsNullOrEmpty(displayJobId))
            {
                return false;
            }

            if (frontPageJobIdField != null)
            {
                frontPageJobIdField.SetValue(frontPage,displayJobId);
                return true;
            }

            if (frontPageJobIdProperty != null && frontPageJobIdProperty.CanWrite)
            {
                frontPageJobIdProperty.SetValue(frontPage,displayJobId,null);
                return true;
            }

            if (!missingFrontPageJobIdMemberLogged)
            {
                missingFrontPageJobIdMemberLogged = true;

                Main.LogWarning(
                    "BOOKLET JOB ID FAILED -> " +
                    "FrontPageTemplatePaperData.jobId " +
                    "could not be found.");
            }

            return false;
        }

        // =====================================================
        // SET COVER PAGE JOB ID
        // =====================================================
        private static bool TrySetCoverPageJobId(CoverPageTemplatePaperData coverPage,string displayJobId)
        {
            if (coverPage == null || string.IsNullOrEmpty(displayJobId))
            {
                return false;
            }

            if (coverPageJobIdField != null)
            {
                coverPageJobIdField.SetValue(coverPage,displayJobId);
                return true;
            }

            if (coverPageJobIdProperty != null && coverPageJobIdProperty.CanWrite)
            {
                coverPageJobIdProperty.SetValue(coverPage,displayJobId,null);
                return true;
            }

            if (!missingCoverPageJobIdMemberLogged)
            {
                missingCoverPageJobIdMemberLogged = true;
				
                Main.LogWarning(
                    "BOOKLET JOB ID FAILED -> " +
                    "CoverPageTemplatePaperData.jobId " +
                    "could not be found.");
            }

            return false;
        }

        // =====================================================
        // SET JOB TYPE NAME
        // =====================================================
        private static bool TrySetJobType(FrontPageTemplatePaperData frontPage,string localizedJobType)
        {
            if (frontPage == null || string.IsNullOrEmpty(localizedJobType))
            {
                return false;
            }

            // =================================================
            // FIELD
            // =================================================
            if (jobTypeField != null)
            {
                jobTypeField.SetValue(frontPage,localizedJobType);
                return true;
            }

            // =================================================
            // PROPERTY
            // =================================================
            if (jobTypeProperty != null && jobTypeProperty.CanWrite)
            {
                jobTypeProperty.SetValue(frontPage,localizedJobType,null);
                return true;
            }

            // =================================================
            // FAILURE
            // =================================================
            if (!missingJobTypeMemberLogged)
            {
                missingJobTypeMemberLogged = true;

                Main.LogWarning(
                    "BOOKLET NAME FAILED -> " +
                    "FrontPageTemplatePaperData.jobType " +
                    "could not be found.");
            }

            return false;
        }

        // =====================================================
        // CONDITION < LIMIT
        // =====================================================
        private static bool ContainsCarBelowCondition(IReadOnlyList<TrainCar> trainCars,float conditionLimit)
        {
            for (int i = 0;
                 i < trainCars.Count;
                 i++)
            {
                TrainCar car = trainCars[i];

                if (car == null ||
                    car.CarDamage == null)
                {
                    continue;
                }

                float condition = Mathf.Clamp(car.CarDamage.EffectiveHealthPercentage100Notation,0f,100f);

                if (condition < conditionLimit)
                    return true;
            }

            return false;
        }

        // =====================================================
        // CONDITION <= LIMIT
        // =====================================================
        private static bool ContainsCarAtOrBelowCondition(IReadOnlyList<TrainCar> trainCars,float conditionLimit)
        {
            for (int i = 0;i < trainCars.Count;i++)
            {
                TrainCar car = trainCars[i];

                if (car == null || car.CarDamage == null)
                {
                    continue;
                }

                float condition = Mathf.Clamp(car.CarDamage.EffectiveHealthPercentage100Notation,0f,100f);

                if (condition <= conditionLimit)
                    return true;
            }

            return false;
        }

        // =====================================================
        // REAL JOB-ID GENERATION
        // =====================================================
        private static SpecialJobBookletColorKind GetGeneratedJobIdKind(JobType jobType)
        {
            IReadOnlyList<TrainCar>? trainCars = jobIdGenerationCars;

            if (trainCars == null || trainCars.Count == 0)
            {
                return SpecialJobBookletColorKind.None;
            }

            if (jobType == JobType.Transport && ContainsCarBelowCondition(trainCars,DamagedFreightCondition))
            {
                return SpecialJobBookletColorKind.DamagedFreight;
            }

            if (jobType == JobType.EmptyHaul && ContainsCarAtOrBelowCondition(trainCars,RepairEmptyHaulCondition))
            {
                return SpecialJobBookletColorKind.RepairEmptyHaul;
            }

            return SpecialJobBookletColorKind.None;
        }

        public static void ReplaceGeneratedJobId(IdGenerator idGenerator,JobType jobType,ref string generatedJobId)
        {
            if (idGenerator == null || string.IsNullOrEmpty(generatedJobId))
            {
                return;
            }

            SpecialJobBookletColorKind kind = GetGeneratedJobIdKind(jobType);

            if (kind == SpecialJobBookletColorKind.None)
                return;

            string expectedCode;
            string replacementCode;

            switch (kind)
            {
                case SpecialJobBookletColorKind.DamagedFreight:
                    expectedCode = "FH";
                    replacementCode = "SH";
                    break;

                case SpecialJobBookletColorKind.RepairEmptyHaul:
                    expectedCode = "LH";
                    replacementCode = "MH";
                    break;

                default:
                    return;
            }

            string[] parts = generatedJobId.Split('-');

            if (parts.Length != 3)
            {
                Main.LogWarning(
                    $"REAL JOB ID FORMAT UNEXPECTED | " +
                    $"Job={generatedJobId}");

                return;
            }

            if (!string.Equals(parts[1],expectedCode,StringComparison.OrdinalIgnoreCase))
            {
                Main.LogWarning(
                    $"REAL JOB ID CODE UNEXPECTED | " +
                    $"Job={generatedJobId} | " +
                    $"Expected={expectedCode}");

                return;
            }

            int originalNumber;

            if (!int.TryParse(parts[2],out originalNumber))
            {
                Main.LogWarning(
                    $"REAL JOB ID NUMBER INVALID | " +
                    $"Job={generatedJobId}");

                return;
            }

            if (idGeneratorExistingJobIdsField == null)
            {
                if (!missingIdGeneratorRegistryLogged)
                {
                    missingIdGeneratorRegistryLogged = true;

                    Main.LogWarning(
                        "REAL JOB ID FAILED -> " +
                        "IdGenerator.existingJobIds could not be found.");
                }

                return;
            }

            HashSet<string>? existingJobIds = idGeneratorExistingJobIdsField.GetValue(idGenerator)as HashSet<string>;

            if (existingJobIds == null)
            {
                if (!missingIdGeneratorRegistryLogged)
                {
                    missingIdGeneratorRegistryLogged = true;

                    Main.LogWarning(
                        "REAL JOB ID FAILED -> " +
                        "IdGenerator.existingJobIds could not be read.");
                }

                return;
            }

            string? replacementJobId = null;

            for (int offset = 0;offset < 100;offset++)
            {
                int number = (originalNumber + offset) % 100;

                string candidate =
                    $"{parts[0]}-" +
                    $"{replacementCode}-" +
                    $"{number:D2}";

                if (existingJobIds.Contains(candidate))
                    continue;

                replacementJobId = candidate;

                break;
            }

            if (replacementJobId == null)
            {
                Main.LogError(
                    $"REAL JOB ID FAILED -> " +
                    $"No free {replacementCode} ID at station " +
                    $"{parts[0]}.");

                return;
            }

            string originalJobId = generatedJobId;

            idGenerator.UnregisterJobId(originalJobId);
            idGenerator.RegisterJobId(replacementJobId);
            generatedJobId = replacementJobId;
            specialJobColors[replacementJobId] = kind;			
            jobIdOverrides[originalJobId] = replacementJobId;
            jobIdOverrides[replacementJobId] = replacementJobId;

            Main.Log(
                $"REAL JOB ID REPLACED | " +
                $"{originalJobId} -> {replacementJobId} | " +
                $"Type={jobType}");
        }


        // =====================================================
        // JOB-ID MAPPING
        // =====================================================
        private static void RememberJobIdOverride(string jobId,SpecialJobBookletColorKind colorKind)
        {
            if (string.IsNullOrEmpty(jobId))
                return;

            string displayJobId = GetDisplayJobId(jobId,colorKind);
            jobIdOverrides[jobId] = displayJobId;
            jobIdOverrides[displayJobId] = displayJobId;
        }


        private static bool TryResolveJobIdOverride(string jobId,out string displayJobId)
        {
            displayJobId = jobId;

            if (string.IsNullOrEmpty(jobId))
                return false;

            string? mappedJobId;

            if (jobIdOverrides.TryGetValue(jobId,out mappedJobId) && !string.IsNullOrEmpty(mappedJobId))
            {
                displayJobId = mappedJobId;
                return true;
            }

            SpecialJobBookletColorKind kind = GetSpecialKindFromJobId(jobId);
			
            if (kind != SpecialJobBookletColorKind.None)
            {
                displayJobId = jobId;
                jobIdOverrides[jobId] = jobId;
                return true;
            }

            if (!specialJobColors.TryGetValue(jobId,out kind))
            {
                return false;
            }

            displayJobId = GetDisplayJobId(jobId,kind);
            jobIdOverrides[jobId] = displayJobId;
            jobIdOverrides[displayJobId] = displayJobId;
            return true;
        }


        private static SpecialJobBookletColorKind GetSpecialKindFromJobId(string jobId)
        {
            if (string.IsNullOrEmpty(jobId))
                return SpecialJobBookletColorKind.None;

            string[] parts = jobId.Split('-');

            if (parts.Length != 3)
                return SpecialJobBookletColorKind.None;

            if (string.Equals(parts[1],"SH",StringComparison.OrdinalIgnoreCase))
            {
                return SpecialJobBookletColorKind.DamagedFreight;
            }

            if (string.Equals(parts[1],"MH",StringComparison.OrdinalIgnoreCase))
            {
                return SpecialJobBookletColorKind.RepairEmptyHaul;
            }

            return SpecialJobBookletColorKind.None;
        }

        // =====================================================
        // FRONT PAGE FINAL FIX
        // =====================================================
        public static void ApplyFinalFrontPageData(FrontPageTemplatePaperData frontPage)
        {
            if (frontPage == null)
                return;

            string currentJobId = GetFrontPageJobId(frontPage);

            if (string.IsNullOrEmpty(currentJobId))
                return;

            SpecialJobBookletColorKind kind = GetSpecialKindFromJobId(currentJobId);

            if (kind == SpecialJobBookletColorKind.None)
            {
                specialJobColors.TryGetValue(
                    currentJobId,
                    out kind);
            }

            if (kind == SpecialJobBookletColorKind.None)
                return;

            string localizedJobType;

            switch (kind)
            {
                case SpecialJobBookletColorKind.DamagedFreight:
                    localizedJobType = LocalizationAPI.L(TrainServiceReworkTranslations.DamagedFreightJobTypeKey);
                    break;

                case SpecialJobBookletColorKind.RepairEmptyHaul:
                    localizedJobType =LocalizationAPI.L(TrainServiceReworkTranslations.RepairEmptyHaulJobTypeKey);
                    break;

                default:
                    return;
            }
            string displayJobId = GetDisplayJobId(currentJobId,kind);

            TrySetJobType(frontPage,localizedJobType);
            TrySetFrontPageJobId(frontPage,displayJobId);
        }

        private static string GetFrontPageJobId(FrontPageTemplatePaperData frontPage)
        {
            if (frontPage == null)
                return string.Empty;

            if (frontPageJobIdField != null)
            {
                string? value = frontPageJobIdField.GetValue(frontPage)as string;
                return value ??
                       string.Empty;
            }

            if (frontPageJobIdProperty != null && frontPageJobIdProperty.CanRead)
            {
                string? value = frontPageJobIdProperty.GetValue(frontPage,null)as string;
                return value ??
                       string.Empty;
            }
            return string.Empty;
        }

        // =====================================================
        // DEBT BOOKLET JOB-ID FIX
        // =====================================================
        public static void ApplyDebtBookletJobId(Debt_data debt)
        {
            if (debt == null ||
                !debt.IsJobDebt)
            {
                return;
            }

            string currentJobId = GetDebtDataId(debt);

            if (string.IsNullOrEmpty(currentJobId))
                return;

            string displayJobId;

            if (!TryResolveJobIdOverride(currentJobId,out displayJobId))
            {
                return;
            }

            if (string.Equals(currentJobId,displayJobId,StringComparison.Ordinal))
            {
                return;
            }

            if (TrySetDebtDataId(debt,displayJobId))
            {
                Main.Log(
                    $"DEBT BOOKLET JOB ID APPLIED | " +
                    $"{currentJobId} -> {displayJobId}");
            }
        }


        private static string GetDebtDataId(Debt_data debt)
        {
            if (debt == null)
                return string.Empty;

            if (debtDataIdField != null)
            {
                string? value = debtDataIdField.GetValue(debt)as string;
                return value ??
                       string.Empty;
            }

            if (debtDataIdProperty != null &&
                debtDataIdProperty.CanRead)
            {
                string? value = debtDataIdProperty.GetValue(debt,null)as string;
                return value ??
                       string.Empty;
            }

            if (debtDataIdBackingField != null)
            {
                string? value = debtDataIdBackingField.GetValue(debt)as string;
                return value ??
                       string.Empty;
            }

            return string.Empty;
        }

        private static bool TrySetDebtDataId(Debt_data debt,string displayJobId)
        {
            if (debt == null || string.IsNullOrEmpty(displayJobId))
            {
                return false;
            }

            if (debtDataIdField != null)
            {
                debtDataIdField.SetValue(debt,displayJobId);
                return true;
            }

            if (debtDataIdProperty != null && debtDataIdProperty.CanWrite)
            {
                debtDataIdProperty.SetValue(debt,displayJobId,null);
                return true;
            }

            if (debtDataIdBackingField != null)
            {
                debtDataIdBackingField.SetValue(debt,displayJobId);
                return true;
            }

            if (!missingDebtDataIdMemberLogged)
            {
                missingDebtDataIdMemberLogged = true;
                Main.LogWarning(
                    "DEBT BOOKLET JOB ID FAILED -> " +
                    "Debt_data.ID could not be written.");
            }
            return false;
        }

        // =====================================================
        // DEBUG CONDITION STRING
        // =====================================================
        private static string GetCarConditionLog(IReadOnlyList<TrainCar> trainCars)
        {
            if (trainCars == null || trainCars.Count == 0)
            {
                return "-";
            }

            string result = string.Empty;

            for (int i = 0;i < trainCars.Count;i++)
            {
                TrainCar car = trainCars[i];

                if (car == null)
                    continue;

                float condition = 100f;

                if (car.CarDamage != null)
                {
                    condition = Mathf.Clamp(car.CarDamage.EffectiveHealthPercentage100Notation,0f,100f);
                }

                if (result.Length > 0)
                    result += ", ";
                result +=
                    $"{car.ID}={condition:F1}%";
            }
            return result;
        }
    }

    // =========================================================
    // REGISTER NEWLY GENERATED JOBS
    // =========================================================
    [HarmonyPatch(typeof(JobChainController),nameof(JobChainController.FinalizeSetupAndGenerateFirstJob))]
    public static class SpecialJobBookletColor_JobFinalizePatch
    {
        static void Prefix(JobChainController __instance)
        {
            SpecialJobBookletColorHelper.BeginJobIdGeneration(__instance);
        }
		
        static void Postfix(JobChainController __instance)
        {
            try
            {
                SpecialJobBookletColorHelper.RegisterFromJobChain(__instance);
            }
            finally
            {
                SpecialJobBookletColorHelper.EndJobIdGeneration();
            }
        }
    }
	
    [HarmonyPatch(typeof(IdGenerator),nameof(IdGenerator.GenerateJobID))]
    public static class SpecialJobRealIdGeneratorPatch
    {
        static void Postfix(IdGenerator __instance,JobType jobType,ref string __result)
        {
            SpecialJobBookletColorHelper.ReplaceGeneratedJobId(__instance,jobType,ref __result);
        }
    }

    // =========================================================
    // FRONT PAGE FINAL FIX
    // =========================================================
    [HarmonyPatch(typeof(FrontPageTemplatePaper),nameof(FrontPageTemplatePaper.FillInData))]
    public static class SpecialJobFrontPageFinalPatch
    {
        static void Prefix(FrontPageTemplatePaper __instance)
        {
            if (__instance == null || __instance.data == null)
            {
                return;
            }

            SpecialJobBookletColorHelper.ApplyFinalFrontPageData(__instance.data);
        }
    }

    // =========================================================
    // PREPARE NORMAL JOB BOOKLET
    // =========================================================
    [HarmonyPatch(typeof(BookletCreator_Job),nameof(BookletCreator_Job.Create),new Type[]{
		typeof(Job),
		typeof(Vector3),
		typeof(Quaternion),
		typeof(Transform),
		typeof(bool)})]
    public static class SpecialJobBookletColor_BookletCreatePatch
    {
        static void Prefix(Job job)
        {
            SpecialJobBookletColorHelper.RegisterExistingJob(job);
        }
    }

    // =========================================================
    // PREPARE JOB OVERVIEW
    // =========================================================
    [HarmonyPatch(typeof(BookletCreator_JobOverview),nameof(BookletCreator_JobOverview.Create),new Type[]{
		typeof(Job),
		typeof(Vector3),
		typeof(Quaternion),
		typeof(Transform)})]
    public static class SpecialJobBookletColor_OverviewCreatePatch
    {
        static void Prefix(Job job)
        {
            SpecialJobBookletColorHelper.RegisterExistingJob(job);
        }
    }

    // =========================================================
    // JOBBOOKLET ASSIGN FALLBACK
    // =========================================================
    [HarmonyPatch(typeof(JobBooklet),nameof(JobBooklet.AssignJob))]
    public static class SpecialJobBookletColor_AssignJobPatch
    {
        static void Prefix(Job jobToAssign)
        {
            SpecialJobBookletColorHelper.RegisterExistingJob(jobToAssign);
        }
    }

    // =========================================================
    // MODIFY NORMAL JOB BOOKLET TEMPLATE
    // =========================================================
    [HarmonyPatch(typeof(BookletCreator_Job),"GetBookletTemplateData")]
    public static class SpecialJobBookletColor_BookletTemplatePatch
    {
        static void Postfix(Job_data job,List<TemplatePaperData> __result)
        {
            SpecialJobBookletColorHelper.ApplyServiceAppearance(job,__result);
        }
    }

    // =========================================================
    // MODIFY JOB OVERVIEW TEMPLATE
    // =========================================================
    [HarmonyPatch(typeof(BookletCreator_JobOverview),nameof(BookletCreator_JobOverview.GetJobOverviewTemplateData))]
    public static class SpecialJobBookletColor_OverviewTemplatePatch
    {
        static void Postfix(Job_data job,List<TemplatePaperData> __result)
        {
            SpecialJobBookletColorHelper.ApplyServiceAppearance(job,__result);
        }
    }	
	
	// =========================================================
    // DEBT BOOKLET / FEES REPORT
    // =========================================================
    [HarmonyPatch(typeof(BookletCreator_Debt),nameof(BookletCreator_Debt.Create),new Type[]{
		typeof(Debt_data),
		typeof(Vector3),
		typeof(Quaternion),
		typeof(Transform)})]
    public static class SpecialJobDebtBookletCreatePatch
    {
        static void Prefix(Debt_data debt)
        {
            SpecialJobBookletColorHelper.ApplyDebtBookletJobId(debt);
        }
    }

    [HarmonyPatch(typeof(BookletCreator_Debt),nameof(BookletCreator_Debt.GetDebtBookletTemplateData),new Type[]{
		typeof(Debt_data),
		typeof(int),
		typeof(int)})]
    public static class SpecialJobDebtBookletTemplatePatch
    {
        static void Prefix(Debt_data debt)
        {
            SpecialJobBookletColorHelper.ApplyDebtBookletJobId(debt);
        }
    }
}