// File: TrainServiceReworkRepairFees.cs
//
// Body repair of cars (pit stop) and the damage fees (host / single player):
//
//   * A damaged car is only repaired (body, Car_DMG) when it belongs to an ACCEPTED maintenance job (SM).
//     Locomotives, tenders, cabooses, owned (unique) cars and every other resource are not affected.
//   * Fee already paid for the damage -> the repair of these units is free (price per unit 0, shared over the units).
//   * Fee still open -> after the repair it is reduced by the repaired units (body damage only, never the cargo):
//       - fee that is still tracked on the car (live)  : the game reduces it itself when the health rises
//       - fee of a finished job (staged)               : reduced here (end value raised), removed when nothing is left
//   * SM payment: shunting effort + what the pit stop charges for the repair (+ time bonus). Fee already paid ->
//     repair free -> only the shunting effort (also lowered later, when the fee is paid while the job exists).
//
// Multiplayer with individual careers (DVMultiplayerIP): who owns a fee and money between players is NOT done here.
// TSR reports every repair (BodyRepaired) and asks an optional hook for the free units (FreeUnitsOverride),
// so the IP mod can make the rules stricter (e.g. only the player who took the job, transfer between players).

using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DV;
using DV.Localization;
using DV.Logic.Job;
using DV.ServicePenalty;
using DV.ThingTypes;

namespace TrainServiceRework
{
    public static class RepairFees
    {
        private const string SaveKey = "TrainServiceRework_RepairFees";

        /// <summary>Car ID -> body damage units whose fee was already paid (not repaired yet).</summary>
        private static readonly Dictionary<string, float> paidUnits = new Dictionary<string, float>(StringComparer.Ordinal);

        // ---------------- API for other mods (DVMultiplayerIP) ----------------

        /// <summary>
        /// After every body repair at a pit stop: car, repaired units (%), units that were free (fee paid before),
        /// units by which open fees of finished jobs were reduced.
        /// </summary>
        public static event Action<TrainCar, float, float, float>? BodyRepaired;

        /// <summary>Optional: returns the free units of a car instead of TSR's own value (null = use TSR's value).</summary>
        public static Func<TrainCar, float?>? FreeUnitsOverride;

        /// <summary>Optional: true = this car may be repaired (in addition to TSR's check of an accepted SM job).</summary>
        public static Func<TrainCar, bool>? RepairAllowedOverride;

        // ---------------- which cars ----------------

        /// <summary>Regular car (no loco / tender / caboose, not owned): the rules of this file apply.</summary>
        public static bool IsManagedCar(TrainCar? car)
        {
            if (car == null || car.CarDamage == null || car.logicCar == null || car.uniqueCar)
                return false;

            if (car.IsLoco || CarRepairHelper.IsLocomotiveOrTender(car))
                return false;

            try
            {
                if (car.IsCaboose)
                    return false;
            }
            catch
            {
                // no caboose info
            }

            return car.carLivery != null && CarTypes.IsRegularCar(car.carLivery);
        }

        public static float MissingUnits(TrainCar car) => Mathf.Max(0f, 100f - DamagedJobSystem.Condition(car));

        /// <summary>The car belongs to an accepted (taken) maintenance job.</summary>
        public static bool InAcceptedMaintenanceJob(TrainCar car)
        {
            try
            {
                Job? job = JobsManager.Instance != null ? JobsManager.Instance.GetJobOfCar(car.logicCar, true) : null;
                return job != null && job.State == JobState.InProgress && DamagedJobSystem.IsOverhaulJob(job);
            }
            catch
            {
                return false;
            }
        }

        private static TrainCar? cachedCar;
        private static int cachedFrame = -1;
        private static bool cachedAllowed;

        /// <summary>RepairAllowed, at most once per frame and car (the pit stop asks very often).</summary>
        public static bool RepairAllowedCached(TrainCar car)
        {
            int frame = Time.frameCount;

            if (cachedCar == car && cachedFrame == frame)
                return cachedAllowed;

            cachedCar = car;
            cachedFrame = frame;
            cachedAllowed = RepairAllowed(car);
            return cachedAllowed;
        }

        public static bool RepairAllowed(TrainCar car)
        {
            if (!IsManagedCar(car) || MissingUnits(car) <= 0.01f)
                return true;

            if (!InAcceptedMaintenanceJob(car))
                return false;

            try
            {
                return RepairAllowedOverride == null || RepairAllowedOverride(car);
            }
            catch
            {
                return true;
            }
        }

        // ---------------- fees of a car ----------------

        private static DebtComponent? LiveBodyComponent(TrainCar car)
        {
            try
            {
                CarDebtController? controller = car.GetComponent<CarDebtController>() ?? car.GetComponentInChildren<CarDebtController>(true);
                DebtComponent[]? components = controller?.CarDebtTracker?.GetDebtData()?.GetTrackedDebts();
                return components?.FirstOrDefault(c => c != null && c.Type == ResourceType.Car_DMG);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Open fee that is still tracked on the car (start value minus the current condition).</summary>
        public static float LiveOpenUnits(TrainCar car)
        {
            DebtComponent? component = LiveBodyComponent(car);
            return component != null ? Mathf.Max(0f, component.StartValue - DamagedJobSystem.Condition(car)) : 0f;
        }

        /// <summary>Body components of the car in fees of finished jobs (not paid yet).</summary>
        private static List<(StagedJobDebt debt, DebtComponent component)> StagedBodyComponents(TrainCar car)
        {
            List<(StagedJobDebt, DebtComponent)> result = new List<(StagedJobDebt, DebtComponent)>();

            try
            {
                JobDebtController? controller = JobDebtController.Instance;

                if (controller?.stagedJobsDebts == null || string.IsNullOrEmpty(car.ID))
                    return result;

                foreach (StagedJobDebt debt in controller.stagedJobsDebts)
                {
                    CarDebtData[]? cars = debt?.jobDebtData?.GetCarsDebts();

                    if (debt == null || cars == null)
                        continue;

                    foreach (CarDebtData data in cars)
                    {
                        if (data == null || !string.Equals(data.id, car.ID, StringComparison.Ordinal))
                            continue;

                        foreach (DebtComponent component in data.GetTrackedDebts() ?? new DebtComponent[0])
                        {
                            if (component != null && component.Type == ResourceType.Car_DMG && component.StartToEndDiff > 0f)
                                result.Add((debt, component));
                        }
                    }
                }
            }
            catch
            {
                // no staged fees
            }

            return result;
        }

        public static float StagedOpenUnits(TrainCar car) => StagedBodyComponents(car).Sum(e => e.component.StartToEndDiff);

        public static float PaidUnits(TrainCar car)
        {
            try
            {
                float? external = FreeUnitsOverride?.Invoke(car);

                if (external.HasValue)
                    return Mathf.Max(0f, external.Value);
            }
            catch
            {
                // own value
            }

            return car?.ID != null && paidUnits.TryGetValue(car.ID, out float units) ? units : 0f;
        }

        /// <summary>Units of the damage that a fee covers (open or already paid), at most the missing units.</summary>
        public static float CoveredUnits(TrainCar car) =>
            Mathf.Min(MissingUnits(car), LiveOpenUnits(car) + StagedOpenUnits(car) + PaidUnits(car));

        /// <summary>Units the pit stop repairs for free (fee paid before).</summary>
        public static float FreeUnits(TrainCar car)
        {
            float cap = Mathf.Max(0f, MissingUnits(car) - LiveOpenUnits(car));
            float paid = PaidUnits(car);

            // the game rounds fees to 0.1 % – a paid fee "99.9 of 99.94" counts as the whole damage
            return paid >= cap - RoundingTolerance ? cap : Mathf.Clamp(paid, 0f, cap);
        }

        private const float RoundingTolerance = 0.1f;

        /// <summary>Factor on the price per unit of the body repair (free units shared over all missing units).</summary>
        public static float PriceFactor(TrainCar car)
        {
            float missing = MissingUnits(car);

            if (missing <= 0.01f)
                return 1f;

            return Mathf.Clamp01((missing - FreeUnits(car)) / missing);
        }

        // ---------------- fee paid at the career manager ----------------

        public static void OnDebtPaid(DisplayableDebt debt)
        {
            if (debt == null || ImmersiveServiceMultiplayer.IsClientOnly)
                return;

            if (!(debt is StagedJobDebt) && !(debt is StagedOtherDebt) && !(debt is ExistingJobDebt) && !(debt is ExistingOtherDebt))
                return;

            try
            {
                // live fees: bring the end values up to date first
                if (debt is ExistingJobDebt existingJob)
                    existingJob.jobDebtTracker?.UpdateDebtValues();

                if (debt is ExistingOtherDebt existingOther && existingOther.joblessCarsTrackers != null)
                {
                    foreach (DebtTrackerCar tracker in existingOther.joblessCarsTrackers)
                        tracker?.UpdateDebtValues();
                }

                foreach (CarDebtData data in debt.GetCarDebts() ?? new CarDebtData[0])
                {
                    if (data == null || string.IsNullOrEmpty(data.id))
                        continue;

                    float units = (data.GetTrackedDebts() ?? new DebtComponent[0])
                        .Where(c => c != null && c.Type == ResourceType.Car_DMG)
                        .Sum(c => Mathf.Max(0f, c.StartToEndDiff));

                    if (units <= 0.01f)
                        continue;

                    float paidBefore = paidUnits.TryGetValue(data.id, out float before) ? before : 0f;
                    paidUnits[data.id] = paidBefore + units;
                    refreshCars.Add(data.id);
                    Main.Log($"REPAIR FEES -> {data.id}: fee for {units:0.0} % body damage paid (repair of these units is free)");

                    // a maintenance job of this car pays the repair – that part is free now, so the payment goes down
                    TrainCar? car = FindCar(data.id);
                    float missing = car != null ? MissingUnits(car) : 0f;

                    if (car == null || missing <= 0.01f || !IsManagedCar(car))
                        continue;

                    float newlyFree = Mathf.Min(missing, paidBefore + units) - Mathf.Min(missing, paidBefore);
                    Job? job = JobsManager.Instance != null ? JobsManager.Instance.GetJobOfCar(car.logicCar, false) : null;

                    if (newlyFree > 0f && job != null && DamagedJobSystem.IsOverhaulJob(job))
                    {
                        float value = DamagedJobSystem.EstimateRepairCost(car) / missing * newlyFree;
                        wageReductions[job] = (wageReductions.TryGetValue(job, out float sum) ? sum : 0f) + value;
                    }
                }

                foreach (KeyValuePair<Job, float> reduction in wageReductions)
                    DamagedJobSystem.ReduceMaintenanceWage(reduction.Key, reduction.Value);

                wageReductions.Clear();
            }
            catch (Exception ex)
            {
                Main.LogWarning("REPAIR FEES -> recording the paid fee failed: " + ex.Message);
            }

            // offers (job overviews) of maintenance jobs with these cars: print them again (repair costs changed)
            if (refreshCars.Count > 0 && !refreshQueued)
            {
                refreshQueued = true;
                DamagedJobSystem.Enqueue(RefreshOverviews);
            }
        }

        private static readonly HashSet<string> refreshCars = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<Job, float> wageReductions = new Dictionary<Job, float>();

        private static TrainCar? FindCar(string id)
        {
            if (CarSpawner.Instance == null)
                return null;

            foreach (TrainCar car in CarSpawner.Instance.AllCars)
            {
                if (car != null && string.Equals(car.ID, id, StringComparison.Ordinal))
                    return car;
            }

            return null;
        }
        private static bool refreshQueued;
        private static readonly System.Reflection.FieldInfo? spawnedOverviewsField =
            AccessTools.Field(typeof(StationController), "spawnedJobOverviews");

        /// <summary>Offers lying in the stations are printed once – after a fee was paid they are replaced by new ones.</summary>
        private static void RefreshOverviews()
        {
            refreshQueued = false;
            HashSet<string> ids = new HashSet<string>(refreshCars, StringComparer.Ordinal);
            refreshCars.Clear();

            Transform? player = null;

            try
            {
                player = PlayerManager.PlayerTransform;
            }
            catch
            {
                player = null;
            }

            foreach (JobOverview overview in DamagedJobTrash.Overviews.ToList())
            {
                try
                {
                    Job? job = overview != null ? overview.job : null;

                    if (overview == null || job == null || job.State != JobState.Available || !DamagedJobSystem.IsOverhaulJob(job))
                        continue;

                    if (!DamagedJobSystem.JobCarIds(job).Any(ids.Contains))
                        continue;

                    // the player holds it (hand / inventory): leave it
                    Transform transform = overview.transform;

                    if (player != null && transform.IsChildOf(player))
                        continue;

                    JobOverview fresh = DV.Booklets.BookletCreator.CreateJobOverview(job, transform.position, transform.rotation, transform.parent);
                    ReplaceInStation(overview, fresh);
                    DamagedJobTrash.Overviews.Remove(overview);
                    overview.DestroyJobOverview();

                    Main.Log($"REPAIR FEES -> offer {job.ID} printed again (fee paid, new repair costs)");
                }
                catch (Exception ex)
                {
                    Main.LogWarning("REPAIR FEES -> renewing an offer failed: " + ex.Message);
                }
            }
        }

        /// <summary>The station keeps a list of its offers (removed when the player leaves) – the new one takes the old place.</summary>
        private static void ReplaceInStation(JobOverview old, JobOverview fresh)
        {
            if (spawnedOverviewsField == null)
                return;

            foreach (StationController station in StationController.allStations)
            {
                if (station == null || !(spawnedOverviewsField.GetValue(station) is List<JobOverview> list))
                    continue;

                int index = list.IndexOf(old);

                if (index >= 0)
                {
                    list[index] = fresh;
                    return;
                }
            }
        }

        // ---------------- repair done (pit stop) ----------------

        public static void OnBodyRepaired(TrainCar car, float before, float after)
        {
            if (!IsManagedCar(car) || ImmersiveServiceMultiplayer.IsClientOnly)
                return;

            float repaired = after - before;

            if (repaired <= 0.0001f)
                return;

            float remaining = repaired;

            // 1) open fee tracked on the car: the game reduces it itself (end value follows the condition)
            DebtComponent? live = LiveBodyComponent(car);
            float liveOpen = live != null ? Mathf.Max(0f, live.StartValue - before) : 0f;
            remaining -= Mathf.Min(liveOpen, remaining);

            // 2) fee paid before: these units were free
            float free = 0f;

            if (remaining > 0f && car.ID != null && paidUnits.TryGetValue(car.ID, out float paid) && paid > 0f)
            {
                free = paid >= remaining - RoundingTolerance ? remaining : Mathf.Min(paid, remaining);
                remaining -= free;

                if (paid - free <= RoundingTolerance)
                    paidUnits.Remove(car.ID);
                else
                    paidUnits[car.ID] = paid - free;
            }

            // 3) open fees of finished jobs: reduced by the repaired units
            float stagedReduced = 0f;

            if (remaining > 0f)
            {
                foreach ((StagedJobDebt debt, DebtComponent component) in StagedBodyComponents(car))
                {
                    if (remaining <= 0f)
                        break;

                    float reduce = Mathf.Min(component.StartToEndDiff, remaining);
                    component.UpdateEndValue(component.EndValue + reduce);
                    remaining -= reduce;
                    stagedReduced += reduce;

                    RemoveIfSettled(debt);
                }
            }

            // a repaired car starts a new fee from its new condition (otherwise later damage up to the old
            // start value would be free)
            if (live != null && live.StartValue < after)
            {
                live.UpdateStartValue(after);
                live.UpdateEndValue(after);
            }

            if (free > 0f || stagedReduced > 0f)
                Main.Log($"REPAIR FEES -> {car.ID}: {repaired:0.0} % repaired, {free:0.0} % free (fee paid), open fee reduced by {stagedReduced:0.0} %");

            try
            {
                BodyRepaired?.Invoke(car, repaired, free, stagedReduced);
            }
            catch (Exception ex)
            {
                Main.LogWarning("REPAIR FEES -> BodyRepaired listener failed: " + ex.Message);
            }
        }

        private static void RemoveIfSettled(StagedJobDebt debt)
        {
            try
            {
                if (debt.GetTotalPrice() <= 0.01f && JobDebtController.Instance != null &&
                    JobDebtController.Instance.stagedJobsDebts.Contains(debt))
                {
                    JobDebtController.Instance.PayStagedJobDebt(debt);
                    Main.Log($"REPAIR FEES -> fee {debt.ID} settled by the repair");
                }
            }
            catch
            {
                // stays in the list with $0
            }
        }

        // ---------------- save / load ----------------

        public static void Write(SaveGameData data)
        {
            try
            {
                JObject units = new JObject();

                foreach (KeyValuePair<string, float> entry in paidUnits)
                {
                    if (entry.Value > 0.01f)
                        units[entry.Key] = entry.Value;
                }

                data.SetJObject(SaveKey, new JObject
                {
                    ["version"] = 1,
                    ["paid"] = units,
                    // cars chosen as damaged cars at the first (soft) threshold
                    ["soft"] = new JArray(DamagedJobSystem.SoftDamagedIds.ToArray())
                });
            }
            catch (Exception ex)
            {
                Main.LogWarning("REPAIR FEES -> saving failed: " + ex.Message);
            }
        }

        public static void Load()
        {
            paidUnits.Clear();
            DamagedJobSystem.SetSoftDamagedIds(new string[0]);

            try
            {
                SaveGameManager manager = SaveGameManager.Instance;
                JObject? root = manager != null && manager.data != null ? manager.data.GetJObject(SaveKey) : null;

                if (root?["soft"] is JArray soft)
                    DamagedJobSystem.SetSoftDamagedIds(soft.Select(t => t.ToString()));

                if (!(root?["paid"] is JObject units))
                    return;

                foreach (JProperty property in units.Properties())
                {
                    float value = property.Value.Type == JTokenType.Float || property.Value.Type == JTokenType.Integer
                        ? property.Value.ToObject<float>()
                        : 0f;

                    if (value > 0.01f)
                        paidUnits[property.Name] = value;
                }

                Main.Log($"REPAIR FEES -> {paidUnits.Count} car(s) with paid fees found in the savegame");
            }
            catch (Exception ex)
            {
                Main.LogWarning("REPAIR FEES -> reading the savegame failed: " + ex.Message);
            }
        }

        /// <summary>Price per unit of the selected car kept up to date (fee paid while the car stands in the pit stop).</summary>
        public static void SyncPrice(LocoResourceModule module, TrainCar car)
        {
            if (!IsManagedCar(car))
                return;

            float missing = MissingUnits(car);
            CashRegisterModule.CashRegisterModuleData? data = module.Data;

            if (missing <= 0.01f || data == null || data.car != car)
                return;

            float unitPrice = DamagedJobSystem.EstimateRepairCost(car) / missing * PriceFactor(car);

            if (Mathf.Abs(unitPrice - data.pricePerUnit) > 0.005f)
                data.pricePerUnit = unitPrice;
        }

        // ---------------- pit stop: only with an accepted SM job ----------------

        /// <summary>Before paying: body repair orders of cars without an accepted SM job are cancelled.</summary>
        public static void CheckRepairOrders(DV.CashRegister.CashRegisterWithModules register)
        {
            if (register?.registerModules == null || ImmersiveServiceMultiplayer.IsClientOnly || !DamagedJobSystem.Enabled)
                return;

            foreach (CashRegisterModule baseModule in register.registerModules)
            {
                if (!(baseModule is LocoResourceModule module) || module.resourceType != ResourceType.Car_DMG)
                    continue;

                List<CashRegisterModule.CashRegisterModuleData>? data = ImmersiveServiceSystem.ResourceData(module);

                if (data == null)
                    continue;

                bool cancelled = false;

                foreach (CashRegisterModule.CashRegisterModuleData entry in data)
                {
                    if (entry == null || entry.unitsToBuy <= 0.0001f || entry.car == null)
                        continue;

                    if (RepairAllowed(entry.car))
                    {
                        // price up to date at the moment of paying (a fee may have been paid while the car stood here)
                        float missing = MissingUnits(entry.car);

                        if (IsManagedCar(entry.car) && missing > 0.01f)
                        {
                            float unitPrice = DamagedJobSystem.EstimateRepairCost(entry.car) / missing * PriceFactor(entry.car);

                            if (Mathf.Abs(unitPrice - entry.pricePerUnit) > 0.005f)
                            {
                                entry.pricePerUnit = unitPrice;
                                cancelled = true;   // refresh the display
                            }
                        }

                        continue;
                    }

                    Main.Log($"REPAIR FEES -> {entry.car.ID}: body repair cancelled – no accepted maintenance job (SM)");
                    entry.unitsToBuy = 0f;
                    cancelled = true;
                }

                if (!cancelled)
                    continue;

                ImmersiveServiceSystem.RefreshRegister(register);

                if (!data.Any(d => d != null && d.car != null && !RepairAllowed(d.car)))
                    continue;

                try
                {
                    if (module.statusText != null)
                        module.statusText.text = LocalizationAPI.L(TrainServiceReworkTranslations.RepairNeedsJobKey);
                }
                catch
                {
                    // text only
                }
            }
        }
    }

    // =========================================================
    // PATCHES
    // =========================================================

    /// <summary>Every fee payment goes through DisplayableDebt.Pay (the subclasses call it first).</summary>
    [HarmonyPatch(typeof(DisplayableDebt), nameof(DisplayableDebt.Pay))]
    public static class RepairFeesDebtPaidPatch
    {
        static void Prefix(DisplayableDebt __instance) => RepairFees.OnDebtPaid(__instance);
    }

    /// <summary>Body repair price: units whose fee is paid are free (after the price fallback of TSR).</summary>
    [HarmonyPatch(typeof(LocoResourceModule), nameof(LocoResourceModule.UpdateResourcePricePerUnit))]
    public static class RepairFeesPricePatch
    {
        [HarmonyPriority(Priority.Low)]
        static void Prefix(LocoResourceModule __instance, TrainCar trainCar, ref float newPricePerUnit)
        {
            try
            {
                if (__instance == null || __instance.resourceType != ResourceType.Car_DMG || !DamagedJobSystem.Enabled ||
                    !RepairFees.IsManagedCar(trainCar))
                {
                    return;
                }

                newPricePerUnit *= RepairFees.PriceFactor(trainCar);
            }
            catch
            {
                // vanilla price
            }
        }
    }

    /// <summary>Paying at the pit stop: body repair only with an accepted SM job (before the other checks).</summary>
    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterWithModules), nameof(DV.CashRegister.CashRegisterWithModules.Buy))]
    public static class RepairFeesBuyPatch
    {
        [HarmonyPriority(Priority.High)]
        static void Prefix(DV.CashRegister.CashRegisterWithModules __instance)
        {
            try
            {
                RepairFees.CheckRepairOrders(__instance);
            }
            catch (Exception ex)
            {
                Main.LogWarning("REPAIR FEES -> checking the repair order failed: " + ex.Message);
            }
        }
    }

    /// <summary>Body module: a damaged car without an accepted SM job shows a red "NO JOB" instead of "READY".</summary>
    [HarmonyPatch(typeof(LocoResourceModule), "UpdateStatusText")]
    public static class RepairFeesStatusTextPatch
    {
        static void Postfix(LocoResourceModule __instance)
        {
            try
            {
                if (__instance == null || __instance.resourceType != ResourceType.Car_DMG || __instance.statusText == null ||
                    !DamagedJobSystem.Enabled || !__instance.IsTrainPresent)
                {
                    return;
                }

                TrainCar? car = ImmersiveServiceSystem.SelectedCar(__instance);

                if (car == null)
                    return;

                if (RepairFees.RepairAllowedCached(car))
                {
                    // price kept up to date (fee paid while the car stands in the pit stop)
                    RepairFees.SyncPrice(__instance, car);
                    return;
                }

                __instance.statusText.color = __instance.colHardError;
                __instance.statusText.text = LocalizationAPI.L(TrainServiceReworkTranslations.RepairNoJobKey);
            }
            catch
            {
                // vanilla text
            }
        }
    }

    /// <summary>
    /// "NO JOB" works like "INCOMPATIBLE": the lever can be pulled, but nothing can be ordered (buy limit 0).
    /// </summary>
    [HarmonyPatch(typeof(LocoResourceModule), nameof(LocoResourceModule.BuyMaxLimit), MethodType.Getter)]
    public static class RepairFeesBuyLimitPatch
    {
        static void Postfix(LocoResourceModule __instance, ref float __result)
        {
            try
            {
                if (__result <= 0f || __instance == null || __instance.resourceType != ResourceType.Car_DMG || !DamagedJobSystem.Enabled)
                    return;

                TrainCar? car = ImmersiveServiceSystem.SelectedCar(__instance);

                if (car != null && !RepairFees.RepairAllowedCached(car))
                    __result = 0f;
            }
            catch
            {
                // vanilla limit
            }
        }
    }

    [HarmonyPatch(typeof(SaveGameManager), "UpdateInternalData")]
    public static class RepairFeesWriteSavePatch
    {
        static void Postfix(SaveGameManager __instance)
        {
            if (__instance != null && __instance.data != null && !ImmersiveServiceMultiplayer.IsClientOnly)
                RepairFees.Write(__instance.data);
        }
    }

    [HarmonyPatch(typeof(JobSaveManager), nameof(JobSaveManager.LoadJobSaveGameData))]
    public static class RepairFeesLoadPatch
    {
        static void Prefix() => RepairFees.Load();
    }
}
