// File: TrainServiceReworkDamagedJobs.cs
//
// Damaged car jobs (host / single player only).
//
// WHEN something happens
//   * A car takes damage (CarDamageModel.DamageCar) -> ~10 s later its condition is checked ONCE. Only if the car is
//     below the limit AND belongs to a not yet accepted FH, LH, SL, SU or CH job, that job is deleted right away and
//     replaced (no polling of all cars – only cars that were just damaged are looked at).
//   * Jobless damaged cars only get a new job when the station generates jobs anyway: with PersistentJobsRework at
//     every full game hour (its refill asks TSR for groups with damaged cars), without it every 15 minutes.
//     A thrown away job is therefore NOT handed out again right away.
//
// WHAT is created
//   healthy + damaged together                     -> AUSRANGIEREN / BAD ORDER SET-OUT (SO, shunting job): all cars of
//                                                      the old job; damaged cars to their own track (O if they leave
//                                                      the station afterwards, S if they stay), the rest back to the
//                                                      original track
//   all loaded, station accepts the cargo           -> SU: unload, damaged cars to their own track
//   damaged + loaded, cargo not accepted here       -> SCHADZUG (DH): FH +50 % time, +25 % pay to a workshop station
//                                                      that accepts the cargo (else the nearest one) + SU there (O track)
//   damaged + empty, station can repair them        -> INSTANDSETZUNG (SM): pit stop, repair to 100 %, park
//                                                      on an S track (last job of the chain); license
//                                                      "Manual Service" needed to take it
//   damaged + empty, station can not repair them    -> SCHADWAGENUEBERFUEHRUNG / MAINTENANCE HAUL (MH): LH +50 % time,
//                                                      +15 % pay to a station that can repair them
//
// EVERY NEW GAME DAY (00:00, game clock)
//   Per station all not yet accepted MH jobs are combined: they are deleted and the cars that can go to the same
//   repair station form one train. Cars on one track -> one MH right away; cars on several tracks -> SORTIEREN /
//   SHUNTING SORTING (SS, shunting job): pick up every group, park the train on an O track. When the SS is handed in,
//   the MH to that repair station is created from that track. Nothing is deleted if no new job can be created.
//   Not yet accepted DH jobs (loaded damaged cars) are combined the same way (destination takes all of the cargo).
//
// FOLLOW-UP (like a job chain, the locomotive may stay coupled): when SO / SS / MH / the unload of a DH is handed in,
// the next job for the damaged cars is created right away: DH -> SU -> (MH) -> SM.
//
// DH, MH, IS and SU are VANILLA definitions (vanilla saving, booklets, multiplayer, LongerLoadingDelay).
// SO is an own definition with vanilla tasks (Transport/Parallel/Sequential -> multiplayer sync works); it is saved
// in an own savegame key and gets its own booklet (see TrainServiceReworkShuntingOut.cs).
// The job type is always recognised by the job ID (XX-SO-00, XX-IS-00, XX-DH-00, XX-MH-00).

using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityModManagerNet;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;

namespace TrainServiceRework
{
    // =========================================================
    // CORE
    // =========================================================
    public static class DamagedJobSystem
    {
        private const float DamageSettleDelay = 10f;
        private const float PendingCheckInterval = 1f;
        private const float FallbackScanInterval = 900f;
        private const float RetryCooldown = 300f;
        private const int MaxChainsPerScan = 3;
        private const float RepairedCondition = 99.5f;
        private const float FallbackBodyPricePerUnit = 150f;
        private const float MinimumOverhaulWage = 500f;

        // multipliers from the specification
        private const float DamagedTrainTimeFactor = 1.5f;
        private const float DamagedTrainPayFactor = 1.25f;
        private const float TransferTimeFactor = 1.5f;
        private const float TransferPayFactor = 1.15f;

        private static float nextPendingCheck;
        private static float nextFallbackScan;

        // new game day -> combine the maintenance hauls of every station
        private const float DayCheckInterval = 5f;
        private static float nextDayCheck;
        private static DateTime? lastGameDate;
        private static bool clockLookupDone;
        private static PropertyInfo? weatherInstanceProperty;
        private static FieldInfo? weatherManagerField;
        private static PropertyInfo? managerDateTimeProperty;

        // cars that were damaged recently -> checked once when the time is up
        private static readonly Dictionary<TrainCar, float> pendingDamage = new Dictionary<TrainCar, float>();

        // job kind (job ID code) for chains that are finalized later (PersistentJobsRework finalizes refill chains)
        private static readonly Dictionary<JobChainController, SpecialJobBookletColorKind> pendingKinds =
            new Dictionary<JobChainController, SpecialJobBookletColorKind>();

        // heavy steps are spread over several frames (one per frame) – no hitch when a job is replaced
        private static readonly Queue<Action> work = new Queue<Action>();

        // building a chain for somebody else (PersistentJobsRework refill): don't finalize, just hand it over
        private static bool deferFinalize;
        private static JobChainController? lastChain;
        private static readonly System.Random Rng = new System.Random();
        private static readonly Dictionary<string, float> retryAfter = new Dictionary<string, float>();

        // per-scan track bookings (several chains in one scan must not pick the same free space twice)
        private static readonly Dictionary<Track, float> booked = new Dictionary<Track, float>();

        private static readonly FieldInfo? transportCarsField = AccessTools.Field(typeof(TransportTask), "cars");
        private static readonly FieldInfo? nestedTasksField = AccessTools.Field(typeof(SequentialTasks), "tasks");
        private static readonly FieldInfo? parallelTasksField = AccessTools.Field(typeof(ParallelTasks), "tasks");

        public static bool Enabled => Main.Settings.DamagedJobs;
        public static float Threshold => Mathf.Clamp(Main.Settings.DamagedThreshold, 5f, 95f);

        /// <summary>Host or single player (clients get everything from the host).</summary>
        public static bool IsAuthority => !ImmersiveServiceMultiplayer.IsClientOnly;

        // =====================================================
        // DAMAGE EVENT (CarDamageModel.DamageCar)
        // =====================================================
        /// <summary>Run a step in a later frame (one step per frame).</summary>
        public static void Enqueue(Action step)
        {
            if (step != null)
                work.Enqueue(step);
        }

        public static void NotifyDamaged(TrainCar? car)
        {
            if (car == null || !Enabled || !IsAuthority)
                return;

            // (re)start the delay – the condition is read when the damage has settled
            pendingDamage[car] = Time.unscaledTime + DamageSettleDelay;
        }

        // =====================================================
        // TICK (from the ImmersiveServiceRunner) – cheap: only looks at recently damaged cars
        // =====================================================
        public static void Tick()
        {
            if (!Enabled || !IsAuthority)
            {
                if (pendingDamage.Count > 0)
                    pendingDamage.Clear();

                return;
            }

            float now = Time.unscaledTime;

            // one queued step per frame
            if (work.Count > 0)
            {
                Action step = work.Dequeue();

                try
                {
                    if (WorldReady())
                        step();
                }
                catch (Exception ex)
                {
                    Main.LogWarning("DAMAGED JOBS -> step failed: " + ex.Message);
                }

                return;
            }

            if (now >= nextPendingCheck)
            {
                nextPendingCheck = now + PendingCheckInterval;

                if (pendingDamage.Count > 0 && WorldReady())
                {
                    try
                    {
                        ProcessPendingDamage(now);
                    }
                    catch (Exception ex)
                    {
                        Main.LogWarning("DAMAGED JOBS -> checking damaged cars failed: " + ex);
                    }
                }
            }

            // 00:00 game time: combine the maintenance hauls (MH) per station
            if (now >= nextDayCheck)
            {
                nextDayCheck = now + DayCheckInterval;

                try
                {
                    CheckDayChange();
                }
                catch (Exception ex)
                {
                    Main.LogWarning("DAMAGED JOBS -> day change check failed: " + ex.Message);
                }
            }

            // without PersistentJobsRework: own (rare) pass for jobless damaged cars
            if (now >= nextFallbackScan)
            {
                nextFallbackScan = now + FallbackScanInterval;

                if (!DamagedJobsPersistentJobsCompat.Active && WorldReady())
                {
                    try
                    {
                        FallbackScan();
                    }
                    catch (Exception ex)
                    {
                        Main.LogWarning("DAMAGED JOBS -> scan failed: " + ex);
                    }
                }
            }
        }

        private static bool WorldReady() =>
            WorldStreamingInit.IsLoaded && JobsManager.Instance != null && CarSpawner.Instance != null &&
            YardTracksOrganizer.Instance != null && StationController.allStations != null && LicenseManager.Instance != null;

        private static void ProcessPendingDamage(float now)
        {
            List<TrainCar>? due = null;

            foreach (KeyValuePair<TrainCar, float> entry in pendingDamage)
            {
                if (entry.Value <= now)
                    (due ??= new List<TrainCar>()).Add(entry.Key);
            }

            if (due == null)
                return;

            foreach (TrainCar car in due)
            {
                pendingDamage.Remove(car);

                if (car == null || car.logicCar == null || !IsDamaged(car))
                    continue;

                HandleDamagedCar(car);

                // the rest waits for the next check (spread the work)
                if (work.Count > 0)
                    break;
            }
        }

        /// <summary>The one-time case: a car of a not yet accepted job fell below the limit.</summary>
        private static void HandleDamagedCar(TrainCar car)
        {
            Job? job = FindAvailableJob(car);

            // accepted job or no job: nothing happens now (jobless cars get their job with the next job generation)
            if (job == null || job.State != JobState.Available)
                return;

            // our own jobs are not replaced – except a not yet accepted SO: another car of the train was damaged,
            // so a new SO with all damaged cars is made
            SpecialJobBookletColorKind serviceKind = SpecialJobBookletColorHelper.GetServiceKind(job.ID);

            if (serviceKind != SpecialJobBookletColorKind.None && serviceKind != SpecialJobBookletColorKind.ShuntingOut)
                return;

            bool circleHaul = IsCircleHaul(job);

            if (!circleHaul &&
                job.jobType != JobType.Transport && job.jobType != JobType.EmptyHaul &&
                job.jobType != JobType.ShuntingLoad && job.jobType != JobType.ShuntingUnload)
            {
                return;
            }

            List<Car> logicCars = JobCars(job);

            if (logicCars.Count == 0)
                return;

            if (job.jobType == JobType.ShuntingUnload && !circleHaul && UnloadKeepsDamagedApart(job, logicCars))
                return;

            string id = job.ID;
            JobType type = job.jobType;

            if (!DamagedJobTrash.Expire(job))
                return;

            Main.Log($"DAMAGED JOBS -> deleted not yet accepted job {id} ({(circleHaul ? "CH" : type.ToString())}): {car.ID} at {Condition(car):0.0} %");
            work.Enqueue(DamagedJobTrash.RemoveOrphanedOverviews);

            // replace it: per track the cars of the old job are standing on
            booked.Clear();
            Dictionary<Track, StationController> trackToStation = TrackStations();
            List<TrainCar> cars = OrderByConsist(TrainCar.ExtractTrainCars(logicCars).Where(c => c != null).ToList());

            foreach (IGrouping<Track?, TrainCar> byTrack in cars.GroupBy(c => c.logicCar?.CurrentTrack))
            {
                Track? track = byTrack.Key;

                if (track == null || !trackToStation.TryGetValue(track, out StationController station))
                    continue;

                CarGroup group = new CarGroup { Station = station, Track = track, Cars = byTrack.ToList() };

                // tracks without damaged cars: these cars are simply jobless now (next job generation)
                if (!group.Cars.Any(IsDamaged))
                    continue;

                // the new job is created in a later frame
                work.Enqueue(() => RunGroup(group));
            }
        }

        private static Job? FindAvailableJob(TrainCar car)
        {
            Job? job = null;

            try
            {
                job = JobsManager.Instance.GetJobOfCar(car.logicCar, false);
            }
            catch
            {
                job = null;
            }

            if (job != null)
                return job.State == JobState.Available ? job : null;

            // not registered as car job: search the available jobs of all stations (only on a damage event)
            foreach (StationController station in StationController.allStations)
            {
                List<Job>? available = station?.logicStation?.availableJobs;

                if (available == null)
                    continue;

                foreach (Job candidate in available)
                {
                    if (candidate != null && candidate.State == JobState.Available && JobCars(candidate).Contains(car.logicCar))
                        return candidate;
                }
            }

            return null;
        }

        private static bool IsCircleHaul(Job job) =>
            (int)job.jobType == 121 || (job.ID != null && job.ID.Contains("-CH-"));

        /// <summary>Cars in the order they are coupled (for the booklet).</summary>
        private static List<TrainCar> OrderByConsist(List<TrainCar> cars)
        {
            return cars
                .OrderBy(c => c.trainset != null ? c.trainset.id : int.MaxValue)
                .ThenBy(c => c.trainset != null && c.trainset.cars != null ? c.trainset.cars.IndexOf(c) : 0)
                .ToList();
        }

        private static bool RunGroup(CarGroup group)
        {
            string key = group.Key;
            string result;
            bool ok;

            try
            {
                ok = HandleGroup(group, out result);
            }
            catch (Exception ex)
            {
                ok = false;
                result = "error: " + ex.Message;
            }

            string place = $"{group.Station.logicStation.ID} {group.Track.ID.FullDisplayID}";

            if (ok)
            {
                retryAfter.Remove(key);
                Main.Log($"DAMAGED JOBS -> {place}: {group.Cars.Count} car(s) -> {result}");
            }
            else
            {
                retryAfter[key] = Time.unscaledTime + RetryCooldown;
                Main.Log($"DAMAGED JOBS -> {place}: {group.Cars.Count} car(s) -> no job ({result})");
            }

            return ok;
        }

        // =====================================================
        // PERSISTENTJOBSREWORK REFILL (full game hour): TSR builds the chain, PJRework finalizes it
        // =====================================================
        public static JobChainController? BuildForRefill(StationController? station, Track? track, List<TrainCar> cars, out string result)
        {
            result = "no station/track";

            if (station == null || track == null || cars.Count == 0 || !WorldReady())
                return null;

            booked.Clear();
            CarGroup group = new CarGroup { Station = station, Track = track, Cars = OrderByConsist(cars) };

            deferFinalize = true;
            lastChain = null;

            try
            {
                bool ok = HandleGroup(group, out result);
                return ok ? lastChain : null;
            }
            catch (Exception ex)
            {
                result = "error: " + ex.Message;
                return null;
            }
            finally
            {
                deferFinalize = false;
                lastChain = null;
            }
        }

        /// <summary>Used by the FinalizeSetupAndGenerateFirstJob patch (job ID code of our chains).</summary>
        public static SpecialJobBookletColorKind TakePendingKind(JobChainController chain)
        {
            if (chain == null || !pendingKinds.TryGetValue(chain, out SpecialJobBookletColorKind kind))
                return SpecialJobBookletColorKind.None;

            pendingKinds.Remove(chain);
            return kind;
        }

        public static bool IsOwnPendingChain(JobChainController chain) => chain != null && pendingKinds.ContainsKey(chain);

        // =====================================================
        // FALLBACK WITHOUT PERSISTENTJOBSREWORK (every 15 min)
        // =====================================================
        private static void FallbackScan()
        {
            booked.Clear();

            if (retryAfter.Count > 500)
                retryAfter.Clear();

            List<CarGroup> groups = CollectDamagedGroups(TrackStations());
            int created = 0;

            foreach (CarGroup group in groups)
            {
                if (created >= MaxChainsPerScan)
                    break;

                if (retryAfter.TryGetValue(group.Key, out float until) && Time.unscaledTime < until)
                    continue;

                if (RunGroup(group))
                    created++;
            }
        }

        // =====================================================
        // NEW GAME DAY: COMBINE THE MAINTENANCE HAULS (MH) + SHUNTING SORTING (SS)
        // =====================================================
        private static void CheckDayChange()
        {
            if (!WorldReady())
            {
                // loading / main menu: the next world starts counting from scratch
                lastGameDate = null;
                return;
            }

            DateTime? time = GameTime();

            if (time == null)
                return;

            DateTime date = time.Value.Date;

            if (lastGameDate == null || date <= lastGameDate.Value)
            {
                lastGameDate = date;
                return;
            }

            lastGameDate = date;
            Main.Log($"DAMAGED JOBS -> new game day {date:yyyy-MM-dd}: combining the maintenance hauls");

            // one station per frame
            foreach (StationController station in StationController.allStations.ToList())
            {
                if (station != null)
                    work.Enqueue(() => CombineTransfers(station));
            }
        }

        /// <summary>Game clock (weather system), null if not available.</summary>
        private static DateTime? GameTime()
        {
            try
            {
                if (!clockLookupDone)
                {
                    clockLookupDone = true;
                    Type? driver = AccessTools.TypeByName("DV.WeatherSystem.WeatherDriver");

                    if (driver != null)
                    {
                        weatherInstanceProperty = driver.GetProperty("Instance",
                            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                        weatherManagerField = AccessTools.Field(driver, "manager");

                        if (weatherManagerField != null)
                            managerDateTimeProperty = AccessTools.Property(weatherManagerField.FieldType, "DateTime");
                    }

                    if (weatherInstanceProperty == null || managerDateTimeProperty == null)
                        Main.LogWarning("DAMAGED JOBS -> game clock not found, the maintenance hauls are not combined at 00:00");
                }

                if (weatherInstanceProperty == null || weatherManagerField == null || managerDateTimeProperty == null)
                    return null;

                object? instance = weatherInstanceProperty.GetValue(null);

                if (instance == null || (instance is UnityEngine.Object unityObject && unityObject == null))
                    return null;

                object? manager = weatherManagerField.GetValue(instance);

                if (manager == null)
                    return null;

                return managerDateTimeProperty.GetValue(manager) is DateTime value ? value : (DateTime?)null;
            }
            catch
            {
                return null;
            }
        }

        private sealed class TransferEntry
        {
            public Job Job = null!;
            public bool Loaded;
            public List<TrainCar> Cars = new List<TrainCar>();
            public List<CarCategory> Categories = new List<CarCategory>();
            public List<CargoType> Cargo = new List<CargoType>();
        }

        /// <summary>
        /// All not yet accepted MH (empty) and DH (loaded) jobs of one station: the ones that can go to the same
        /// station are combined into one train (one track -> MH/DH right away, several tracks -> SS first).
        /// </summary>
        private static void CombineTransfers(StationController station)
        {
            List<Job>? available = station?.logicStation?.availableJobs;
            string? yardId = station?.stationInfo?.YardID;

            if (station == null || available == null || string.IsNullOrEmpty(yardId))
                return;

            List<Job> transfers = available
                .Where(j => j != null && j.State == JobState.Available && j.chainData != null && j.chainData.chainOriginYardId == yardId)
                .Where(j =>
                {
                    SpecialJobBookletColorKind kind = SpecialJobBookletColorHelper.GetServiceKind(j.ID);
                    return kind == SpecialJobBookletColorKind.RepairEmptyHaul || kind == SpecialJobBookletColorKind.DamagedFreight;
                })
                .ToList();

            if (transfers.Count < 2)
                return;

            Dictionary<Track, StationController> trackToStation = TrackStations();
            List<TransferEntry> entries = new List<TransferEntry>();

            foreach (Job job in transfers)
            {
                List<TrainCar> cars = TrainCar.ExtractTrainCars(JobCars(job)).Where(c => c != null && c.logicCar != null).ToList();

                if (cars.Count == 0 || !cars.Any(IsDamaged))
                    continue;

                // standing still, in this station, not coupled to a locomotive (somebody is working with them)
                bool usable = cars.All(c =>
                    c.logicCar.CurrentTrack != null && !c.derailed && Mathf.Abs(c.GetForwardSpeed()) <= 0.3f &&
                    trackToStation.TryGetValue(c.logicCar.CurrentTrack, out StationController at) && at == station &&
                    (c.trainset == null || c.trainset.cars == null || !c.trainset.cars.Any(m => m != null && IsProtected(m))));

                if (!usable)
                    continue;

                bool loaded = cars.All(IsLoaded);

                // mixed loaded / empty: leave it as it is
                if (!loaded && cars.Any(IsLoaded))
                    continue;

                entries.Add(new TransferEntry
                {
                    Job = job,
                    Loaded = loaded,
                    Cars = cars,
                    Categories = cars.Select(CarCategoryHelper.GetCarCategory).Distinct().ToList(),
                    Cargo = loaded ? cars.Select(c => c.logicCar.CurrentCargoTypeInCar).Distinct().ToList() : new List<CargoType>()
                });
            }

            booked.Clear();
            CombinePass(station, entries.Where(e => !e.Loaded).ToList(), false);
            CombinePass(station, entries.Where(e => e.Loaded).ToList(), true);
        }

        /// <summary>The station takes the cargo (accepted + a warehouse machine for all of it).</summary>
        private static bool TakesCargo(StationController station, List<CargoType> cargo) =>
            Accepts(station, cargo) && (station.logicStation.yard.GetWarehouseMachinesThatSupportCargoTypes(cargo)?.Count ?? 0) > 0;

        private static void CombinePass(StationController station, List<TransferEntry> entries, bool loaded)
        {
            if (entries.Count < 2)
                return;

            List<StationController> candidates = StationController.allStations
                .Where(s => s != null && s != station && s.logicStation != null)
                .ToList();

            while (entries.Count >= 2)
            {
                StationController? best = null;
                List<TransferEntry> bestEntries = new List<TransferEntry>();
                bool bestWorkshop = false;
                float bestDistance = float.MaxValue;

                foreach (StationController candidate in candidates)
                {
                    List<TransferEntry> covered = new List<TransferEntry>();
                    List<CargoType> cargo = new List<CargoType>();

                    foreach (TransferEntry entry in entries)
                    {
                        if (loaded)
                        {
                            // DH: the destination must take the cargo of every combined job
                            List<CargoType> together = cargo.Union(entry.Cargo).ToList();

                            if (!TakesCargo(candidate, together))
                                continue;

                            cargo = together;
                        }
                        else if (!CarRepairHelper.IsRepairStationForCategories(candidate, entry.Categories))
                        {
                            continue;
                        }

                        covered.Add(entry);
                    }

                    if (covered.Count < 2)
                        continue;

                    // DH: stations with a workshop for these cars first
                    bool workshop = !loaded || CarRepairHelper.IsRepairStationForCategories(candidate,
                        covered.SelectMany(e => e.Categories).Distinct().ToList());
                    float distance = JobPaymentCalculator.GetDistanceBetweenStations(station, candidate);

                    bool better = covered.Count > bestEntries.Count ||
                                  (covered.Count == bestEntries.Count && workshop && !bestWorkshop) ||
                                  (covered.Count == bestEntries.Count && workshop == bestWorkshop && distance < bestDistance);

                    if (!better)
                        continue;

                    best = candidate;
                    bestEntries = covered;
                    bestWorkshop = workshop;
                    bestDistance = distance;
                }

                if (best == null || bestEntries.Count < 2)
                    break;

                foreach (TransferEntry entry in bestEntries)
                    entries.Remove(entry);

                CombineGroup(station, best, bestEntries, loaded);
            }
        }

        private static void CombineGroup(StationController station, StationController target, List<TransferEntry> group, bool loaded)
        {
            List<TrainCar> cars = OrderByConsist(group.SelectMany(e => e.Cars).Distinct().ToList());
            List<CarCategory> categories = cars.Select(CarCategoryHelper.GetCarCategory).Distinct().ToList();
            float length = Length(cars);
            string ids = string.Join(", ", group.Select(e => e.Job.ID));

            // pick-ups: one per track the cars are standing on
            List<IGrouping<Track, TrainCar>> byTrack = cars.GroupBy(c => c.logicCar.CurrentTrack).ToList();

            // check BEFORE deleting anything: free space for the combined train here and at the destination
            YardData targetYard = GetYard(target);
            Track? arrival = PickTrack(length, new List<Track>(), targetYard.In, targetYard.Out);

            if (arrival == null)
            {
                Main.Log($"DAMAGED JOBS -> {station.logicStation.ID}: {ids} not combined (no free I/O track at {target.logicStation.ID} for {length:0} m)");
                return;
            }

            Unbook(arrival, length);

            Track? end = null;

            if (byTrack.Count > 1)
            {
                YardData yard = GetYard(station);
                end = PickTrack(length, new List<Track>(), yard.Out, yard.In);

                if (end == null)
                {
                    Main.Log($"DAMAGED JOBS -> {station.logicStation.ID}: {ids} not combined (no free O track for {length:0} m)");
                    return;
                }
            }

            foreach (TransferEntry entry in group)
                DamagedJobTrash.Expire(entry.Job);

            work.Enqueue(DamagedJobTrash.RemoveOrphanedOverviews);

            string result;
            bool ok;

            if (end == null)
            {
                // all cars on one track: one DH / MH right away
                CarGroup carGroup = new CarGroup { Station = station, Track = byTrack[0].Key, Cars = cars };
                ok = loaded
                    ? TryDamagedTrain(carGroup, cars, out result, target)
                    : TryRepairTransfer(carGroup, cars, categories, out result, target);
            }
            else
            {
                // SS first, the DH / MH follows when it is handed in (see OnChainCompleted)
                ok = TrySorting(station, target, byTrack, end, cars, out result);

                if (!ok)
                    Unbook(end, length);
            }

            Main.Log($"DAMAGED JOBS -> {station.logicStation.ID}: {ids} combined ({cars.Count} car(s)) -> " +
                     (ok ? result : "no job (" + result + "), the cars get their job with the next job generation"));
        }

        /// <summary>SORTIEREN / SHUNTING SORTING (SS): collect the cars of several tracks on one O track.</summary>
        private static bool TrySorting(StationController station, StationController target, List<IGrouping<Track, TrainCar>> byTrack,
            Track end, List<TrainCar> cars, out string result)
        {
            List<CarsPerTrack> pickups = byTrack
                .Select(g => new CarsPerTrack(g.Key, TrainCar.ExtractLogicCars(g.ToList())))
                .ToList();

            List<TrainCar> ordered = byTrack.SelectMany(g => g).ToList();
            List<CargoType> cargo = ordered.Where(IsLoaded).Select(c => c.logicCar.CurrentCargoTypeInCar).ToList();
            float wage = JobPaymentCalculator.CalculateJobPayment(JobType.ShuntingLoad, 500f, PaymentData(ordered, cargo));

            // vanilla rule for shunting jobs: bonus time per pick-up track
            float time = JobPaymentCalculator.CalculateShuntingBonusTimeLimit(pickups.Count, false);
            string yardId = station.stationInfo.YardID;

            JobChainController chain = NewChain(station, station, ordered, "SS");
            ShuntingOutJobDefinition definition = chain.jobChainGO.AddComponent<ShuntingOutJobDefinition>();
            definition.PopulateBaseJobDefinition(station.logicStation, time, wage,
                new StationsChainData(yardId, yardId),
                Licenses(JobType.ShuntingUnload, cargo.Distinct().ToList(), ordered));
            definition.SetupSorting(chain, pickups, end, target.stationInfo.YardID);
            chain.AddJobDefinitionToChain(definition);

            if (!Finalize(chain, SpecialJobBookletColorKind.Sorting))
            {
                result = "finalizing failed";
                return false;
            }

            string next = cargo.Count > 0 ? "SPECIAL HAUL" : "MAINTENANCE HAUL";
            result = $"SHUNTING SORTING ({pickups.Count} pick-ups -> {end.ID.FullDisplayID}), then {next} to {target.logicStation.ID}";
            return true;
        }

        // =====================================================
        // FOLLOW-UP JOBS (like a job chain): when one of our jobs is handed in, the next one is created right away –
        // also with the locomotive still coupled.
        //   SO  -> damaged cars: DH (loaded) / SM (repairable here) / MH
        //   SS  -> DH (loaded) / MH, to the station chosen when the cars were combined
        //   SU  (end of a DH chain / unload with separated damaged cars) -> SM (repairable here) / MH
        //   MH  -> SM at the repair station
        //   SM  -> end
        // =====================================================
        public static void OnChainCompleted(JobChainController? chain, Job? job)
        {
            if (!Enabled || !IsAuthority || job == null)
                return;

            SpecialJobBookletColorKind kind = SpecialJobBookletColorHelper.GetServiceKind(job.ID);
            ShuntingOutJobDefinition? definition = chain?.jobChainGO != null
                ? chain.jobChainGO.GetComponent<ShuntingOutJobDefinition>()
                : null;

            List<Car> cars;
            string? preferredYard = null;
            bool requireSeparated = false;

            switch (kind)
            {
                case SpecialJobBookletColorKind.ShuntingOut:
                    cars = definition != null && definition.DamagedCars.Count > 0 ? definition.DamagedCars.ToList() : JobCars(job);
                    break;

                case SpecialJobBookletColorKind.Sorting:
                    cars = definition != null && definition.AllCars.Count > 0 ? definition.AllCars.ToList() : JobCars(job);
                    preferredYard = definition?.TargetYardId;
                    break;

                case SpecialJobBookletColorKind.RepairEmptyHaul:
                    cars = JobCars(job);
                    break;

                case SpecialJobBookletColorKind.None:
                    // unload: only damaged cars that stand apart from the healthy ones (DH chain, unload + split)
                    if (job.jobType != JobType.ShuntingUnload)
                        return;

                    cars = JobCars(job);
                    requireSeparated = true;
                    break;

                default:
                    return;
            }

            if (cars.Count == 0)
                return;

            string id = job.ID;
            work.Enqueue(() => CreateFollowUp(id, job, cars, preferredYard, requireSeparated));
        }

        private static void CreateFollowUp(string jobId, Job completed, List<Car> cars, string? preferredYard, bool requireSeparated)
        {
            JobsManager jobs = JobsManager.Instance;
            Dictionary<Track, StationController> trackToStation = TrackStations();

            List<TrainCar> all = OrderByConsist(TrainCar.ExtractTrainCars(cars)
                .Where(c => c != null && c.logicCar != null)
                .Where(c =>
                {
                    Job? current = jobs.GetJobOfCar(c.logicCar, false);
                    return current == null || current == completed ||
                           (current.State != JobState.Available && current.State != JobState.InProgress);
                })
                .ToList());

            List<TrainCar> damaged = all.Where(IsDamaged).ToList();

            if (damaged.Count == 0)
                return;

            StationController? preferred = string.IsNullOrEmpty(preferredYard) ? null : StationController.GetStationByYardID(preferredYard);
            booked.Clear();

            foreach (IGrouping<Track?, TrainCar> byTrack in damaged.GroupBy(c => c.logicCar.CurrentTrack))
            {
                Track? track = byTrack.Key;

                if (track == null || !trackToStation.TryGetValue(track, out StationController station))
                    continue;

                if (requireSeparated && all.Any(c => !IsDamaged(c) && c.logicCar.CurrentTrack == track))
                    continue;

                CarGroup group = new CarGroup { Station = station, Track = track, Cars = byTrack.ToList() };
                string result = HandleDamagedOnly(group, preferred);
                Main.Log($"DAMAGED JOBS -> {jobId} handed in: {group.Cars.Count} damaged car(s) on {track.ID.FullDisplayID} -> {result}");
            }
        }

        /// <summary>Follow-up for a group of damaged cars only (no healthy cars among them).</summary>
        private static string HandleDamagedOnly(CarGroup group, StationController? preferred)
        {
            List<string> results = new List<string>();
            List<TrainCar> loaded = group.Cars.Where(IsLoaded).ToList();
            List<TrainCar> empty = group.Cars.Where(c => !IsLoaded(c)).ToList();
            string result;

            if (loaded.Count > 0 &&
                TakesCargo(group.Station, loaded.Select(c => c.logicCar.CurrentCargoTypeInCar).Distinct().ToList()))
            {
                // cargo accepted here: unload here first (SU), the empty damaged cars get SM / MH afterwards
                CarGroup unload = new CarGroup { Station = group.Station, Track = group.Track, Cars = loaded };
                results.Add(TryUnloadSplit(unload, loaded, new List<TrainCar>(), out result) ? result : "no job (" + result + ")");
            }
            else if (loaded.Count > 0)
            {
                // all loaded cars together (combined train), otherwise one DH per cargo
                if (TryDamagedTrain(group, loaded, out result, preferred))
                {
                    results.Add(result);
                }
                else
                {
                    foreach (IGrouping<CargoType, TrainCar> byCargo in loaded.GroupBy(c => c.logicCar.CurrentCargoTypeInCar))
                        results.Add(TryDamagedTrain(group, byCargo.ToList(), out result, preferred) ? result : "no job (" + result + ")");
                }
            }

            if (empty.Count > 0)
            {
                // repairable here: maintenance (SM)
                List<TrainCar> here = empty
                    .Where(c => CarRepairHelper.IsRepairStationForCategories(group.Station, new List<CarCategory> { CarCategoryHelper.GetCarCategory(c) }))
                    .ToList();

                if (here.Count > 0)
                    results.Add(TryOverhaul(group, here, out result) ? result : "no job (" + result + ")");

                // the rest: maintenance haul (MH) to a repair station
                List<TrainCar> away = empty.Where(c => !here.Contains(c)).ToList();

                if (away.Count > 0)
                {
                    List<CarCategory> categories = away.Select(CarCategoryHelper.GetCarCategory).Distinct().ToList();

                    if (TryRepairTransfer(group, away, categories, out result, preferred))
                    {
                        results.Add(result);
                    }
                    else if (categories.Count > 1)
                    {
                        foreach (IGrouping<CarCategory, TrainCar> byCategory in away.GroupBy(CarCategoryHelper.GetCarCategory))
                        {
                            results.Add(TryRepairTransfer(group, byCategory.ToList(), new List<CarCategory> { byCategory.Key }, out result, preferred)
                                ? result
                                : "no job (" + result + ")");
                        }
                    }
                    else
                    {
                        results.Add("no job (" + result + ")");
                    }
                }
            }

            return results.Count > 0 ? string.Join("; ", results) : "nothing to do";
        }

        // =====================================================
        // DAMAGE
        // =====================================================
        public static float Condition(TrainCar? car)
        {
            if (car == null || car.CarDamage == null)
                return 100f;

            return Mathf.Clamp(car.CarDamage.EffectiveHealthPercentage100Notation, 0f, 100f);
        }

        public static bool IsDamaged(TrainCar? car) => car != null && (Condition(car) < Threshold || IsSoftDamaged(car));

        // ---------------- first (soft) threshold ----------------
        // Between the soft threshold (e.g. 75 %) and the hard one (e.g. 50 %) a car MAY become a damaged car: only when
        // it gets a new job (job generation), with a chance. Once chosen it stays a damaged car until it is repaired.

        public static float SoftThreshold => Mathf.Clamp(Main.Settings.SoftDamagedThreshold, Threshold, 99f);
        public static float SoftChance => Mathf.Clamp01(Main.Settings.SoftDamagedChance / 100f);

        private static readonly HashSet<string> softDamaged = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> softRolled = new Dictionary<string, long>(StringComparer.Ordinal);

        public static IEnumerable<string> SoftDamagedIds => softDamaged;

        public static void SetSoftDamagedIds(IEnumerable<string> ids)
        {
            softDamaged.Clear();
            softRolled.Clear();

            foreach (string id in ids)
            {
                if (!string.IsNullOrEmpty(id))
                    softDamaged.Add(id);
            }
        }

        private static bool IsSoftDamaged(TrainCar car)
        {
            if (car.ID == null || softDamaged.Count == 0 || !softDamaged.Contains(car.ID))
                return false;

            // repaired (above the soft threshold again): a normal car again
            if (Condition(car) >= SoftThreshold)
            {
                softDamaged.Remove(car.ID);
                return false;
            }

            return true;
        }

        /// <summary>Cars that get a new job now: cars between the two thresholds become damaged cars with the chance (once per game hour).</summary>
        public static void RollSoftDamage(IEnumerable<TrainCar> cars)
        {
            if (!Enabled || SoftChance <= 0f || SoftThreshold <= Threshold)
                return;

            DateTime? time = GameTime();
            long stamp = time.HasValue ? time.Value.Ticks / TimeSpan.TicksPerHour : (long)(Time.realtimeSinceStartup / 3600f);

            foreach (TrainCar car in cars)
            {
                if (car == null || car.ID == null || softDamaged.Contains(car.ID))
                    continue;

                float condition = Condition(car);

                if (condition < Threshold || condition >= SoftThreshold)
                    continue;

                if (softRolled.TryGetValue(car.ID, out long last) && last == stamp)
                    continue;

                softRolled[car.ID] = stamp;

                if (Rng.NextDouble() < SoftChance)
                {
                    softDamaged.Add(car.ID);
                    Main.Log($"DAMAGED JOBS -> {car.ID} at {condition:0.0} % chosen as damaged car (chance {SoftChance * 100f:0} %)");
                }
            }
        }

        /// <summary>
        /// Loaded for the job decisions. An exploded car counts as EMPTY: it gets SM / MH like an empty
        /// damaged car – its destroyed cargo is removed when the repair is paid.
        /// </summary>
        private static bool IsLoaded(TrainCar car) =>
            car.logicCar != null && car.logicCar.CurrentCargoTypeInCar != CargoType.None && !CargoDestroyed(car);

        /// <summary>
        /// The car exploded (savegame "exploded": true). Only cargo at 0 % does NOT count – a destroyed container
        /// is still there.
        /// </summary>
        public static bool CargoDestroyed(TrainCar? car) => car != null && car.isExploded;

        /// <summary>SU: every car group that is parked at the end is either completely damaged or completely healthy.</summary>
        private static bool UnloadKeepsDamagedApart(Job job, List<Car> allCars)
        {
            foreach (Task leaf in LeafTasks(job))
            {
                if (!(leaf is TransportTask))
                    continue;

                List<Car>? cars = GetCars(leaf);

                if (cars == null || cars.Count == 0)
                    continue;

                List<TrainCar> trainCars = TrainCar.ExtractTrainCars(cars);
                int damaged = trainCars.Count(IsDamaged);

                if (damaged == 0 || damaged == trainCars.Count)
                    continue;

                // the pick-up task (all cars to the unload track) is allowed to be mixed
                if (cars.Count == allCars.Count && LeafTasks(job).OfType<TransportTask>().Count() > 1 && IsFirstTransport(job, leaf))
                    continue;

                return false;
            }

            return true;
        }

        private static bool IsFirstTransport(Job job, Task task)
        {
            foreach (Task leaf in LeafTasks(job))
            {
                if (leaf is TransportTask)
                    return leaf == task;
            }

            return false;
        }

        // =====================================================
        // 2) COLLECT JOBLESS DAMAGED CARS
        // =====================================================
        private sealed class CarGroup
        {
            public StationController Station = null!;
            public Track Track = null!;
            public List<TrainCar> Cars = new List<TrainCar>();

            public string Key => string.Join(",", Cars.Select(c => c.CarGUID));
        }

        private static List<CarGroup> CollectDamagedGroups(Dictionary<Track, StationController> trackToStation)
        {
            List<CarGroup> result = new List<CarGroup>();
            HashSet<TrainCar> seen = new HashSet<TrainCar>();
            JobsManager jobs = JobsManager.Instance;

            foreach (TrainCar car in CarSpawner.Instance.AllCars.ToList())
            {
                if (car == null || seen.Contains(car))
                    continue;

                Trainset? set = car.trainset;

                if (set == null || set.cars == null)
                    continue;

                foreach (TrainCar member in set.cars)
                {
                    if (member != null)
                        seen.Add(member);
                }

                // same exclusions as PersistentJobsRework: trainsets with loco/tender/caboose, derailed, moving
                if (set.cars.Any(c => c == null || IsProtected(c)))
                    continue;

                if (set.cars.Any(c => c.derailed))
                    continue;

                if (set.cars.Any(c => Mathf.Abs(c.GetForwardSpeed()) > 0.3f))
                    continue;

                // jobless cars get a new job now: first threshold (chance)
                RollSoftDamage(set.cars.Where(c => c.logicCar != null && jobs.GetJobOfCar(c.logicCar, false) == null));

                if (!set.cars.Any(IsDamaged))
                    continue;

                CarGroup? current = null;

                foreach (TrainCar c in set.cars)
                {
                    Track? track = c.logicCar?.CurrentTrack;
                    bool jobless = c.logicCar != null && jobs.GetJobOfCar(c.logicCar, false) == null;

                    if (!jobless || c.playerSpawnedCar || track == null || !trackToStation.TryGetValue(track, out StationController station))
                    {
                        current = null;
                        continue;
                    }

                    if (current == null || current.Track != track)
                    {
                        current = new CarGroup { Station = station, Track = track };
                        result.Add(current);
                    }

                    current.Cars.Add(c);
                }
            }

            // only groups that really contain damaged cars
            result.RemoveAll(g => g.Cars.Count == 0 || !g.Cars.Any(IsDamaged));
            return result;
        }

        private static bool IsProtected(TrainCar car)
        {
            if (car.IsLoco || CarRepairHelper.IsLocomotiveOrTender(car))
                return true;

            string name = car.name ?? string.Empty;

            return name.StartsWith("Loco", StringComparison.OrdinalIgnoreCase)
                   || name.IndexOf("Tender", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Caboose", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // =====================================================
        // DECISION
        // =====================================================
        private static bool HandleGroup(CarGroup group, out string result)
        {
            List<TrainCar> damaged = group.Cars.Where(IsDamaged).ToList();
            List<TrainCar> healthy = group.Cars.Where(c => !IsDamaged(c)).ToList();
            bool allLoaded = group.Cars.All(IsLoaded);

            // loaded cars whose cargo is accepted here: unload first, then split (damaged separated)
            if (allLoaded && TryUnloadSplit(group, damaged, healthy, out result))
                return true;

            // healthy and damaged cars together: bad order set-out (shunting job with all cars)
            if (healthy.Count > 0)
                return TryShuntingOut(group, damaged, healthy, out result);

            // ---- only damaged cars ----
            List<TrainCar> loaded = damaged.Where(IsLoaded).ToList();
            List<TrainCar> empty = damaged.Where(c => !IsLoaded(c)).ToList();

            if (loaded.Count > 0)
            {
                // one cargo type per damaged car train (destination must accept all of it)
                CargoType cargo = loaded[0].logicCar.CurrentCargoTypeInCar;
                List<TrainCar> sameCargo = loaded.Where(c => c.logicCar.CurrentCargoTypeInCar == cargo).ToList();
                return TryDamagedTrain(group, sameCargo, out result);
            }

            // empty damaged cars: one category per job (repair stations are per category)
            CarCategory category = CarCategoryHelper.GetCarCategory(empty[0]);
            List<TrainCar> sameCategory = empty.Where(c => CarCategoryHelper.GetCarCategory(c) == category).ToList();
            List<CarCategory> categories = new List<CarCategory> { category };

            if (CarRepairHelper.IsRepairStationForCategories(group.Station, categories))
                return TryOverhaul(group, sameCategory, out result);

            return TryRepairTransfer(group, sameCategory, categories, out result);
        }

        // =====================================================
        // SU WITH SEPARATED DAMAGED CARS
        // =====================================================
        private static bool TryUnloadSplit(CarGroup group, List<TrainCar> damaged, List<TrainCar> healthy, out string result)
        {
            StationController station = group.Station;
            List<CargoType> cargo = group.Cars.Select(c => c.logicCar.CurrentCargoTypeInCar).Distinct().ToList();

            if (!Accepts(station, cargo))
            {
                result = $"{station.logicStation.ID} does not accept {string.Join("/", cargo)}";
                return false;
            }

            List<WarehouseMachine>? machines = station.logicStation.yard.GetWarehouseMachinesThatSupportCargoTypes(cargo);

            if (machines == null || machines.Count == 0)
            {
                result = "no warehouse machine for the cargo";
                return false;
            }

            YardData yard = GetYard(station);
            List<Track> exclude = new List<Track> { group.Track };
            List<CarsPerTrack> destinations = new List<CarsPerTrack>();

            if (healthy.Count > 0)
            {
                // healthy cars: S if the station can load them again, otherwise O (an empty haul follows later)
                Track? healthyTrack = CanLoad(station, healthy)
                    ? PickTrack(Length(healthy), exclude, yard.Storage, yard.Out)
                    : PickTrack(Length(healthy), exclude, yard.Out, yard.In);

                if (healthyTrack == null)
                {
                    result = "no free track for the healthy cars after unloading";
                    return false;
                }

                exclude.Add(healthyTrack);
                destinations.Add(new CarsPerTrack(healthyTrack, TrainCar.ExtractLogicCars(healthy)));
            }

            // damaged cars: own track. Repairable here -> S (maintenance follows here), otherwise O (they leave
            // for repair). Exploded cars count as empty, after the unload all of them are empty anyway.
            bool repairableHere = CarRepairHelper.IsRepairStationForCategories(station,
                damaged.Select(CarCategoryHelper.GetCarCategory).Distinct().ToList());
            Track? damagedTrack = repairableHere
                ? PickTrack(Length(damaged), exclude, yard.Storage, yard.Out, yard.In)
                : PickTrack(Length(damaged), exclude, yard.Out, yard.In, yard.Storage);

            if (damagedTrack == null)
            {
                Unbook(destinations);
                result = "no free track for the damaged cars after unloading";
                return false;
            }

            destinations.Add(new CarsPerTrack(damagedTrack, TrainCar.ExtractLogicCars(damaged)));

            JobChainController chain = NewChain(station, station, group.Cars, "SU");
            chain.AddJobDefinitionToChain(UnloadDefinition(chain, station, station, group.Track, destinations,
                machines[Rng.Next(machines.Count)], group.Cars, 1f, 1f));

            if (!Finalize(chain, SpecialJobBookletColorKind.None))
            {
                result = "finalizing failed";
                return false;
            }

            result = $"unload + split (healthy {healthy.Count}, damaged {damaged.Count} -> {damagedTrack.ID.FullDisplayID})";
            return true;
        }

        // =====================================================
        // AUSRANGIEREN (AR)
        // =====================================================
        private static bool TryShuntingOut(CarGroup group, List<TrainCar> damaged, List<TrainCar> healthy, out string result)
        {
            StationController station = group.Station;
            YardData yard = GetYard(station);
            float length = Length(damaged);

            // where do the damaged cars go afterwards? leave the station (DH/MH) -> O track, stay (SU/IS) -> S track
            bool leaves = DamagedCarsLeaveStation(station, damaged);
            List<Track> exclude = new List<Track> { group.Track };
            Track? target = leaves
                ? PickTrack(length, exclude, yard.Out, yard.In, yard.Storage)
                : PickTrack(length, exclude, yard.Storage, yard.Out, yard.In);

            if (target == null)
            {
                result = $"no free {(leaves ? "O" : "S")} track for the damaged cars ({length:0} m)";
                return false;
            }

            List<CargoType> cargo = group.Cars.Where(IsLoaded).Select(c => c.logicCar.CurrentCargoTypeInCar).ToList();
            float wage = JobPaymentCalculator.CalculateJobPayment(JobType.ShuntingUnload, 500f, PaymentData(group.Cars, cargo));
            // pick-ups = groups of damaged cars that don't stand together (+ bringing the rest of the train back)
            float time = JobPaymentCalculator.CalculateShuntingBonusTimeLimit(DamagedRuns(group.Cars) + 1, false);

            JobChainController chain = NewChain(station, station, group.Cars, "SO");
            ShuntingOutJobDefinition definition = chain.jobChainGO.AddComponent<ShuntingOutJobDefinition>();
            definition.PopulateBaseJobDefinition(station.logicStation, time, wage,
                new StationsChainData(station.stationInfo.YardID, station.stationInfo.YardID),
                Licenses(JobType.ShuntingUnload, cargo.Distinct().ToList(), group.Cars));
            definition.Setup(chain, TrainCar.ExtractLogicCars(group.Cars), TrainCar.ExtractLogicCars(damaged), group.Track, target);
            chain.AddJobDefinitionToChain(definition);

            if (!Finalize(chain, SpecialJobBookletColorKind.ShuntingOut))
            {
                result = "finalizing failed";
                return false;
            }

            result = $"BAD ORDER SET-OUT ({damaged.Count} damaged -> {target.ID.FullDisplayID} [{(leaves ? "leave the station" : "stay")}], " +
                     $"{healthy.Count} back to {group.Track.ID.FullDisplayID})";
            return true;
        }

        /// <summary>Number of damaged car groups in the consist (damaged cars standing together count once).</summary>
        private static int DamagedRuns(List<TrainCar> consist)
        {
            int runs = 0;
            bool previous = false;

            foreach (TrainCar car in consist)
            {
                bool damaged = IsDamaged(car);

                if (damaged && !previous)
                    runs++;

                previous = damaged;
            }

            return Math.Max(1, runs);
        }

        /// <summary>true = the damaged cars will leave this station afterwards (damaged car train / transfer).</summary>
        private static bool DamagedCarsLeaveStation(StationController station, List<TrainCar> damaged)
        {
            foreach (TrainCar car in damaged)
            {
                if (IsLoaded(car))
                {
                    List<CargoType> cargo = new List<CargoType> { car.logicCar.CurrentCargoTypeInCar };

                    if (!Accepts(station, cargo) ||
                        (station.logicStation.yard.GetWarehouseMachinesThatSupportCargoTypes(cargo)?.Count ?? 0) == 0)
                    {
                        return true;
                    }
                }
                else if (!CarRepairHelper.IsRepairStationForCategories(station, new List<CarCategory> { CarCategoryHelper.GetCarCategory(car) }))
                {
                    return true;
                }
            }

            return false;
        }

        // =====================================================
        // SCHADZUG (DH): FH + SU
        // =====================================================
        private static bool TryDamagedTrain(CarGroup group, List<TrainCar> cars, out string result, StationController? preferred = null)
        {
            StationController from = group.Station;
            List<CargoType> cargo = cars.Select(c => c.logicCar.CurrentCargoTypeInCar).Distinct().ToList();
            List<CarCategory> categories = cars.Select(CarCategoryHelper.GetCarCategory).Distinct().ToList();
            float length = Length(cars);

            List<StationController> accepting = StationController.allStations
                .Where(s => s != null && s != from && Accepts(s, cargo) &&
                            (s.logicStation.yard.GetWarehouseMachinesThatSupportCargoTypes(cargo)?.Count ?? 0) > 0)
                .ToList();

            if (accepting.Count == 0)
            {
                result = $"no station accepts {string.Join("/", cargo)}";
                return false;
            }

            // stations with a workshop for these cars first (random), then the nearest accepting station
            List<StationController> ordered = accepting
                .Where(s => CarRepairHelper.IsRepairStationForCategories(s, categories))
                .OrderBy(_ => Rng.Next())
                .Concat(accepting
                    .Where(s => !CarRepairHelper.IsRepairStationForCategories(s, categories))
                    .OrderBy(s => JobPaymentCalculator.GetDistanceBetweenStations(from, s)))
                .ToList();

            // the station chosen when the cars were combined first
            if (preferred != null && ordered.Remove(preferred))
                ordered.Insert(0, preferred);

            foreach (StationController to in ordered)
            {
                YardData yard = GetYard(to);
                Track? arrival = PickTrack(length, new List<Track>(), yard.In, yard.Out);

                if (arrival == null)
                    continue;

                // after unloading at the destination: empty damaged cars to an O track
                Track? parking = PickTrack(length, new List<Track> { arrival }, yard.Out, yard.In, yard.Storage);

                List<WarehouseMachine>? machines = yard.Station.logicStation.yard.GetWarehouseMachinesThatSupportCargoTypes(cargo);

                if (parking == null || machines == null || machines.Count == 0)
                {
                    Unbook(arrival, length);
                    continue;
                }

                JobChainController chain = NewChain(from, to, cars, "DH");
                chain.AddJobDefinitionToChain(TransportDefinition(chain, from, to, group.Track, arrival, cars,
                    DamagedTrainTimeFactor, DamagedTrainPayFactor));
                chain.AddJobDefinitionToChain(UnloadDefinition(chain, from, to, arrival,
                    new List<CarsPerTrack> { new CarsPerTrack(parking, TrainCar.ExtractLogicCars(cars)) },
                    machines[Rng.Next(machines.Count)], cars, 1f, 1f));

                if (!Finalize(chain, SpecialJobBookletColorKind.DamagedFreight))
                {
                    result = "finalizing failed";
                    return false;
                }

                bool workshop = CarRepairHelper.IsRepairStationForCategories(to, categories);
                result = $"SCHADZUG to {to.logicStation.ID}{(workshop ? " (workshop)" : " (nearest accepting station)")} + unload -> {parking.ID.FullDisplayID}";
                return true;
            }

            result = $"no accepting station has a free I/O track for {length:0} m";
            return false;
        }

        // =====================================================
        // SCHADWAGENUEBERFUEHRUNG (MH): LH to a repair station
        // =====================================================
        private static bool TryRepairTransfer(CarGroup group, List<TrainCar> cars, List<CarCategory> categories, out string result,
            StationController? preferred = null)
        {
            StationController from = group.Station;
            float length = Length(cars);

            // the preferred repair station first (combined / sorted train), then all repair stations, random order
            List<StationController> candidates = new List<StationController>();

            if (preferred != null && preferred != from && CarRepairHelper.IsRepairStationForCategories(preferred, categories))
                candidates.Add(preferred);

            for (int i = 0; i < 12; i++)
            {
                StationController? candidate = CarRepairHelper.FindRandomRepairStation(from, categories, Rng);

                if (candidate != null && !candidates.Contains(candidate))
                    candidates.Add(candidate);
            }

            if (candidates.Count == 0)
            {
                result = $"no repair station for {categories[0]}";
                return false;
            }

            foreach (StationController to in candidates)
            {
                YardData yard = GetYard(to);
                Track? arrival = PickTrack(length, new List<Track>(), yard.In, yard.Out);

                if (arrival == null)
                    continue;

                JobChainController chain = NewChain(from, to, cars, "MH");
                chain.AddJobDefinitionToChain(EmptyHaulDefinition(chain, from, to, group.Track, arrival, cars,
                    TransferTimeFactor, TransferPayFactor));

                if (!Finalize(chain, SpecialJobBookletColorKind.RepairEmptyHaul))
                {
                    result = "finalizing failed";
                    return false;
                }

                result = $"MAINTENANCE HAUL to {to.logicStation.ID} -> {arrival.ID.FullDisplayID}";
                return true;
            }

            result = $"no repair station has a free I/O track for {length:0} m";
            return false;
        }

        // =====================================================
        // INSTANDSETZUNG (IS): pit stop, repair, park
        // =====================================================
        private static bool TryOverhaul(CarGroup group, List<TrainCar> cars, out string result)
        {
            StationController station = group.Station;
            YardData yard = GetYard(station);
            float length = Length(cars);
            List<CarCategory> categories = cars.Select(CarCategoryHelper.GetCarCategory).Distinct().ToList();

            // pit stop (service) track of this station for these cars
            Track? service = FindServiceTrack(station, categories);

            if (service == null)
            {
                result = "no pit stop track found at this station";
                return false;
            }

            // SM is the last job of the chain: the repaired cars are parked on an S track (storage), like any other
            // jobless cars of the station – PersistentJobsRework gives them their next job from there
            bool loadable = CanLoad(station, cars);
            List<Track> exclude = new List<Track> { group.Track, service };
            Track? target = PickTrack(length, exclude, yard.Storage);

            // no free S track: the original track if it is an S track, otherwise O / I, at last the original track
            if (target == null)
                target = yard.Storage.Contains(group.Track) ? group.Track : PickTrack(length, exclude, yard.Out, yard.In) ?? group.Track;

            // payment: shunting effort + what the pit stop will charge for the repair (time bonus comes on top).
            // Fee already paid -> the repair is free -> only the shunting effort.
            float repairCost = cars.Sum(EstimateRepairCost);
            float pitCost = cars.Sum(c => EstimateRepairCost(c) * RepairFees.PriceFactor(c));
            float shunting = MaintenanceShuntingWage(cars);
            float wage = Mathf.Max(MinimumOverhaulWage, shunting + pitCost);

            // time: shunting to the pit stop and back + the repair itself (Immersive Service)
            float time = JobPaymentCalculator.CalculateShuntingBonusTimeLimit(3, false) + EstimateRepairSeconds(cars);

            JobChainController chain = NewChain(station, station, cars, "SM");
            ShuntingOutJobDefinition definition = chain.jobChainGO.AddComponent<ShuntingOutJobDefinition>();
            definition.PopulateBaseJobDefinition(station.logicStation, time, wage,
                new StationsChainData(station.stationInfo.YardID, station.stationInfo.YardID),
                Licenses(JobType.ShuntingUnload, new List<CargoType>(), cars));
            definition.Setup(chain, TrainCar.ExtractLogicCars(cars), TrainCar.ExtractLogicCars(cars), group.Track, service,
                ShuntingOutJobDefinition.ModeMaintenance, target);
            chain.AddJobDefinitionToChain(definition);

            if (!Finalize(chain, SpecialJobBookletColorKind.Maintenance))
            {
                result = "finalizing failed";
                return false;
            }

            result = $"MAINTENANCE ({cars.Count} car(s), service {service.ID.FullDisplayID}, repair ~${repairCost:0} " +
                     $"(at the pit stop ${pitCost:0}), pay ${wage:0} = shunting ${shunting:0} + repair) -> " +
                     $"{target.ID.FullDisplayID} ({(loadable ? "loadable here" : "not loadable here")})";
            return true;
        }

        /// <summary>Shunting effort of a maintenance job (like a vanilla shunting job with these cars).</summary>
        public static float MaintenanceShuntingWage(List<TrainCar> cars) =>
            JobPaymentCalculator.CalculateJobPayment(JobType.ShuntingUnload, 500f, PaymentData(cars, new List<CargoType>()));

        /// <summary>
        /// A fee was paid for cars of a not finished maintenance job: the repair of these units is free now, so the
        /// payment of the job goes down by their price (never below the shunting effort).
        /// </summary>
        public static void ReduceMaintenanceWage(Job job, float amount)
        {
            if (job == null || amount <= 0.5f || !IsOverhaulJob(job) ||
                (job.State != JobState.Available && job.State != JobState.InProgress))
            {
                return;
            }

            try
            {
                float current = job.GetBasePaymentForTheJob();
                float floor = Mathf.Max(MinimumOverhaulWage, MaintenanceShuntingWage(TrainCar.ExtractTrainCars(JobCars(job)).Where(c => c != null).ToList()));
                float wage = Mathf.Max(floor, current - amount);

                if (Mathf.Abs(wage - current) < 0.5f)
                    return;

                jobWageField?.SetValue(job, wage);

                foreach (ShuntingOutJobDefinition definition in ShuntingOutJobDefinition.All)
                {
                    if (definition != null && definition.GeneratedJob == job)
                        definition.SetWage(wage);
                }

                Main.Log($"DAMAGED JOBS -> {job.ID}: fee paid, repair now free -> payment ${current:0} -> ${wage:0}");
            }
            catch (Exception ex)
            {
                Main.LogWarning($"DAMAGED JOBS -> adjusting the payment of {job.ID} failed: {ex.Message}");
            }
        }

        private static readonly FieldInfo? jobWageField = AccessTools.Field(typeof(Job), "initialWage");

        /// <summary>Logic track of the pit stop that repairs these cars at this station.</summary>
        private static Track? FindServiceTrack(StationController station, List<CarCategory> categories)
        {
            try
            {
                foreach (Vector3 point in CarRepairHelper.GetRepairPoints(station, categories))
                {
                    Vector3 world = point + WorldMover.currentMove;
                    RailTrack? rail = RailTrack.GetClosest(world, 0f, null).Item1;

                    if (rail != null && RailTrackRegistry.RailTrackToLogicTrack.TryGetValue(rail, out Track track) && track != null)
                        return track;
                }
            }
            catch (Exception ex)
            {
                Main.LogWarning("DAMAGED JOBS -> finding the pit stop track failed: " + ex.Message);
            }

            return null;
        }

        public static float EstimateRepairCost(TrainCar car)
        {
            float missing = 100f - Condition(car);

            if (missing <= 0f)
                return 0f;

            float pricePerUnit = FallbackBodyPricePerUnit;

            try
            {
                if (car.carLivery != null)
                {
                    float price = ResourceTypes.GetFullUnitPriceOfResource(ResourceType.Car_DMG, car.carLivery, null,
                        DV.Globals.G.GameParams.ResourcesParams);

                    if (!float.IsNaN(price) && !float.IsInfinity(price) && price > 0f)
                        pricePerUnit = price;
                }
            }
            catch
            {
                // fallback price
            }

            if (car.carLivery != null && string.Equals(car.carLivery.id, "Crane", StringComparison.OrdinalIgnoreCase))
                pricePerUnit = 850f;

            return missing * pricePerUnit;
        }

        private static float EstimateRepairSeconds(List<TrainCar> cars)
        {
            if (!Main.Settings.ImmersiveService)
                return 0f;

            float missing = cars.Sum(c => 100f - Condition(c));
            return missing * Mathf.Max(0f, Main.Settings.SecondsBody) * 1.5f;
        }

        // =====================================================
        // INSTANDSETZUNG GATE (TransportTask can't finish before the cars are repaired)
        // =====================================================
        private static readonly Dictionary<TransportTask, float> gateCheckTime = new Dictionary<TransportTask, float>();
        private static readonly Dictionary<TransportTask, bool> gateResult = new Dictionary<TransportTask, bool>();

        /// <summary>Instandsetzung needs the general license "Manual Service" (checked when taking the job).</summary>
        public static bool HasManualService()
        {
            try
            {
                LicenseManager manager = LicenseManager.Instance;
                return manager == null || manager.IsGeneralLicenseAcquired(GeneralLicenseType.ManualService.ToV2());
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Maintenance job (SM) or the old in-station car repair job (IS).</summary>
        public static bool IsOverhaulJob(Job? job)
        {
            if (job == null)
                return false;

            SpecialJobBookletColorKind kind = SpecialJobBookletColorHelper.GetServiceKind(job.ID);
            return kind == SpecialJobBookletColorKind.Maintenance || kind == SpecialJobBookletColorKind.Overhaul;
        }

        /// <summary>IDs of all cars of a job.</summary>
        public static List<string> JobCarIds(Job job) =>
            TrainCar.ExtractTrainCars(JobCars(job)).Where(c => c != null).Select(c => c.ID).ToList();

        /// <summary>All cars of the job repaired (checked live, used when the job is validated).</summary>
        public static bool JobCarsRepaired(Job job)
        {
            foreach (TrainCar car in TrainCar.ExtractTrainCars(JobCars(job)))
            {
                if (car != null && Condition(car) < RepairedCondition)
                    return false;
            }

            return true;
        }

        /// <summary>true = all cars of the task are repaired (checked at most twice per second).</summary>
        public static bool CarsRepaired(TransportTask task)
        {
            float now = Time.unscaledTime;

            if (gateCheckTime.TryGetValue(task, out float last) && now - last < 0.5f && gateResult.TryGetValue(task, out bool cached))
                return cached;

            bool repaired = true;
            List<Car>? cars = transportCarsField?.GetValue(task) as List<Car>;

            if (cars != null)
            {
                foreach (TrainCar car in TrainCar.ExtractTrainCars(cars))
                {
                    if (Condition(car) < RepairedCondition)
                    {
                        repaired = false;
                        break;
                    }
                }
            }

            if (gateCheckTime.Count > 200)
            {
                gateCheckTime.Clear();
                gateResult.Clear();
            }

            gateCheckTime[task] = now;
            gateResult[task] = repaired;
            return repaired;
        }

        // =====================================================
        // DEFINITIONS (vanilla types)
        // =====================================================
        private static JobChainController NewChain(StationController from, StationController to, List<TrainCar> cars, string code)
        {
            GameObject go = new GameObject($"ChainJob[TSR-{code}]: {from.logicStation.ID} - {to.logicStation.ID}");
            go.transform.SetParent(from.transform);

            return new JobChainController(go) { carsForJobChain = TrainCar.ExtractLogicCars(cars) };
        }

        private static bool Finalize(JobChainController chain, SpecialJobBookletColorKind kind)
        {
            // the job ID code is applied by the FinalizeSetupAndGenerateFirstJob patch (also when PJRework finalizes)
            if (kind != SpecialJobBookletColorKind.None)
                pendingKinds[chain] = kind;

            if (deferFinalize)
            {
                lastChain = chain;
                return true;
            }

            try
            {
                chain.FinalizeSetupAndGenerateFirstJob(false);
                return chain.currentJobInChain != null;
            }
            catch (Exception ex)
            {
                Main.LogWarning("DAMAGED JOBS -> finalizing chain failed: " + ex.Message);

                try
                {
                    chain.DestroyChain();
                }
                catch
                {
                    try
                    {
                        UnityEngine.Object.Destroy(chain.jobChainGO);
                    }
                    catch
                    {
                        // nothing left to clean up
                    }
                }

                return false;
            }
            finally
            {
                pendingKinds.Remove(chain);
            }
        }

        /// <summary>In-station job (IS): vanilla EmptyHaul from track to track inside the same station.</summary>
        private static StaticEmptyHaulJobDefinition InStationDefinition(JobChainController chain, StationController station,
            Track start, Track destination, List<TrainCar> cars, float timeLimit, float wage)
        {
            StaticEmptyHaulJobDefinition definition = chain.jobChainGO.AddComponent<StaticEmptyHaulJobDefinition>();
            List<CargoType> cargo = cars.Where(IsLoaded).Select(c => c.logicCar.CurrentCargoTypeInCar).Distinct().ToList();

            definition.PopulateBaseJobDefinition(station.logicStation, timeLimit, wage,
                new StationsChainData(station.stationInfo.YardID, station.stationInfo.YardID),
                Licenses(JobType.ShuntingUnload, cargo, cars));

            definition.startingTrack = start;
            definition.destinationTrack = destination;
            definition.carsToTransport = TrainCar.ExtractLogicCars(cars);
            return definition;
        }

        private static StaticTransportJobDefinition TransportDefinition(JobChainController chain, StationController from,
            StationController to, Track start, Track destination, List<TrainCar> cars, float timeFactor, float payFactor)
        {
            List<CargoType> cargoPerCar = cars.Select(c => c.logicCar.CurrentCargoTypeInCar).ToList();
            float distance = JobPaymentCalculator.GetDistanceBetweenStations(from, to);
            float time = JobPaymentCalculator.CalculateHaulBonusTimeLimit(distance, false) * timeFactor;
            float wage = JobPaymentCalculator.CalculateJobPayment(JobType.Transport, distance, PaymentData(cars, cargoPerCar)) * payFactor;

            StaticTransportJobDefinition definition = chain.jobChainGO.AddComponent<StaticTransportJobDefinition>();
            definition.PopulateBaseJobDefinition(from.logicStation, time, wage,
                new StationsChainData(from.stationInfo.YardID, to.stationInfo.YardID),
                Licenses(JobType.Transport, cargoPerCar.Distinct().ToList(), cars));

            definition.startingTrack = start;
            definition.destinationTrack = destination;
            definition.carsToTransport = TrainCar.ExtractLogicCars(cars);
            definition.transportedCargoPerCar = cargoPerCar;
            definition.cargoAmountPerCar = cars.Select(_ => 1f).ToList();
            definition.forceCorrectCargoStateOnCars = false;
            return definition;
        }

        private static StaticEmptyHaulJobDefinition EmptyHaulDefinition(JobChainController chain, StationController from,
            StationController to, Track start, Track destination, List<TrainCar> cars, float timeFactor, float payFactor)
        {
            float distance = JobPaymentCalculator.GetDistanceBetweenStations(from, to);
            float time = JobPaymentCalculator.CalculateHaulBonusTimeLimit(distance, false) * timeFactor;
            float wage = JobPaymentCalculator.CalculateJobPayment(JobType.EmptyHaul, distance, PaymentData(cars, new List<CargoType>())) * payFactor;

            StaticEmptyHaulJobDefinition definition = chain.jobChainGO.AddComponent<StaticEmptyHaulJobDefinition>();
            definition.PopulateBaseJobDefinition(from.logicStation, time, wage,
                new StationsChainData(from.stationInfo.YardID, to.stationInfo.YardID),
                Licenses(JobType.EmptyHaul, new List<CargoType>(), cars));

            definition.startingTrack = start;
            definition.destinationTrack = destination;
            definition.carsToTransport = TrainCar.ExtractLogicCars(cars);
            return definition;
        }

        private static StaticShuntingUnloadJobDefinition UnloadDefinition(JobChainController chain, StationController from,
            StationController to, Track start, List<CarsPerTrack> destinations, WarehouseMachine machine, List<TrainCar> cars,
            float timeFactor, float payFactor)
        {
            List<CargoType> cargoPerCar = cars.Select(c => c.logicCar.CurrentCargoTypeInCar).ToList();
            float time = JobPaymentCalculator.CalculateShuntingBonusTimeLimit(destinations.Count, false) * timeFactor;
            float wage = JobPaymentCalculator.CalculateJobPayment(JobType.ShuntingUnload, 500f, PaymentData(cars, cargoPerCar)) * payFactor;

            StaticShuntingUnloadJobDefinition definition = chain.jobChainGO.AddComponent<StaticShuntingUnloadJobDefinition>();
            definition.PopulateBaseJobDefinition(to.logicStation, time, wage,
                new StationsChainData(from.stationInfo.YardID, to.stationInfo.YardID),
                Licenses(JobType.ShuntingUnload, cargoPerCar.Distinct().ToList(), cars));

            definition.startingTrack = start;
            definition.carsPerDestinationTrack = destinations;
            definition.unloadData = CargoData(cars, cargoPerCar);
            definition.unloadMachine = machine;
            definition.forceCorrectCargoStateOnCars = false;
            return definition;
        }

        private static List<CarsPerCargoType> CargoData(List<TrainCar> cars, List<CargoType> cargoPerCar)
        {
            List<CarsPerCargoType> result = new List<CarsPerCargoType>();

            foreach (IGrouping<CargoType, int> grp in Enumerable.Range(0, cars.Count).GroupBy(i => cargoPerCar[i]))
            {
                List<Car> logic = grp.Select(i => cars[i].logicCar).ToList();
                result.Add(new CarsPerCargoType(grp.Key, logic, logic.Count));
            }

            return result;
        }

        private static JobLicenses Licenses(JobType type, List<CargoType> cargo, List<TrainCar> cars)
        {
            LicenseManager manager = LicenseManager.Instance;
            List<JobLicenseType_v2> list = manager.GetRequiredLicensesForJobType(type).ToList();
            list.AddRange(manager.GetRequiredLicensesForCarTypes(cars.Select(c => c.carLivery.parentType).Distinct().ToList()));

            if (cargo.Count > 0)
                list.AddRange(manager.GetRequiredLicensesForCargoTypes(cargo));

            JobLicenseType_v2 lengthLicense = manager.GetRequiredLicenseForNumberOfTransportedCars(cars.Count);

            if (lengthLicense != null)
                list.Add(lengthLicense);

            return JobLicenseType_v2.ListToFlags(list);
        }

        private static PaymentCalculationData PaymentData(List<TrainCar> cars, List<CargoType> cargo)
        {
            Dictionary<TrainCarLivery, int> liveries = new Dictionary<TrainCarLivery, int>();

            foreach (TrainCar car in cars)
                liveries[car.carLivery] = liveries.TryGetValue(car.carLivery, out int n) ? n + 1 : 1;

            Dictionary<CargoType, int> cargoes = new Dictionary<CargoType, int>();

            foreach (CargoType type in cargo)
            {
                if (type != CargoType.None)
                    cargoes[type] = cargoes.TryGetValue(type, out int n) ? n + 1 : 1;
            }

            return new PaymentCalculationData(liveries, cargoes);
        }

        // =====================================================
        // STATION DATA
        // =====================================================
        private sealed class YardData
        {
            public StationController Station = null!;
            public List<Track> Storage = new List<Track>();
            public List<Track> In = new List<Track>();
            public List<Track> Out = new List<Track>();
        }

        private static YardData GetYard(StationController station)
        {
            YardData data = new YardData { Station = station };
            Yard? yard = station.logicStation?.yard;

            if (yard == null)
                return data;

            if (yard.StorageTracks != null) data.Storage.AddRange(yard.StorageTracks.Where(t => t != null));
            if (yard.TransferInTracks != null) data.In.AddRange(yard.TransferInTracks.Where(t => t != null));
            if (yard.TransferOutTracks != null) data.Out.AddRange(yard.TransferOutTracks.Where(t => t != null));
            return data;
        }

        private static Dictionary<Track, StationController> TrackStations()
        {
            Dictionary<Track, StationController> map = new Dictionary<Track, StationController>();

            foreach (StationController station in StationController.allStations)
            {
                Yard? yard = station?.logicStation?.yard;

                if (station == null || yard == null)
                    continue;

                foreach (List<Track>? list in new[] { yard.StorageTracks, yard.TransferInTracks, yard.TransferOutTracks })
                {
                    if (list == null)
                        continue;

                    foreach (Track track in list)
                    {
                        if (track != null)
                            map[track] = station;
                    }
                }

                // loading tracks (warehouse machines)
                if (yard.WarehouseMachines != null)
                {
                    foreach (WarehouseMachine machine in yard.WarehouseMachines)
                    {
                        if (machine?.WarehouseTrack != null)
                            map[machine.WarehouseTrack] = station;
                    }
                }
            }

            return map;
        }

        private static bool Accepts(StationController station, List<CargoType> cargo)
        {
            var groups = station?.proceduralJobsRuleset?.inputCargoGroups;
            return groups != null && cargo.All(ct => groups.Any(g => g.cargoTypes != null && g.cargoTypes.Contains(ct)));
        }

        private static bool CanLoad(StationController station, List<TrainCar> cars)
        {
            var groups = station?.proceduralJobsRuleset?.outputCargoGroups;

            if (station == null || groups == null)
                return false;

            foreach (var group in groups)
            {
                if (group.cargoTypes == null)
                    continue;

                foreach (CargoType cargo in group.cargoTypes)
                {
                    if (cargo == CargoType.None)
                        continue;

                    if (cars.All(c => c.carLivery != null && cargo.ToV2().IsLoadableOnCarType(c.carLivery.parentType)) &&
                        (station.logicStation.yard.GetWarehouseMachinesThatSupportCargoTypes(new List<CargoType> { cargo })?.Count ?? 0) > 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static float Length(List<TrainCar> cars) =>
            CarSpawner.Instance.GetTotalTrainCarsLength(TrainCar.ExtractLogicCars(cars), true);

        /// <summary>Smallest free track that fits (in the order of the lists), booked for this scan.</summary>
        private static Track? PickTrack(float length, List<Track> exclude, params List<Track>[] lists)
        {
            YardTracksOrganizer organizer = YardTracksOrganizer.Instance;

            foreach (List<Track> list in lists)
            {
                Track? best = null;
                float bestFree = float.MaxValue;

                foreach (Track track in list)
                {
                    if (track == null || exclude.Contains(track))
                        continue;

                    float free = (float)organizer.GetFreeSpaceOnTrack(track);

                    if (booked.TryGetValue(track, out float already))
                        free -= already;

                    if (free < length || free >= bestFree)
                        continue;

                    best = track;
                    bestFree = free;
                }

                if (best != null)
                {
                    booked[best] = (booked.TryGetValue(best, out float b) ? b : 0f) + length;
                    return best;
                }
            }

            return null;
        }

        private static void Unbook(Track track, float length)
        {
            if (booked.TryGetValue(track, out float b))
                booked[track] = Mathf.Max(0f, b - length);
        }

        private static void Unbook(List<CarsPerTrack> destinations)
        {
            foreach (CarsPerTrack cpt in destinations)
            {
                if (cpt?.track != null && cpt.cars != null)
                    Unbook(cpt.track, CarSpawner.Instance.GetTotalTrainCarsLength(cpt.cars, true));
            }
        }

        // =====================================================
        // TASK HELPERS
        // =====================================================
        private static List<Car> JobCars(Job job)
        {
            HashSet<Car> result = new HashSet<Car>();

            foreach (Task leaf in LeafTasks(job))
            {
                List<Car>? cars = GetCars(leaf);

                if (cars == null)
                    continue;

                foreach (Car car in cars)
                {
                    if (car != null)
                        result.Add(car);
                }
            }

            return result.ToList();
        }

        private static List<Car>? GetCars(Task task)
        {
            if (task is TransportTask)
                return transportCarsField?.GetValue(task) as List<Car>;

            if (task is WarehouseTask warehouseTask)
                return warehouseTask.cars;

            return null;
        }

        private static IEnumerable<Task> LeafTasks(Job job)
        {
            if (job?.tasks == null)
                yield break;

            foreach (Task task in job.tasks)
            {
                foreach (Task leaf in Leaves(task, 0))
                    yield return leaf;
            }
        }

        private static IEnumerable<Task> Leaves(Task? task, int depth)
        {
            if (task == null || depth > 10)
                yield break;

            object? children = null;

            if (task is SequentialTasks)
                children = nestedTasksField?.GetValue(task);
            else if (task is ParallelTasks)
                children = parallelTasksField?.GetValue(task);

            if (children is IEnumerable enumerable)
            {
                foreach (object child in enumerable)
                {
                    if (child is Task childTask)
                    {
                        foreach (Task leaf in Leaves(childTask, depth + 1))
                            yield return leaf;
                    }
                }

                yield break;
            }

            yield return task;
        }
    }

    // =========================================================
    // DELETING NOT YET ACCEPTED JOBS
    // The class name contains "Trash" on purpose: PersistentJobsRework only lets available jobs expire
    // when a "Trash" / "JobAbandoner" / "JobValidator" caller is on the stack.
    // =========================================================
    internal static class DamagedJobTrash
    {
        private static readonly MethodInfo? destroyOverview = AccessTools.Method(typeof(JobOverview), "DestroyJobOverview");

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Expire(Job job)
        {
            try
            {
                if (job == null || job.State != JobState.Available)
                    return false;

                job.ExpireJob();
                return job.State != JobState.Available;
            }
            catch (Exception ex)
            {
                Main.LogWarning($"DAMAGED JOBS -> deleting {job?.ID} failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>All job overviews in the world (tracked via JobOverview.Start – no FindObjectsOfType).</summary>
        public static readonly HashSet<JobOverview> Overviews = new HashSet<JobOverview>();

        /// <summary>Printed overviews of jobs that don't exist anymore.</summary>
        public static void RemoveOrphanedOverviews()
        {
            try
            {
                foreach (JobOverview overview in Overviews.ToList())
                {
                    if (overview == null)
                    {
                        Overviews.Remove(overview!);
                        continue;
                    }

                    if (overview.job == null || overview.job.State != JobState.Expired)
                        continue;

                    Overviews.Remove(overview);

                    if (destroyOverview != null)
                        destroyOverview.Invoke(overview, null);
                    else
                        UnityEngine.Object.Destroy(overview.gameObject);
                }

                Overviews.RemoveWhere(o => o == null);
            }
            catch (Exception ex)
            {
                Main.LogWarning("DAMAGED JOBS -> removing job overviews failed: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(JobOverview), "Start")]
    public static class JobOverviewTrackingPatch
    {
        static void Postfix(JobOverview __instance)
        {
            if (__instance != null)
                DamagedJobTrash.Overviews.Add(__instance);
        }
    }

    // =========================================================
    // INSTANDSETZUNG: the parking task only finishes when the cars are repaired
    // =========================================================
    [HarmonyPatch(typeof(TransportTask), nameof(TransportTask.UpdateTaskState))]
    public static class OverhaulTaskGatePatch
    {
        static bool Prefix(TransportTask __instance, ref TaskState __result)
        {
            Job? job = __instance?.Job;

            if (__instance == null || job == null || !DamagedJobSystem.IsOverhaulJob(job))
                return true;

            if (!__instance.IsLastTask)
            {
                // couple / shunt to the pit stop: done as soon as the cars are repaired (e.g. the car already stood
                // in the pit stop when the job was taken, or it was moved on before the task was checked)
                if (__instance.state != TaskState.Done && DamagedJobSystem.CarsRepaired(__instance))
                {
                    __instance.state = TaskState.Done;
                    __result = TaskState.Done;
                    return false;
                }

                return true;
            }

            if (DamagedJobSystem.CarsRepaired(__instance))
                return true;

            // not (or no longer) repaired: the parking task stays / goes back to in progress
            if (__instance.state == TaskState.Done)
                __instance.state = TaskState.InProgress;

            __result = __instance.state;
            return false;
        }
    }

    /// <summary>Validating a maintenance job: every car must be repaired at this moment (damaged again = not finished).</summary>
    [HarmonyPatch(typeof(Job), nameof(Job.ValidateJobFinished))]
    public static class OverhaulValidatePatch
    {
        static bool Prefix(Job __instance, ref bool __result)
        {
            if (__instance == null || !DamagedJobSystem.IsOverhaulJob(__instance))
                return true;

            if (DamagedJobSystem.JobCarsRepaired(__instance))
                return true;

            __result = false;
            Main.Log($"DAMAGED JOBS -> {__instance.ID}: not every car is repaired, job can't be validated");
            return false;
        }
    }

    // =========================================================
    // MAINTENANCE: license "Freight Car Maintenance" / "Passenger Car Maintenance" required
    // (both need Manual Service to be bought). Job.requiredLicenses can only hold job licenses, ours are general
    // licenses. So the job validator's license check is extended for maintenance jobs: the job being validated is
    // remembered, and IsLicensedForJob returns false without the license -> vanilla prints the "missing license"
    // report and the job is not taken (host and clients, ID based).
    // =========================================================
    [HarmonyPatch(typeof(JobValidator), nameof(JobValidator.ProcessJobOverview))]
    public static class OverhaulLicenseContextPatch
    {
        [ThreadStatic]
        internal static Job? CurrentJob;

        [HarmonyPriority(Priority.First)]
        static void Prefix(JobOverview jobOverview)
        {
            CurrentJob = jobOverview != null ? jobOverview.job : null;
        }

        static void Finalizer()
        {
            CurrentJob = null;
        }
    }

    [HarmonyPatch(typeof(LicenseManager), nameof(LicenseManager.IsLicensedForJob))]
    public static class OverhaulLicensePatch
    {
        static void Postfix(ref bool __result)
        {
            if (!__result)
                return;

            Job? job = OverhaulLicenseContextPatch.CurrentJob;

            if (job == null || !DamagedJobSystem.IsOverhaulJob(job))
                return;

            TsrLicenseV2? required = MaintenanceLicenses.RequiredFor(DamagedJobSystem.JobCarIds(job));

            if (required == null ? DamagedJobSystem.HasManualService() : MaintenanceLicenses.IsAcquired(required))
                return;

            __result = false;
            Main.Log($"DAMAGED JOBS -> {job.ID}: maintenance needs the license {(required != null ? required.id : "Manual Service")}");
        }
    }

    // =========================================================
    // DAMAGE EVENT: remember the car, its condition is checked ~10 s later (only these cars, no polling)
    // =========================================================
    /// <summary>A job chain is finished (last job handed in): follow-up job for the damaged cars (see OnChainCompleted).</summary>
    [HarmonyPatch(typeof(JobChainController), "OnLastJobInChainCompleted")]
    public static class DamagedJobsChainCompletedPatch
    {
        static void Postfix(JobChainController __instance, Job lastJobInChain)
        {
            try
            {
                DamagedJobSystem.OnChainCompleted(__instance, lastJobInChain);
            }
            catch (Exception ex)
            {
                Main.LogWarning("DAMAGED JOBS -> follow-up job failed: " + ex.Message);
            }
        }
    }

    [HarmonyPatch(typeof(DV.Damage.CarDamageModel), nameof(DV.Damage.CarDamageModel.DamageCar))]
    public static class DamagedJobsDamageEventPatch
    {
        static void Postfix(DV.Damage.CarDamageModel __instance)
        {
            if (__instance != null)
                DamagedJobSystem.NotifyDamaged(__instance.trainCar);
        }
    }

    // =========================================================
    // OPTIONAL PERSISTENTJOBSREWORK + CIRCLEHAULS COMPATIBILITY (reflection only, no hard dependency)
    // =========================================================
    public static class DamagedJobsPersistentJobsCompat
    {
        private const string ModId = "PersistentJobsRework";
        private const string CircleHaulsId = "CircleHauls";

        private static bool patched;
        private static FieldInfo? groupCarsField;
        private static FieldInfo? groupStationField;
        private static FieldInfo? groupTrackField;

        // PJRework calls two Try* methods per group – TSR only tries once per group
        private static readonly HashSet<object> handledGroups = new HashSet<object>();

        /// <summary>PersistentJobsRework is installed and its refill hook is active.</summary>
        public static bool Active { get; private set; }

        public static void TryPatch()
        {
            if (patched)
                return;

            patched = true;

            Harmony harmony = new Harmony(Main.ModId + ".DamagedJobs.Compat");
            PatchPersistentJobs(harmony);
            PatchCircleHauls(harmony);
        }

        private static void PatchPersistentJobs(Harmony harmony)
        {
            UnityModManager.ModEntry mod = UnityModManager.FindMod(ModId);

            if (mod == null || !mod.Enabled || mod.Assembly == null)
                return;

            try
            {
                Assembly assembly = mod.Assembly;
                int count = 0;

                Type? refill = assembly.GetType("PersistentJobsRework.JobRefill");

                if (refill != null)
                {
                    foreach (string name in new[] { "TryUnloadHere", "TryHaulLoaded", "TryLoadHere", "TryEmptyHaul" })
                    {
                        MethodInfo? method = AccessTools.Method(refill, name);

                        if (method == null)
                            continue;

                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(DamagedJobsPersistentJobsCompat), nameof(RefillPrefix)));
                        count++;
                    }

                    MethodInfo? collect = AccessTools.Method(refill, "CollectGroups");

                    if (collect != null)
                        harmony.Patch(collect, prefix: new HarmonyMethod(typeof(DamagedJobsPersistentJobsCompat), nameof(CollectGroupsPrefix)));

                    Active = count > 0;
                }

                Type? lhRedirect = assembly.GetType("PersistentJobsRework.LhRedirect");
                MethodInfo? redirect = lhRedirect != null ? AccessTools.Method(lhRedirect, "Redirect") : null;

                if (redirect != null)
                {
                    harmony.Patch(redirect, prefix: new HarmonyMethod(typeof(DamagedJobsPersistentJobsCompat), nameof(RedirectPrefix)));
                    count++;
                }

                Type? alternatives = assembly.GetType("PersistentJobsRework.AlternativeTracks");
                MethodInfo? allowed = alternatives != null ? AccessTools.Method(alternatives, "IsAllowedAlternative") : null;

                if (allowed != null)
                {
                    harmony.Patch(allowed, prefix: new HarmonyMethod(typeof(DamagedJobsPersistentJobsCompat), nameof(AlternativePrefix)));
                    count++;
                }

                Type? trackAssignment = assembly.GetType("PersistentJobsRework.TrackAssignment");
                MethodInfo? assign = trackAssignment != null ? AccessTools.Method(trackAssignment, "Assign") : null;

                if (assign != null)
                {
                    harmony.Patch(assign, prefix: new HarmonyMethod(typeof(DamagedJobsPersistentJobsCompat), nameof(AssignPrefix)));
                    count++;
                }

                Main.Log($"DAMAGED JOBS -> PersistentJobsRework compatibility active ({count} hook(s))");
            }
            catch (Exception ex)
            {
                Main.LogWarning("DAMAGED JOBS -> PersistentJobsRework compatibility failed: " + ex.Message);
            }
        }

        private static void PatchCircleHauls(Harmony harmony)
        {
            UnityModManager.ModEntry mod = UnityModManager.FindMod(CircleHaulsId);

            if (mod == null || !mod.Enabled || mod.Assembly == null)
                return;

            try
            {
                Type? integration = mod.Assembly.GetType("CircleHauls.Jobs.VanillaIntegration");
                int count = 0;

                MethodInfo? convert = integration != null ? AccessTools.Method(integration, "TryConvertNewChain") : null;

                if (convert != null)
                {
                    harmony.Patch(convert, prefix: new HarmonyMethod(typeof(DamagedJobsPersistentJobsCompat), nameof(CircleHaulConvertPrefix)));
                    count++;
                }

                MethodInfo? afterLoad = integration != null ? AccessTools.Method(integration, "TryReplaceAfterShuntingLoad") : null;

                if (afterLoad != null)
                {
                    harmony.Patch(afterLoad, prefix: new HarmonyMethod(typeof(DamagedJobsPersistentJobsCompat), nameof(CircleHaulAfterLoadPrefix)));
                    count++;
                }

                Main.Log($"DAMAGED JOBS -> CircleHauls compatibility active ({count} hook(s))");
            }
            catch (Exception ex)
            {
                Main.LogWarning("DAMAGED JOBS -> CircleHauls compatibility failed: " + ex.Message);
            }
        }

        // ---------------- PersistentJobsRework ----------------

        private static void CollectGroupsPrefix()
        {
            handledGroups.Clear();
        }

        /// <summary>
        /// PJRework refill (full game hour): a group with damaged cars gets its job from TSR. TSR builds the chain,
        /// PJRework finalizes it as usual (manually spawned cars, plates, log).
        /// </summary>
        private static bool RefillPrefix(object g, ref string kind, ref JobChainController? __result)
        {
            try
            {
                if (!DamagedJobSystem.Enabled || g == null)
                    return true;

                Type type = g.GetType();
                groupCarsField ??= AccessTools.Field(type, "Cars");
                groupStationField ??= AccessTools.Field(type, "Station");
                groupTrackField ??= AccessTools.Field(type, "Track");

                if (!(groupCarsField?.GetValue(g) is List<TrainCar> cars) || cars.Count == 0)
                    return true;

                // these cars get a new job now: first threshold (chance)
                DamagedJobSystem.RollSoftDamage(cars);

                // manually spawned cars: TSR builds the job too, PJRework converts them to job cars when it finalizes
                if (!cars.Any(DamagedJobSystem.IsDamaged))
                    return true;

                __result = null;

                // second Try* call for the same group: TSR already decided
                if (!handledGroups.Add(g))
                {
                    kind = "none - damaged cars (TrainServiceRework)";
                    return false;
                }

                StationController? station = groupStationField?.GetValue(g) as StationController;
                Track? track = groupTrackField?.GetValue(g) as Track;
                JobChainController? chain = DamagedJobSystem.BuildForRefill(station, track, cars, out string result);

                if (chain != null)
                {
                    __result = chain;
                    kind = "TrainServiceRework: " + result;
                }
                else
                {
                    kind = "none - damaged cars, TrainServiceRework: " + result;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>PJRework moves LH destinations from S to I – not for our in-station jobs (IS).</summary>
        private static bool RedirectPrefix(StaticEmptyHaulJobDefinition def)
        {
            return !IsInStation(def?.chainData);
        }

        /// <summary>PJRework bundles FH/LH on the same destination track – not for our in-station jobs (IS).</summary>
        private static bool AssignPrefix(Job job)
        {
            return !IsInStation(job?.chainData);
        }

        /// <summary>
        /// PJRework alternative tracks: it treats SO jobs like an unload (only S alternatives). For our in-station jobs
        /// (SO / IS) an alternative track of the same kind as the assigned one is allowed: S for S, I/O for I/O.
        /// </summary>
        private static bool AlternativePrefix(Track assigned, Track used, Job job, ref bool sameGroup, ref bool __result)
        {
            try
            {
                if (job == null || assigned == null || used == null)
                    return true;

                SpecialJobBookletColorKind kind = SpecialJobBookletColorHelper.GetServiceKind(job.ID);

                if (kind != SpecialJobBookletColorKind.ShuntingOut && kind != SpecialJobBookletColorKind.Overhaul &&
                    kind != SpecialJobBookletColorKind.Maintenance && kind != SpecialJobBookletColorKind.Sorting)
                    return true;

                sameGroup = false;
                __result = false;

                if (assigned.ID.yardId != used.ID.yardId)
                    return false;

                Yard? yard = StationController.GetStationByYardID(assigned.ID.yardId)?.logicStation?.yard;

                if (yard == null)
                    return false;

                bool IsS(Track t) => yard.StorageTracks?.Contains(t) ?? false;
                bool IsIO(Track t) => (yard.TransferInTracks?.Contains(t) ?? false) || (yard.TransferOutTracks?.Contains(t) ?? false);

                if (IsS(assigned))
                    __result = IsS(used);
                else if (IsIO(assigned))
                    __result = IsIO(used);

                // same group = same sub yard letter (e.g. E1O / E2O)
                string a = assigned.ID.TrackPartOnly ?? string.Empty;
                string u = used.ID.TrackPartOnly ?? string.Empty;
                sameGroup = a.Length > 0 && u.Length > 0 && a[0] == u[0];
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsInStation(StationsChainData? data)
        {
            return data != null &&
                   !string.IsNullOrEmpty(data.chainOriginYardId) &&
                   string.Equals(data.chainOriginYardId, data.chainDestinationYardId, StringComparison.Ordinal);
        }

        // ---------------- CircleHauls ----------------

        /// <summary>CircleHauls converts new FH/LH chains into circle hauls – never ours and never with damaged cars.</summary>
        private static bool CircleHaulConvertPrefix(JobChainController chain)
        {
            try
            {
                if (chain == null || !DamagedJobSystem.Enabled)
                    return true;

                if (DamagedJobSystem.IsOwnPendingChain(chain))
                    return false;

                List<Car>? cars = chain.carsForJobChain;
                return cars == null || !TrainCar.ExtractTrainCars(cars).Any(DamagedJobSystem.IsDamaged);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>CircleHauls replaces the FH after a loading job – not when a car is damaged.</summary>
        private static bool CircleHaulAfterLoadPrefix(StaticJobDefinition shuntingLoad)
        {
            try
            {
                if (!DamagedJobSystem.Enabled || !(shuntingLoad is StaticShuntingLoadJobDefinition load) || load.carsPerStartingTrack == null)
                    return true;

                List<Car> cars = load.carsPerStartingTrack.Where(t => t?.cars != null).SelectMany(t => t.cars).ToList();
                return !TrainCar.ExtractTrainCars(cars).Any(DamagedJobSystem.IsDamaged);
            }
            catch
            {
                return true;
            }
        }
    }
}
