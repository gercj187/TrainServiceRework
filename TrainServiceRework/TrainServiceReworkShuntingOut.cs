// File: TrainServiceReworkShuntingOut.cs
//
// Own shunting jobs of TrainServiceRework (vanilla tasks, own definition, own savegame key, own booklet):
//
//   SO  SHUNTING OUT / AUSRANGIEREN  – set damaged cars out of a train (see below)
//   SM  MAINTENANCE / INSTANDSETZUNG – shunt damaged cars to the pit stop track, repair them to 100 %, park them
//   SS  SHUNTING SORTING / SORTIEREN – collect damaged cars of several tracks into one train for a maintenance haul
//       Sequential: Parallel(last)[ Transport pick-up i -> end track ]; when handed in the DH / MH is created
//       Sequential: Transport all start->start (couple) | Transport all -> service track | Transport all -> park (last)
//       The last task (and the validation) only finishes when every car is repaired.
//
// AUSRANGIEREN / BAD ORDER SET-OUT (SO) – own shunting job made of vanilla tasks:
//
//   Sequential
//   ├─ Transport  all cars       original track -> original track   (pick up / couple)
//   └─ Parallel (last)
//      ├─ Transport damaged cars -> set-out track                   (sort out, secure)
//      └─ Transport other cars   -> original track                  (prepare for departure)
//
// * Vanilla tasks only -> the multiplayer mod syncs the job like any other (task tree is generic). The job type is
//   ShuntingUnload (a vanilla shunting type, so nothing in the game trips over an unknown type), the job is recognised
//   by its ID (XX-SO-00).
// * Booklet: cover, overview (all cars, damaged cars marked red), couple, sort out, prepare for departure, validate.
//   Built from the job's task data -> identical on host and clients.
// * Saving: own savegame key (like CircleHauls / PassengerJobs). If TSR is removed, the vanilla part of the savegame
//   stays readable – the SO jobs are simply gone, the cars stay.

using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using DV.Booklets;
using DV.Localization;
using DV.Logic.Job;
using DV.RenderTextureSystem.BookletRender;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;

namespace TrainServiceRework
{
    // =========================================================
    // JOB DEFINITION
    // =========================================================
    public class ShuntingOutJobDefinition : StaticJobDefinition
    {
        public static readonly HashSet<ShuntingOutJobDefinition> All = new HashSet<ShuntingOutJobDefinition>();

        public const int ModeShuntingOut = 0;
        public const int ModeMaintenance = 1;
        public const int ModeSorting = 2;

        public JobChainController? Chain;
        public int Mode = ModeShuntingOut;
        public List<Car> AllCars = new List<Car>();
        public List<Car> DamagedCars = new List<Car>();

        /// <summary>SS: pick-ups (cars per start track, in the order of AllCars).</summary>
        public List<CarsPerTrack> Pickups = new List<CarsPerTrack>();

        /// <summary>SS: yard ID of the repair station the maintenance haul goes to afterwards.</summary>
        public string TargetYardId = string.Empty;

        /// <summary>SO: track the train stands on. SM: start track.</summary>
        public Track? OriginalTrack;

        /// <summary>SO: set-out track of the damaged cars. SM: pit stop (service) track.</summary>
        public Track? SetOutTrack;

        /// <summary>SO: = OriginalTrack. SM: parking track after the repair.</summary>
        public Track? EndTrack;

        public List<Car> RemainingCars => AllCars.Where(c => !DamagedCars.Contains(c)).ToList();

        /// <summary>The job made from this definition (null before it is generated).</summary>
        public Job? GeneratedJob => job;

        /// <summary>New payment (also saved with the definition).</summary>
        public void SetWage(float wage)
        {
            initialWage = wage;
        }

        public void Setup(JobChainController chain, List<Car> allCars, List<Car> damagedCars, Track originalTrack, Track setOutTrack,
            int mode = ModeShuntingOut, Track? endTrack = null)
        {
            Chain = chain;
            Mode = mode;
            AllCars = allCars;
            DamagedCars = damagedCars;
            OriginalTrack = originalTrack;
            SetOutTrack = setOutTrack;
            EndTrack = endTrack ?? originalTrack;
        }

        /// <summary>SS: pick up the cars of several tracks and park them together on the end track.</summary>
        public void SetupSorting(JobChainController chain, List<CarsPerTrack> pickups, Track endTrack, string targetYardId)
        {
            Chain = chain;
            Mode = ModeSorting;
            Pickups = pickups;
            AllCars = pickups.SelectMany(p => p.cars).ToList();
            DamagedCars = AllCars.ToList();
            OriginalTrack = pickups.Count > 0 ? pickups[0].track : endTrack;
            SetOutTrack = endTrack;
            EndTrack = endTrack;
            TargetYardId = targetYardId ?? string.Empty;
        }

        private void Awake()
        {
            All.Add(this);
        }

        private void OnDestroy()
        {
            All.Remove(this);
        }

        protected override void GenerateJob(Station jobOriginStation, float timeLimit, float initialWage, string forcedJobId, JobLicenses requiredLicenses)
        {
            if (Mode == ModeMaintenance)
            {
                GenerateMaintenanceJob(jobOriginStation, timeLimit, initialWage, forcedJobId, requiredLicenses);
                return;
            }

            if (Mode == ModeSorting)
            {
                GenerateSortingJob(jobOriginStation, timeLimit, initialWage, forcedJobId, requiredLicenses);
                return;
            }

            List<Car> remaining = RemainingCars;

            if (AllCars.Count == 0 || DamagedCars.Count == 0 || remaining.Count == 0 || OriginalTrack == null || SetOutTrack == null)
            {
                Main.LogWarning("SHUNTING OUT -> incomplete definition, job not created");
                return;
            }

            // cargo is not checked (null): the cars keep whatever they have
            Task pickup = JobsGenerator.CreateTransportTask(AllCars, OriginalTrack, OriginalTrack, null, false);
            Task setOut = JobsGenerator.CreateTransportTask(DamagedCars, SetOutTrack, OriginalTrack, null, true);
            Task departure = JobsGenerator.CreateTransportTask(remaining, OriginalTrack, OriginalTrack, null, true);

            ParallelTasks split = new ParallelTasks(new List<Task> { setOut, departure }, 0L, true);
            SequentialTasks sequence = new SequentialTasks(new List<Task> { pickup, split }, 0L);

            job = new Job(sequence, JobType.ShuntingUnload, timeLimit, initialWage, chainData, forcedJobId, requiredLicenses);
            jobOriginStation.AddJobToStation(job);
        }

        private void GenerateMaintenanceJob(Station jobOriginStation, float timeLimit, float initialWage, string forcedJobId, JobLicenses requiredLicenses)
        {
            Track? end = EndTrack ?? OriginalTrack;

            if (AllCars.Count == 0 || OriginalTrack == null || SetOutTrack == null || end == null)
            {
                Main.LogWarning("MAINTENANCE -> incomplete definition, job not created");
                return;
            }

            Task pickup = JobsGenerator.CreateTransportTask(AllCars, OriginalTrack, OriginalTrack, null, false);
            Task toService = JobsGenerator.CreateTransportTask(AllCars, SetOutTrack, OriginalTrack, null, false);
            Task park = JobsGenerator.CreateTransportTask(AllCars, end, SetOutTrack, null, true);

            SequentialTasks sequence = new SequentialTasks(new List<Task> { pickup, toService, park }, 0L);

            job = new Job(sequence, JobType.ShuntingUnload, timeLimit, initialWage, chainData, forcedJobId, requiredLicenses);
            jobOriginStation.AddJobToStation(job);
        }

        /// <summary>
        /// SS: Sequential[ Parallel(last)[ Transport pick-up i: start track i -> end track ] ].
        /// When it is completed the maintenance haul (MH) is created from the end track.
        /// </summary>
        private void GenerateSortingJob(Station jobOriginStation, float timeLimit, float initialWage, string forcedJobId, JobLicenses requiredLicenses)
        {
            Track? end = EndTrack;

            if (Pickups.Count == 0 || end == null || Pickups.Any(p => p?.track == null || p.cars == null || p.cars.Count == 0))
            {
                Main.LogWarning("SHUNTING SORTING -> incomplete definition, job not created");
                return;
            }

            Track endTrack = end;
            List<Task> pickups = Pickups
                .Select(p => (Task)JobsGenerator.CreateTransportTask(p.cars, endTrack, p.track, null, true))
                .ToList();

            ParallelTasks all = new ParallelTasks(pickups, 0L, true);
            SequentialTasks sequence = new SequentialTasks(new List<Task> { all }, 0L);

            job = new Job(sequence, JobType.ShuntingUnload, timeLimit, initialWage, chainData, forcedJobId, requiredLicenses);
            jobOriginStation.AddJobToStation(job);

            // the DH / MH afterwards is created when the job is handed in (DamagedJobSystem.OnChainCompleted)
        }

        public override List<TrackReservation> GetRequiredTrackReservations()
        {
            List<TrackReservation> result = new List<TrackReservation>();

            if (Mode == ModeSorting)
            {
                if (EndTrack != null && AllCars.Count > 0)
                    result.Add(new TrackReservation(EndTrack, CarSpawner.Instance.GetTotalCarsLength(AllCars, true)));

                return result;
            }

            if (Mode == ModeMaintenance)
            {
                Track? end = EndTrack;

                if (end != null && end != OriginalTrack && AllCars.Count > 0)
                    result.Add(new TrackReservation(end, CarSpawner.Instance.GetTotalCarsLength(AllCars, true)));

                return result;
            }

            if (SetOutTrack != null && DamagedCars.Count > 0)
                result.Add(new TrackReservation(SetOutTrack, CarSpawner.Instance.GetTotalCarsLength(DamagedCars, true)));

            return result;
        }

        public override JobDefinitionDataBase GetJobDefinitionSaveData()
        {
            return new ShuntingOutJobDefinitionData(
                timeLimitForJob, initialWage, logicStation.ID, chainData.chainOriginYardId, chainData.chainDestinationYardId,
                (int)requiredLicenses,
                GetGuidsFromCars(AllCars), GetGuidsFromCars(DamagedCars),
                OriginalTrack != null ? OriginalTrack.ID.FullID : string.Empty,
                SetOutTrack != null ? SetOutTrack.ID.FullID : string.Empty,
                Mode,
                EndTrack != null ? EndTrack.ID.FullID : string.Empty,
                Pickups.Select(p => p?.track != null ? p.track.ID.FullID : string.Empty).ToArray(),
                Pickups.Select(p => p?.cars?.Count ?? 0).ToArray(),
                TargetYardId ?? string.Empty);
        }
    }

    public class ShuntingOutJobDefinitionData : JobDefinitionDataBase
    {
        public string[] allCarGuids;
        public string[] damagedCarGuids;
        public string originalTrackId;
        public string setOutTrackId;
        public int mode;
        public string endTrackId;

        // SS (shunting sorting): start track and number of cars per pick-up (cars in the order of allCarGuids)
        public string[] pickupTrackIds;
        public int[] pickupCounts;
        public string targetYardId;

        [JsonConstructor]
        public ShuntingOutJobDefinitionData(float timeLimitForJob, float initialWage, string stationId, string originStationId,
            string destinationStationId, int requiredLicenses, string[] allCarGuids, string[] damagedCarGuids,
            string originalTrackId, string setOutTrackId, int mode, string endTrackId,
            string[]? pickupTrackIds = null, int[]? pickupCounts = null, string? targetYardId = null)
            : base(timeLimitForJob, initialWage, stationId, originStationId, destinationStationId, requiredLicenses)
        {
            this.pickupTrackIds = pickupTrackIds ?? new string[0];
            this.pickupCounts = pickupCounts ?? new int[0];
            this.targetYardId = targetYardId ?? string.Empty;
            this.mode = mode;
            this.endTrackId = endTrackId ?? string.Empty;
            this.allCarGuids = allCarGuids ?? new string[0];
            this.damagedCarGuids = damagedCarGuids ?? new string[0];
            this.originalTrackId = originalTrackId ?? string.Empty;
            this.setOutTrackId = setOutTrackId ?? string.Empty;
        }
    }

    public class ShuntingOutChainSaveData : JobChainSaveData
    {
        [JsonConstructor]
        public ShuntingOutChainSaveData(JobDefinitionDataBase[] jobChainData, string[] trainCarGuids, bool jobTaken,
            TaskSaveData[] currentJobTaskData, string firstJobId)
            : base(jobChainData, trainCarGuids, jobTaken, currentJobTaskData, firstJobId)
        {
        }

        public ShuntingOutChainSaveData(JobChainSaveData source)
            : base(source.jobChainData, source.trainCarGuids, source.jobTaken, source.currentJobTaskData, source.firstJobId)
        {
        }
    }

    // =========================================================
    // SAVE / LOAD (own key)
    // =========================================================
    public static class ShuntingOutSave
    {
        private const string SaveKey = "TrainServiceRework_ShuntingOut";

        private static readonly MethodInfo? getCars = AccessTools.Method(typeof(JobSaveManager), "GetCarsFromCarGuids");
        private static readonly MethodInfo? getTrack = AccessTools.Method(typeof(JobSaveManager), "GetYardTrackWithId");
        private static readonly MethodInfo? getStation = AccessTools.Method(typeof(JobSaveManager), "GetStationWithId");
        private static readonly MethodInfo? initBooklet = AccessTools.Method(typeof(JobSaveManager), "InitializeCorrespondingJobBooklet");

        private static JsonSerializer Serializer() => JsonSerializer.Create(JobSaveManager.serializeSettings);

        public static bool IsShuntingOutChain(JobChainController? chain) =>
            chain?.jobChainGO != null && chain.jobChainGO.GetComponent<ShuntingOutJobDefinition>() != null;

        public static void Write(SaveGameData data)
        {
            try
            {
                List<JobChainSaveData> chains = new List<JobChainSaveData>();

                foreach (ShuntingOutJobDefinition definition in ShuntingOutJobDefinition.All.ToList())
                {
                    JobChainController? chain = definition != null ? definition.Chain : null;

                    if (chain == null || chain.currentJobInChain == null)
                        continue;

                    JobState state = chain.currentJobInChain.State;

                    if (state != JobState.Available && state != JobState.InProgress)
                        continue;

                    JobChainSaveData saved = chain.GetJobChainSaveData();

                    if (saved != null)
                        chains.Add(saved is ShuntingOutChainSaveData ? saved : new ShuntingOutChainSaveData(saved));
                }

                JObject root = new JObject
                {
                    ["version"] = 1,
                    ["jobs"] = JToken.FromObject(new JobsSaveGameData(chains.ToArray(), 0f), Serializer())
                };

                data.SetJObject(SaveKey, root);
                Main.Log($"SHUNTING OUT -> {chains.Count} job(s) saved");
            }
            catch (Exception ex)
            {
                Main.LogWarning("SHUNTING OUT -> saving failed: " + ex.Message);
            }
        }

        /// <summary>Our chains are mixed into the vanilla list so that LoadJobChain sees them.</summary>
        public static void Merge(JobsSaveGameData mainJobs)
        {
            try
            {
                SaveGameManager manager = SaveGameManager.Instance;
                JObject? root = manager != null && manager.data != null ? manager.data.GetJObject(SaveKey) : null;
                JToken? token = root?["jobs"];

                if (token == null || mainJobs == null)
                    return;

                JobsSaveGameData? own = token.ToObject<JobsSaveGameData>(Serializer());

                if (own?.jobChains == null || own.jobChains.Length == 0)
                    return;

                JobChainSaveData[] main = mainJobs.jobChains ?? new JobChainSaveData[0];
                HashSet<string> knownIds = new HashSet<string>(main.Where(c => c?.firstJobId != null).Select(c => c.firstJobId));

                List<JobChainSaveData> add = own.jobChains
                    .Where(c => c is ShuntingOutChainSaveData && c.firstJobId != null && knownIds.Add(c.firstJobId))
                    .ToList();

                mainJobs.jobChains = main.Concat(add).ToArray();
                Main.Log($"SHUNTING OUT -> {add.Count} job(s) found in the savegame");
            }
            catch (Exception ex)
            {
                Main.LogWarning("SHUNTING OUT -> reading the savegame failed: " + ex.Message);
            }
        }

        public static GameObject? LoadChain(ShuntingOutChainSaveData save, List<JobBooklet> booklets)
        {
            JobSaveManager manager = JobSaveManager.Instance;
            ShuntingOutJobDefinitionData? data = save.jobChainData?.OfType<ShuntingOutJobDefinitionData>().FirstOrDefault();

            if (manager == null || data == null || getCars == null || getTrack == null || getStation == null)
            {
                Main.LogWarning($"SHUNTING OUT -> {save.firstJobId}: can't be restored");
                return null;
            }

            List<Car>? allCars = getCars.Invoke(manager, new object[] { data.allCarGuids }) as List<Car>;
            List<Car>? damaged = getCars.Invoke(manager, new object[] { data.damagedCarGuids }) as List<Car>;
            Track? original = FindTrack(manager, data.originalTrackId);
            Track? setOut = FindTrack(manager, data.setOutTrackId);
            Station? station = getStation.Invoke(manager, new object[] { data.stationId }) as Station;
            Track? end = string.IsNullOrEmpty(data.endTrackId) ? original : FindTrack(manager, data.endTrackId);

            if (allCars == null || damaged == null || original == null || setOut == null || station == null || end == null)
            {
                Main.LogWarning($"SHUNTING OUT -> {save.firstJobId}: data incomplete, skipped " +
                                $"(cars {allCars?.Count ?? -1}/{data.allCarGuids.Length}, start '{data.originalTrackId}' {(original != null ? "ok" : "missing")}, " +
                                $"middle '{data.setOutTrackId}' {(setOut != null ? "ok" : "missing")}, end '{data.endTrackId}' {(end != null ? "ok" : "missing")}, " +
                                $"station '{data.stationId}' {(station != null ? "ok" : "missing")})");
                return null;
            }

            GameObject go = new GameObject($"[LOADED] ChainJob[TSR-SO]: {data.originStationId}");
            JobChainController chain = new JobChainController(go) { carsForJobChain = allCars };

            ShuntingOutJobDefinition definition = go.AddComponent<ShuntingOutJobDefinition>();
            definition.PopulateBaseJobDefinition(station, data.timeLimitForJob, data.initialWage,
                new StationsChainData(data.originStationId, data.destinationStationId), (JobLicenses)data.requiredLicenses);
            definition.ForceJobId(save.firstJobId);

            if (data.mode == ShuntingOutJobDefinition.ModeSorting)
            {
                List<CarsPerTrack>? pickups = SortingPickups(manager, data, allCars);

                if (pickups == null)
                {
                    Main.LogWarning($"SHUNTING SORTING -> {save.firstJobId}: pick-ups incomplete, skipped");
                    UnityEngine.Object.Destroy(go);
                    return null;
                }

                definition.SetupSorting(chain, pickups, end, data.targetYardId);
            }
            else
            {
                definition.Setup(chain, allCars, damaged, original, setOut, data.mode, end);
            }

            chain.AddJobDefinitionToChain(definition);
            chain.FinalizeSetupAndGenerateFirstJob(true);

            if (chain.currentJobInChain == null)
            {
                Main.LogWarning($"SHUNTING OUT -> {save.firstJobId}: job could not be restored");
                chain.DestroyChain();
                return null;
            }

            if (save.jobTaken)
            {
                JobsManager.Instance.TakeJob(chain.currentJobInChain, true);

                if (save.currentJobTaskData != null)
                {
                    try
                    {
                        chain.currentJobInChain.OverrideTasksStates(save.currentJobTaskData);
                    }
                    catch (Exception ex)
                    {
                        Main.LogWarning($"SHUNTING OUT -> {save.firstJobId}: progress not restored ({ex.Message})");
                    }
                }

                initBooklet?.Invoke(manager, new object[] { chain.currentJobInChain, booklets });
            }

            Main.Log($"SHUNTING OUT -> {save.firstJobId} loaded");
            return go;
        }

        /// <summary>
        /// Track by its full ID. The vanilla lookup only knows station yard tracks (S/I/O/L) – the pit stop track of a
        /// maintenance job (e.g. SM-A-02-P, OWN-A-02-D) is searched in all logic tracks of the map.
        /// </summary>
        private static Track? FindTrack(JobSaveManager manager, string? id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            try
            {
                if (getTrack?.Invoke(manager, new object[] { id! }) is Track yardTrack)
                    return yardTrack;
            }
            catch
            {
                // not a yard track
            }

            try
            {
                foreach (Track track in RailTrackRegistry.RailTrackToLogicTrack.Values)
                {
                    if (track?.ID != null && string.Equals(track.ID.FullID, id, StringComparison.Ordinal))
                        return track;
                }
            }
            catch (Exception ex)
            {
                Main.LogWarning($"SHUNTING OUT -> track {id} not found: {ex.Message}");
            }

            return null;
        }

        private static List<CarsPerTrack>? SortingPickups(JobSaveManager manager, ShuntingOutJobDefinitionData data, List<Car> allCars)
        {
            if (data.pickupTrackIds == null || data.pickupCounts == null || data.pickupTrackIds.Length == 0 ||
                data.pickupTrackIds.Length != data.pickupCounts.Length || data.pickupCounts.Sum() != allCars.Count)
            {
                return null;
            }

            List<CarsPerTrack> result = new List<CarsPerTrack>();
            int index = 0;

            for (int i = 0; i < data.pickupTrackIds.Length; i++)
            {
                if (!(FindTrack(manager, data.pickupTrackIds[i]) is Track track) || data.pickupCounts[i] <= 0)
                    return null;

                result.Add(new CarsPerTrack(track, allCars.GetRange(index, data.pickupCounts[i])));
                index += data.pickupCounts[i];
            }

            return result;
        }
    }

    [HarmonyPatch(typeof(JobChainController), nameof(JobChainController.GetJobChainSaveData))]
    public static class ShuntingOutChainSaveDataPatch
    {
        static void Postfix(JobChainController __instance, ref JobChainSaveData __result)
        {
            if (__result != null && !(__result is ShuntingOutChainSaveData) && ShuntingOutSave.IsShuntingOutChain(__instance))
                __result = new ShuntingOutChainSaveData(__result);
        }

        /// <summary>
        /// A broken chain (e.g. one of its cars was deleted with the comms radio) throws "Uninitialized chain controller!"
        /// and that stops the WHOLE save – no job would be saved. The broken chain is skipped instead.
        /// </summary>
        static Exception? Finalizer(Exception? __exception, JobChainController __instance, ref JobChainSaveData? __result)
        {
            if (__exception == null)
                return null;

            string name = __instance?.jobChainGO != null ? __instance.jobChainGO.name : "?";
            Main.LogWarning($"SAVE -> job chain '{name}' can't be saved ({__exception.Message}) – skipped, the other jobs are saved");
            __result = null;
            return null;
        }
    }

    [HarmonyPatch(typeof(JobSaveManager))]
    public static class ShuntingOutJobSaveManagerPatch
    {
        /// <summary>Our chains are NOT stored in the vanilla part (own key instead).</summary>
        [HarmonyPatch(nameof(JobSaveManager.GetJobsSaveGameData))]
        [HarmonyPostfix]
        static void GetJobsSaveGameData_Postfix(ref JobsSaveGameData __result)
        {
            if (__result?.jobChains == null)
                return;

            // (null = broken chain that could not be saved, see ShuntingOutChainSaveDataPatch)
            __result.jobChains = __result.jobChains.Where(c => c != null && !(c is ShuntingOutChainSaveData)).ToArray();
        }

        [HarmonyPatch(nameof(JobSaveManager.LoadJobSaveGameData))]
        [HarmonyPrefix]
        static void LoadJobSaveGameData_Prefix(JobsSaveGameData saveData)
        {
            ShuntingOutSave.Merge(saveData);
        }

        [HarmonyPatch(nameof(JobSaveManager.LoadJobChain))]
        [HarmonyPrefix]
        static bool LoadJobChain_Prefix(JobChainSaveData chainSaveData, List<JobBooklet> jobBooklets, ref GameObject? __result)
        {
            if (!(chainSaveData is ShuntingOutChainSaveData own))
                return true;

            try
            {
                __result = ShuntingOutSave.LoadChain(own, jobBooklets);
            }
            catch (Exception ex)
            {
                Main.LogWarning("SHUNTING OUT -> loading failed: " + ex.Message);
                __result = null;
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(SaveGameManager), "UpdateInternalData")]
    public static class ShuntingOutWriteSavePatch
    {
        static void Postfix(SaveGameManager __instance)
        {
            if (__instance != null && __instance.data != null && !ImmersiveServiceMultiplayer.IsClientOnly)
                ShuntingOutSave.Write(__instance.data);
        }
    }

    // =========================================================
    // BOOKLET
    // =========================================================
    public static class ShuntingOutBooklet
    {
        /// <summary>Job ID -> IDs of the damaged cars (red marks on the overview page).</summary>
        public static readonly Dictionary<string, HashSet<string>> MarkedCars = new Dictionary<string, HashSet<string>>();

        /// <summary>Asphalt grey stripe of the maintenance jobs.</summary>
        public static readonly Color MaintenanceColor = new Color(0.36f, 0.38f, 0.40f, 1f);

        public static bool IsShuntingOut(Job_data? job) => IsOwnJob(job);

        /// <summary>SO or SM job (own booklet).</summary>
        public static bool IsOwnJob(Job_data? job)
        {
            if (job == null)
                return false;

            SpecialJobBookletColorKind kind = SpecialJobBookletColorHelper.GetServiceKind(job.ID);
            return kind == SpecialJobBookletColorKind.ShuntingOut || kind == SpecialJobBookletColorKind.Maintenance ||
                   kind == SpecialJobBookletColorKind.Sorting;
        }

        private static bool IsMaintenance(Job_data job) =>
            SpecialJobBookletColorHelper.GetServiceKind(job.ID) == SpecialJobBookletColorKind.Maintenance;

        private static bool IsSorting(Job_data job) =>
            SpecialJobBookletColorHelper.GetServiceKind(job.ID) == SpecialJobBookletColorKind.Sorting;

        /// <summary>SS: the pick-up tasks (start track = pick-up track, destination = end track).</summary>
        private static List<Task_data>? SortingPickups(Job_data job)
        {
            Task_data? parallel = job.tasksData?.FirstOrDefault()?.nestedTasks?.FirstOrDefault();
            Task_data[]? pickups = parallel?.nestedTasks;

            if (pickups == null || pickups.Length == 0 || pickups.Any(p => p?.cars == null || p.destinationTrackID == null))
                return null;

            return pickups.ToList();
        }

        private sealed class Parts
        {
            public Task_data Pickup = null!;
            public Task_data Middle = null!;   // SO: set-out of the damaged cars, SM: to the service track
            public Task_data Last = null!;     // SO: rest of the train back, SM: park after the repair
        }

        private static Parts? Extract(Job_data job)
        {
            Task_data? sequence = job.tasksData?.FirstOrDefault();
            Task_data[]? nested = sequence?.nestedTasks;

            if (nested == null || nested.Length < 2)
                return null;

            Parts parts;

            if (IsMaintenance(job))
            {
                if (nested.Length < 3)
                    return null;

                parts = new Parts { Pickup = nested[0], Middle = nested[1], Last = nested[2] };
            }
            else
            {
                Task_data? parallel = nested[1];

                if (parallel?.nestedTasks == null || parallel.nestedTasks.Length < 2)
                    return null;

                parts = new Parts { Pickup = nested[0], Middle = parallel.nestedTasks[0], Last = parallel.nestedTasks[1] };
            }

            if (parts.Pickup?.cars == null || parts.Middle?.cars == null || parts.Last?.cars == null)
                return null;

            return parts;
        }

        /// <summary>The pick-up task (for the vanilla reports, which only need cars and a track).</summary>
        public static Task_data? PickupTask(Job_data job)
        {
            if (!IsSorting(job))
                return Extract(job)?.Pickup;

            // SS: one transport view with all cars to the end track
            List<Task_data>? pickups = SortingPickups(job);

            if (pickups == null)
                return null;

            Task_data first = pickups[0];
            return new Task_data(first.type, first.instanceTaskType, first.state, first.taskStartTime, first.taskFinishTime,
                pickups.SelectMany(p => p.cars).ToList(), first.startTrackID, first.destinationTrackID, first.warehouseTaskType,
                null, 0f, false, false, null);
        }

        private static TaskTemplatePaperData Page(int step, string type, string description, StationInfo station, TrackID track,
            List<Car_data> cars, int page, int total)
        {
            return new TaskTemplatePaperData(step.ToString(), type, description,
                station.YardID, station.StationColor, track.TrackPartOnly, C.TRACK_COLOR,
                string.Empty, string.Empty, TemplatePaperData.NOT_USED_COLOR,
                cars, DisplayCargo(cars), page.ToString(), total.ToString());
        }

        private static bool IsStorageTrack(TrackID track)
        {
            string part = track?.TrackPartOnly ?? string.Empty;
            return part.EndsWith("S", StringComparison.OrdinalIgnoreCase);
        }

        public static List<TemplatePaperData> CreateBooklet(Job_data job)
        {
            if (IsSorting(job))
                return CreateSortingBooklet(job);

            Parts? parts = Extract(job);

            if (parts == null)
                return Fallback(job);

            StationInfo station = job.chainOriginStationInfo;
            bool maintenance = IsMaintenance(job);
            int total = maintenance ? 7 : 6;
            int page = 1;
            int step = 1;

            List<TemplatePaperData> pages = new List<TemplatePaperData>
            {
                new CoverPageTemplatePaperData(job.ID, Title(job), (page++).ToString(), total.ToString()),
                CreateOverview(job, parts, page++, total)
            };

            // couple – all cars on the start track
            pages.Add(Page(step++, LocalizationAPI.L("job/task_type_couple"), LocalizationAPI.L("job/task_desc_couple"),
                station, parts.Pickup.destinationTrackID, parts.Pickup.cars, page++, total));

            if (maintenance)
            {
                // shunt carefully to the maintenance (pit stop) track
                pages.Add(Page(step++, T(TrainServiceReworkTranslations.MaintenanceShuntTypeKey), T(TrainServiceReworkTranslations.MaintenanceShuntDescKey),
                    station, parts.Middle.destinationTrackID, parts.Middle.cars, page++, total));

                // repair to 100 %
                pages.Add(Page(step++, T(TrainServiceReworkTranslations.MaintenanceRepairTypeKey), T(TrainServiceReworkTranslations.MaintenanceRepairDescKey),
                    station, parts.Middle.destinationTrackID, parts.Middle.cars, page++, total));

                // park the repaired cars
                pages.Add(Page(step++, LocalizationAPI.L("job/task_type_uncouple"), T(TrainServiceReworkTranslations.MaintenanceParkDescKey),
                    station, parts.Last.destinationTrackID, parts.Last.cars, page++, total));
            }
            else
            {
                // sort out – only the damaged cars, their own track (no haul)
                pages.Add(Page(step++, T(TrainServiceReworkTranslations.ShuntingOutSortTypeKey), T(TrainServiceReworkTranslations.ShuntingOutSortDescKey),
                    station, parts.Middle.destinationTrackID, parts.Middle.cars, page++, total));

                // the rest of the train: back to the original track – on an S track it is only parked
                bool park = IsStorageTrack(parts.Last.destinationTrackID);
                pages.Add(Page(step++,
                    park ? LocalizationAPI.L("job/task_type_uncouple") : T(TrainServiceReworkTranslations.ShuntingOutDepartTypeKey),
                    park ? T(TrainServiceReworkTranslations.ShuntingOutParkDescKey) : T(TrainServiceReworkTranslations.ShuntingOutDepartDescKey),
                    station, parts.Last.destinationTrackID, parts.Last.cars, page++, total));
            }

            pages.Add(new ValidateJobTaskTemplatePaperData(step.ToString(), page.ToString(), total.ToString()));
            return pages;
        }

        /// <summary>
        /// SS: cover, overview (all cars), one couple page per pick-up ("take the car(s) from track"),
        /// uncouple page (train ready for the maintenance haul on the end track), validate.
        /// </summary>
        private static List<TemplatePaperData> CreateSortingBooklet(Job_data job)
        {
            List<Task_data>? pickups = SortingPickups(job);

            if (pickups == null)
                return Fallback(job);

            StationInfo station = job.chainOriginStationInfo;
            List<Car_data> all = pickups.SelectMany(p => p.cars).ToList();
            int total = 2 + pickups.Count + 2;
            int page = 1;
            int step = 1;

            List<TemplatePaperData> pages = new List<TemplatePaperData>
            {
                new CoverPageTemplatePaperData(job.ID, Title(job), (page++).ToString(), total.ToString()),
                CreateSortingOverview(job, pickups, page++, total)
            };

            foreach (Task_data pickup in pickups)
            {
                pages.Add(Page(step++, LocalizationAPI.L("job/task_type_couple"), T(TrainServiceReworkTranslations.SortingPickupDescKey),
                    station, pickup.startTrackID ?? pickup.destinationTrackID, pickup.cars, page++, total));
            }

            pages.Add(Page(step++, LocalizationAPI.L("job/task_type_uncouple"), T(TrainServiceReworkTranslations.SortingParkDescKey),
                station, pickups[0].destinationTrackID, all, page++, total));

            pages.Add(new ValidateJobTaskTemplatePaperData(step.ToString(), page.ToString(), total.ToString()));
            return pages;
        }

        private static TemplatePaperData CreateSortingOverview(Job_data job, List<Task_data> pickups, int page, int total)
        {
            StationInfo station = job.chainOriginStationInfo;
            List<Car_data> cars = pickups.SelectMany(p => p.cars).ToList();
            List<CargoType>? cargo = DisplayCargo(cars);
            MarkedCars.Remove(job.ID);

            string timeBonus = job.timeLimit > 0f ? $"{Mathf.FloorToInt(job.timeLimit / 60f)} min" : C.NO_BONUS_TIME_LIMIT_STR;
            string length = $"{C.GetCarsTotalLength(cars).ToString("N2", LocalizationAPI.CC)} m";
            string mass = $"{(C.GetCarsTotalMass(cars, cargo) * 0.001f).ToString("N2", LocalizationAPI.CC)} t";
            string value = $"${(C.GetTrainValue(cars, cargo) / 1000000f).ToString("N2", LocalizationAPI.CC)}m";
            string payment = job.basePayment.ToString("N0", LocalizationAPI.CC);
            // like vanilla shunting jobs: "2 pick-ups: - ..." (green 1, orange 2, red 3+)
            string description = C.GetShuntingPickUpsText(pickups.Count) + " - " + T(TrainServiceReworkTranslations.SortingDescKey);
            List<CargoType> distinct = cargo != null ? cargo.Distinct().ToList() : new List<CargoType>();

            return new FrontPageTemplatePaperData(
                Title(job), C.SHUNTING_JOB_TYPE_NAME, job.ID, C.SHUNTING_LOAD_JOB_TYPE_COLOR,
                description, job.requiredLicenses,
                distinct, cargo,
                LocalizationAPI.L(station.LocalizationKey), station.Type, station.StationColor,
                string.Empty, string.Empty, TemplatePaperData.NOT_USED_COLOR,
                string.Empty, string.Empty, TemplatePaperData.NOT_USED_COLOR,
                cars,
                length, mass, value, timeBonus, payment,
                page > 0 ? page.ToString() : string.Empty,
                total > 0 ? total.ToString() : string.Empty);
        }

        public static List<TemplatePaperData> CreateOverviewOnly(Job_data job)
        {
            if (IsSorting(job))
            {
                List<Task_data>? pickups = SortingPickups(job);

                return pickups == null
                    ? new List<TemplatePaperData> { new CoverPageTemplatePaperData(job.ID, Title(job), string.Empty, string.Empty) }
                    : new List<TemplatePaperData> { CreateSortingOverview(job, pickups, 0, 0) };
            }

            Parts? parts = Extract(job);

            if (parts == null)
                return new List<TemplatePaperData> { new CoverPageTemplatePaperData(job.ID, Title(job), string.Empty, string.Empty) };

            return new List<TemplatePaperData> { CreateOverview(job, parts, 0, 0) };
        }

        private static List<TemplatePaperData> Fallback(Job_data job)
        {
            Main.LogWarning($"TSR BOOKLET -> {job.ID}: unexpected task structure, emergency booklet");
            return new List<TemplatePaperData>
            {
                new CoverPageTemplatePaperData(job.ID, Title(job), "1", "2"),
                new ValidateJobTaskTemplatePaperData("1", "2", "2")
            };
        }

        public static string Title(Job_data job) => IsSorting(job)
            ? T(TrainServiceReworkTranslations.SortingJobTypeKey)
            : IsMaintenance(job)
                ? T(TrainServiceReworkTranslations.MaintenanceJobTypeKey)
                : T(TrainServiceReworkTranslations.DecommissionJobTypeKey);

        private static string T(string key)
        {
            string value = LocalizationAPI.L(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }

        private static TemplatePaperData CreateOverview(Job_data job, Parts parts, int page, int total)
        {
            StationInfo station = job.chainOriginStationInfo;
            bool maintenance = IsMaintenance(job);
            List<Car_data> cars = parts.Pickup.cars;
            List<CargoType>? cargo = DisplayCargo(cars);

            if (maintenance)
                MarkedCars.Remove(job.ID);
            else
                MarkedCars[job.ID] = new HashSet<string>(parts.Middle.cars.Where(c => c != null).Select(c => c.ID));

            string timeBonus = job.timeLimit > 0f ? $"{Mathf.FloorToInt(job.timeLimit / 60f)} min" : C.NO_BONUS_TIME_LIMIT_STR;
            string length = $"{C.GetCarsTotalLength(cars).ToString("N2", LocalizationAPI.CC)} m";
            string mass = $"{(C.GetCarsTotalMass(cars, cargo) * 0.001f).ToString("N2", LocalizationAPI.CC)} t";
            string value = $"${(C.GetTrainValue(cars, cargo) / 1000000f).ToString("N2", LocalizationAPI.CC)}m";
            string payment = job.basePayment.ToString("N0", LocalizationAPI.CC);

            // SO: like vanilla shunting jobs "1 pick-up: - ..." (coloured by the number of pick-ups)
            string description = maintenance
                ? T(TrainServiceReworkTranslations.MaintenanceDescKey)
                : C.GetShuntingPickUpsText(DamagedRuns(cars, MarkedCars[job.ID])) + " - " + string.Format(T(TrainServiceReworkTranslations.ShuntingOutDescKey),
                    parts.Middle.cars.Count,
                    parts.Middle.destinationTrackID.FullDisplayID,
                    parts.Last.destinationTrackID.FullDisplayID);

            List<CargoType> distinct = cargo != null ? cargo.Distinct().ToList() : new List<CargoType>();

            return new FrontPageTemplatePaperData(
                Title(job), C.SHUNTING_JOB_TYPE_NAME, job.ID, maintenance ? MaintenanceColor : C.SHUNTING_UNLOAD_JOB_TYPE_COLOR,
                description, job.requiredLicenses,
                distinct, cargo,
                LocalizationAPI.L(station.LocalizationKey), station.Type, station.StationColor,
                string.Empty, string.Empty, TemplatePaperData.NOT_USED_COLOR,
                string.Empty, string.Empty, TemplatePaperData.NOT_USED_COLOR,
                cars,
                length, mass, value, timeBonus, payment,
                page > 0 ? page.ToString() : string.Empty,
                total > 0 ? total.ToString() : string.Empty);
        }

        /// <summary>
        /// SM: repair costs of the cars (what the pit stop will charge, live condition when the booklet is printed),
        /// null if the cars are not found.
        /// </summary>
        public static float? RepairCost(List<Car_data>? cars)
        {
            try
            {
                if (cars == null || CarSpawner.Instance == null)
                    return null;

                HashSet<string> ids = new HashSet<string>(cars.Where(c => c != null).Select(c => c.ID));
                float cost = 0f;
                bool found = false;

                foreach (TrainCar car in CarSpawner.Instance.AllCars)
                {
                    if (car == null || car.ID == null || !ids.Contains(car.ID))
                        continue;

                    // what the pit stop will charge: units whose fee is already paid are free
                    float factor = RepairFees.IsManagedCar(car) ? RepairFees.PriceFactor(car) : 1f;
                    cost += DamagedJobSystem.EstimateRepairCost(car) * factor;
                    found = true;
                }

                return found ? cost : (float?)null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>SO pick-ups: groups of damaged cars that don't stand together in the train.</summary>
        private static int DamagedRuns(List<Car_data> consist, HashSet<string> damaged)
        {
            int runs = 0;
            bool previous = false;

            foreach (Car_data car in consist)
            {
                bool isDamaged = car != null && damaged.Contains(car.ID);

                if (isDamaged && !previous)
                    runs++;

                previous = isDamaged;
            }

            return Math.Max(1, runs);
        }

        /// <summary>Cargo per car for the pages: only if every car is loaded (the renderer can't show CargoType.None).</summary>
        private static List<CargoType>? DisplayCargo(List<Car_data> cars)
        {
            try
            {
                if (CarSpawner.Instance == null)
                    return null;

                Dictionary<string, TrainCar> byId = new Dictionary<string, TrainCar>();

                foreach (TrainCar car in CarSpawner.Instance.AllCars)
                {
                    if (car != null && car.ID != null)
                        byId[car.ID] = car;
                }

                List<CargoType> result = new List<CargoType>();

                foreach (Car_data data in cars)
                {
                    if (data == null || !byId.TryGetValue(data.ID, out TrainCar car) || car.logicCar == null)
                        return null;

                    CargoType cargo = car.logicCar.CurrentCargoTypeInCar;

                    if (cargo == CargoType.None)
                        return null;

                    result.Add(cargo);
                }

                return result;
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch(typeof(BookletCreator_Job), "GetBookletTemplateData")]
    public static class ShuntingOutBookletPatch
    {
        static bool Prefix(Job_data job, ref List<TemplatePaperData> __result)
        {
            if (!ShuntingOutBooklet.IsOwnJob(job))
                return true;

            try
            {
                __result = ShuntingOutBooklet.CreateBooklet(job);
            }
            catch (Exception ex)
            {
                Main.LogWarning($"SHUNTING OUT -> booklet {job.ID} failed: {ex.Message}");
                __result = new List<TemplatePaperData>
                {
                    new CoverPageTemplatePaperData(job.ID, "SO", "1", "2"),
                    new ValidateJobTaskTemplatePaperData("1", "2", "2")
                };
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(BookletCreator_JobOverview), nameof(BookletCreator_JobOverview.GetJobOverviewTemplateData))]
    public static class ShuntingOutOverviewPatch
    {
        static bool Prefix(Job_data job, ref List<TemplatePaperData> __result)
        {
            if (!ShuntingOutBooklet.IsOwnJob(job))
                return true;

            try
            {
                __result = ShuntingOutBooklet.CreateOverviewOnly(job);
            }
            catch (Exception ex)
            {
                Main.LogWarning($"SHUNTING OUT -> overview {job.ID} failed: {ex.Message}");
                __result = new List<TemplatePaperData> { new CoverPageTemplatePaperData(job.ID, "SO", string.Empty, string.Empty) };
            }

            return false;
        }
    }

    /// <summary>
    /// Order report of a maintenance job (SM): one line per car "Repair &lt;car&gt; to 100 %" right after the shunting
    /// to the maintenance track – ticked as soon as the car is repaired (live condition, also on clients).
    /// </summary>
    [HarmonyPatch(typeof(BookletCreator_JobReport), "GetReportTemplateData")]
    public static class MaintenanceReportContextPatch
    {
        [ThreadStatic]
        internal static Job_data? Current;

        [HarmonyPriority(Priority.First)]
        static void Prefix(Job_data data) => Current = data;

        static void Finalizer() => Current = null;
    }

    [HarmonyPatch(typeof(BookletCreator_JobReport), "ExtractTaskInfo")]
    public static class MaintenanceReportEntriesPatch
    {
        static void Postfix(Task_data[] tasksData, List<JobReportTasksTemplatePaperData.JobReportEntry> __result)
        {
            try
            {
                Job_data? job = MaintenanceReportContextPatch.Current;

                if (job == null || __result == null ||
                    SpecialJobBookletColorHelper.GetServiceKind(job.ID) != SpecialJobBookletColorKind.Maintenance)
                {
                    return;
                }

                Task_data[]? nested = tasksData?.FirstOrDefault()?.nestedTasks;

                if (nested == null || nested.Length < 3 || nested[1]?.cars == null || nested[1].destinationTrackID == null)
                    return;

                string service = nested[1].destinationTrackID.FullDisplayID;

                // insert after the line "... to <service track>", otherwise before "Validate order"
                int index = __result.FindIndex(e => e != null && e.description != null &&
                                                    e.description.TrimEnd().EndsWith(service, StringComparison.Ordinal));
                index = index >= 0 ? index + 1 : Math.Max(0, __result.Count - 1);

                Dictionary<string, TrainCar> byId = new Dictionary<string, TrainCar>();

                if (CarSpawner.Instance != null)
                {
                    foreach (TrainCar car in CarSpawner.Instance.AllCars)
                    {
                        if (car != null && car.ID != null)
                            byId[car.ID] = car;
                    }
                }

                string format = LocalizationAPI.L(TrainServiceReworkTranslations.MaintenanceReportRepairKey);

                if (string.IsNullOrEmpty(format) || !format.Contains("{0}"))
                    format = "Repair {0} to 100 %";

                List<JobReportTasksTemplatePaperData.JobReportEntry> lines = new List<JobReportTasksTemplatePaperData.JobReportEntry>();

                foreach (Car_data data in nested[1].cars)
                {
                    if (data == null)
                        continue;

                    bool repaired = byId.TryGetValue(data.ID, out TrainCar train) && DamagedJobSystem.Condition(train) >= 99.5f;
                    lines.Add(new JobReportTasksTemplatePaperData.JobReportEntry(
                        string.Format(format, data.ID), string.Empty,
                        repaired ? JobReportTasksTemplatePaperData.EntryState.COMPLETED : JobReportTasksTemplatePaperData.EntryState.IN_PROGRESS));
                }

                __result.InsertRange(Math.Min(index, __result.Count), lines);
            }
            catch (Exception ex)
            {
                Main.LogWarning("MAINTENANCE -> order report lines failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Expired / missing license reports: vanilla reads the task structure of the job type. For SO jobs a simple
    /// one-transport view (all cars, original track) is handed in instead.
    /// </summary>
    public static class ShuntingOutReportHelper
    {
        public static void Substitute(ref Job_data job)
        {
            if (!ShuntingOutBooklet.IsOwnJob(job))
                return;

            Task_data? pickup = ShuntingOutBooklet.PickupTask(job);

            if (pickup == null)
                return;

            job = new Job_data(job.ID, JobType.EmptyHaul, job.state, job.completionTime, job.timeOnJob, job.timeLimit,
                job.basePayment, job.bonusPayment, job.totalPayment, job.requiredLicenses, new[] { pickup },
                job.chainOriginStationInfo, job.chainDestinationStationInfo);
        }
    }

    [HarmonyPatch(typeof(BookletCreator_JobExpiredReport), "GetJobExpiredTemplateData")]
    public static class ShuntingOutExpiredReportPatch
    {
        static void Prefix(ref Job_data job) => ShuntingOutReportHelper.Substitute(ref job);

        static void Postfix(Job_data job, List<TemplatePaperData> __result) => ServiceReportTitles.Apply(job, __result, false);
    }

    [HarmonyPatch(typeof(BookletCreator_JobMissingLicense), "GetMissingLicenseTemplateData")]
    public static class ShuntingOutMissingLicensePatch
    {
        static void Prefix(ref Job_data job) => ShuntingOutReportHelper.Substitute(ref job);

        static void Postfix(Job_data job, bool isJobLicenseMissing, List<TemplatePaperData> __result) =>
            ServiceReportTitles.Apply(job, __result, isJobLicenseMissing);
    }

    /// <summary>
    /// "Denied" / expired papers of our jobs: our job name and colour instead of the vanilla type
    /// (FREIGHT HAUL / LOGISTICAL HAUL); maintenance jobs list the missing maintenance license.
    /// </summary>
    public static class ServiceReportTitles
    {
        public static void Apply(Job_data? job, List<TemplatePaperData>? pages, bool jobLicenseMissing)
        {
            try
            {
                if (job == null || pages == null)
                    return;

                SpecialJobBookletColorKind kind = SpecialJobBookletColorHelper.GetServiceKind(job.ID);

                if (kind == SpecialJobBookletColorKind.None)
                    return;

                string name = SpecialJobBookletColorHelper.GetLocalizedJobType(kind);
                bool shunting = kind == SpecialJobBookletColorKind.ShuntingOut || kind == SpecialJobBookletColorKind.Maintenance ||
                                kind == SpecialJobBookletColorKind.Overhaul || kind == SpecialJobBookletColorKind.Sorting;
                Color? color = kind == SpecialJobBookletColorKind.Maintenance || kind == SpecialJobBookletColorKind.Overhaul
                    ? ShuntingOutBooklet.MaintenanceColor
                    : kind == SpecialJobBookletColorKind.ShuntingOut ? C.SHUNTING_UNLOAD_JOB_TYPE_COLOR
                    : kind == SpecialJobBookletColorKind.Sorting ? C.SHUNTING_LOAD_JOB_TYPE_COLOR : (Color?)null;

                foreach (TemplatePaperData page in pages)
                {
                    if (page is MissingLicensesPageTemplatePaperData missing)
                    {
                        if (!string.IsNullOrEmpty(name))
                            missing.jobType = name;

                        if (shunting)
                            missing.jobSubtype = C.SHUNTING_JOB_TYPE_NAME;

                        if (color.HasValue)
                            missing.jobTypeColor = color.Value;

                        if (jobLicenseMissing && (kind == SpecialJobBookletColorKind.Maintenance || kind == SpecialJobBookletColorKind.Overhaul))
                            AddMaintenanceLicense(job, missing);
                    }
                    else if (page is JobExpiredTemplatePaperData expired)
                    {
                        if (!string.IsNullOrEmpty(name))
                            expired.jobType = name;

                        if (shunting)
                            expired.jobSubtype = C.SHUNTING_JOB_TYPE_NAME;

                        if (color.HasValue)
                            expired.jobTypeColor = color.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Main.LogWarning("TSR BOOKLET -> report title failed: " + ex.Message);
            }
        }

        private static void AddMaintenanceLicense(Job_data job, MissingLicensesPageTemplatePaperData page)
        {
            List<string?> ids = new List<string?>();

            foreach (Task_data? task in job.tasksData ?? new Task_data[0])
                CollectCarIds(task, ids, 0);

            TsrLicenseV2? license = MaintenanceLicenses.RequiredFor(ids);

            if (license == null)
                return;

            page.licensesData ??= new List<MissingLicensesPageTemplatePaperData.LicensePrintData>();

            string licenseName = LocalizationAPI.L(license.localizationKey);

            if (page.licensesData.Any(l => l != null && l.licenseName == licenseName))
                return;

            page.licensesData.Insert(0, new MissingLicensesPageTemplatePaperData.LicensePrintData(
                licenseName, license.icon, MaintenanceLicenses.IsAcquired(license)));
        }

        private static void CollectCarIds(Task_data? task, List<string?> ids, int depth)
        {
            if (task == null || depth > 10)
                return;

            if (task.cars != null)
                ids.AddRange(task.cars.Where(c => c != null).Select(c => (string?)c.ID));

            if (task.nestedTasks != null)
            {
                foreach (Task_data nested in task.nestedTasks)
                    CollectCarIds(nested, ids, depth + 1);
            }
        }
    }

    // =========================================================
    // OVERVIEW PAGE (SO + SM)
    //  * station name in the same style as the start/end station bands (not bold, smaller)
    //  * SO: damaged cars marked red – red car + small red triangle centred above it (own white sprite, tinted)
    //  * SM: Manual Service license shown next to the job licenses
    // =========================================================
    [HarmonyPatch(typeof(FrontPageTemplatePaper), nameof(FrontPageTemplatePaper.FillInData))]
    public static class ShuntingOutFrontPageMarkPatch
    {
        private static readonly FieldInfo? createdField = AccessTools.Field(typeof(TemplatePaper), "dynamicallyCreatedObjects");
        private static readonly Color MarkColor = new Color(0.85f, 0.1f, 0.1f, 1f);

        // red of the vanilla booklets (#B63936)
        private static readonly Color CostBoxColor = new Color(0.714f, 0.224f, 0.212f, 1f);
        private static Sprite? triangle;

        /// <summary>The payment box of the reused template gets its original size back before every fill.</summary>
        static void Prefix(FrontPageTemplatePaper __instance)
        {
            try
            {
                if (__instance != null)
                    RestorePaymentBox(__instance);
            }
            catch
            {
                // nothing to restore
            }
        }

        static void Postfix(FrontPageTemplatePaper __instance)
        {
            try
            {
                if (__instance == null)
                    return;

                FrontPageTemplatePaperData? data = __instance.data;

                if (data == null || string.IsNullOrEmpty(data.jobId))
                    return;

                SpecialJobBookletColorKind kind = SpecialJobBookletColorHelper.GetServiceKind(data.jobId);

                if (kind != SpecialJobBookletColorKind.ShuntingOut && kind != SpecialJobBookletColorKind.Maintenance &&
                    kind != SpecialJobBookletColorKind.Sorting)
                    return;

                StyleStationName(__instance);

                if (kind == SpecialJobBookletColorKind.Sorting)
                    return;

                List<GameObject>? created = createdField?.GetValue(__instance) as List<GameObject>;

                if (kind == SpecialJobBookletColorKind.Maintenance)
                {
                    ShowMaintenanceLicense(__instance, data);
                    ShowRepairCost(__instance, data, created);
                    return;
                }

                if (data.cars == null || !ShuntingOutBooklet.MarkedCars.TryGetValue(data.jobId, out HashSet<string> marked) || marked.Count == 0)
                    return;

                if (created == null || __instance.car == null)
                    return;

                string template = __instance.car.name;
                List<GameObject> carObjects = created.Where(o => o != null && o.name.StartsWith(template, StringComparison.Ordinal)).ToList();

                for (int i = 0; i < carObjects.Count && i < data.cars.Count; i++)
                {
                    Car_data car = data.cars[i];

                    if (car == null || !marked.Contains(car.ID))
                        continue;

                    GameObject carObject = carObjects[i];

                    // red car (not the cargo icon)
                    foreach (Image image in carObject.GetComponentsInChildren<Image>(true))
                    {
                        if (image != null && image.name != TemplatePaper.CARGO_ICON_GO_NAME)
                            image.color = MarkColor;
                    }

                    AddMark(carObject);
                }
            }
            catch (Exception ex)
            {
                Main.LogWarning("TSR BOOKLET -> overview page failed: " + ex.Message);
            }
        }

        /// <summary>Single station name like the start/end bands: same font, style and size (not bold, smaller).</summary>
        private static void StyleStationName(FrontPageTemplatePaper page)
        {
            TextMeshProUGUI? target = page.singleStationName;
            TextMeshProUGUI? source = page.startStationName;

            if (target == null || source == null)
                return;

            target.font = source.font;
            target.fontSharedMaterial = source.fontSharedMaterial;
            target.fontStyle = source.fontStyle;
            target.enableAutoSizing = false;
            target.fontSize = source.fontSize > 0f ? source.fontSize : target.fontSize * 0.6f;
            target.characterSpacing = source.characterSpacing;
            target.color = source.color;
        }

        /// <summary>White triangle (pointing down) – tinted red by the Image colour.</summary>
        private static Sprite Triangle()
        {
            if (triangle != null)
                return triangle;

            const int size = 32;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "TSR_Triangle",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            Color[] pixels = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                // y = 0 is the bottom row: the tip; the top row is the full width
                float half = (y + 0.5f) / size * (size * 0.5f);

                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x + 0.5f - size * 0.5f);
                    float alpha = Mathf.Clamp01(half - dx + 0.5f);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            UnityEngine.Object.DontDestroyOnLoad(texture);

            triangle = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            triangle.name = "TSR_Triangle";
            UnityEngine.Object.DontDestroyOnLoad(triangle);
            return triangle;
        }

        /// <summary>Small red triangle, centred above the car icon, pointing down at it.</summary>
        private static void AddMark(GameObject carObject)
        {
            RectTransform? carRect = carObject.transform as RectTransform;
            float carHeight = carRect != null && carRect.rect.height > 0f ? carRect.rect.height : 20f;
            float width = carHeight * 0.9f;
            float height = carHeight * 0.7f;

            GameObject mark = new GameObject("[TSR damaged mark]", typeof(RectTransform));
            mark.layer = carObject.layer;
            mark.transform.SetParent(carObject.transform, false);

            Image image = mark.AddComponent<Image>();
            image.sprite = Triangle();
            image.color = MarkColor;
            image.raycastTarget = false;
            image.preserveAspect = false;

            RectTransform rect = (RectTransform)mark.transform;
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.sizeDelta = new Vector2(width, height);
            rect.anchoredPosition = new Vector2(0f, carHeight * 0.15f);
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            mark.transform.SetAsLastSibling();
        }

        /// <summary>
        /// SM job: red box "repair costs: -$…" right next to the green payment box (a copy of it), so the player sees
        /// the costs before taking the job. If the box can't be found: a red line in the description instead.
        /// </summary>
        private static void ShowRepairCost(FrontPageTemplatePaper page, FrontPageTemplatePaperData data, List<GameObject>? created)
        {
            float? cost = ShuntingOutBooklet.RepairCost(data.cars);

            // nothing to pay at the pit stop (fee already paid): no repair cost box at all
            if (cost == null || cost.Value < 0.5f)
                return;

            string amount = "-$" + cost.Value.ToString("N0", LocalizationAPI.CC);
            string label = LocalizationAPI.L(TrainServiceReworkTranslations.MaintenanceCostLabelKey);

            if (string.IsNullOrEmpty(label))
                label = "repair costs:";

            TextMeshProUGUI? payment = page.payment;
            RectTransform? box = payment != null ? FindPaymentBox(page, payment) : null;

            if (payment == null || box == null || created == null || box.parent == null)
            {
                if (page.jobDescription != null)
                    page.jobDescription.text += $"\n<color=#B63936><b>{label} {amount}</b></color>";

                return;
            }

            // job overview (offer) has no page numbers – the "to accept" text is right of the payment box there:
            // both boxes get narrower so they end before that text
            bool overview = string.IsNullOrEmpty(data.pageNumber);
            Vector2 size = box.sizeDelta;
            Vector2 position = box.anchoredPosition;
            float width = box.rect.width;
            float height = box.rect.height;

            // path from the box to the payment text (to find the copied text in the copied box)
            List<int> path = new List<int>();

            for (Transform t = payment.transform; t != box.transform; t = t.parent)
                path.Insert(0, t.GetSiblingIndex());

            GameObject clone = UnityEngine.Object.Instantiate(box.gameObject, box.parent, false);
            clone.name = "[TSR repair costs]";
            created.Add(clone);
            RectTransform rect = (RectTransform)clone.transform;

            if (overview)
            {
                // the template is reused for other jobs: the original size is restored before the next fill
                ShrunkBoxes.Add(new ShrunkBox(box, size, position, payment, payment.enableAutoSizing, payment.fontSizeMax, payment.fontSize,
                    payment.textWrappingMode));

                // both boxes 70 % wide, side by side from the left edge of the payment box – they end before
                // the "to accept" text (which is right-aligned)
                float narrow = width * 0.7f;
                float less = width - narrow;
                float pivot = box.pivot.x;

                box.sizeDelta = size - new Vector2(less, 0f);
                box.anchoredPosition = position - new Vector2(pivot * less, 0f);
                rect.sizeDelta = box.sizeDelta;
                rect.anchoredPosition = box.anchoredPosition + new Vector2(narrow + width * 0.05f, 0f);

                FitText(payment);
            }
            else
            {
                rect.anchoredPosition = position + new Vector2(width * 1.06f, 0f);
            }

            // red box: own rounded white sprite tinted red (the green sprite can't be tinted red)
            Image? background = clone.GetComponent<Image>();

            if (background != null)
            {
                background.sprite = RoundedBox(height);
                background.type = Image.Type.Sliced;
                background.color = CostBoxColor;
            }

            foreach (Image image in clone.GetComponentsInChildren<Image>(true))
            {
                if (image != null && image != background)
                    image.enabled = false;
            }

            Transform? target = clone.transform;

            foreach (int index in path)
                target = target != null && index < target.childCount ? target.GetChild(index) : null;

            TextMeshProUGUI? amountText = target != null ? target.GetComponent<TextMeshProUGUI>() : null;

            if (amountText == null)
                return;

            // amount exactly in the middle of the box
            RectTransform amountRect = amountText.rectTransform;
            amountRect.anchorMin = Vector2.zero;
            amountRect.anchorMax = Vector2.one;
            amountRect.pivot = new Vector2(0.5f, 0.5f);
            amountRect.offsetMin = Vector2.zero;
            amountRect.offsetMax = Vector2.zero;
            amountText.alignment = TextAlignmentOptions.Center;
            amountText.margin = Vector4.zero;
            amountText.textWrappingMode = TextWrappingModes.NoWrap;
            amountText.text = amount;

            if (overview)
                FitText(amountText);

            // small label at the top edge
            TextMeshProUGUI labelText = UnityEngine.Object.Instantiate(amountText, amountText.transform.parent, false);
            labelText.name = "[TSR repair costs label]";
            labelText.text = label;
            labelText.enableAutoSizing = false;
            labelText.fontSize = Mathf.Max(4f, (payment.enableAutoSizing ? payment.fontSizeMax : payment.fontSize) * 0.36f);
            labelText.alignment = TextAlignmentOptions.Top;
            labelText.textWrappingMode = TextWrappingModes.NoWrap;

            RectTransform labelRect = labelText.rectTransform;
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = new Vector2(0f, -height * 0.03f);
        }

        /// <summary>Text shrinks to fit the (smaller) box.</summary>
        private static void FitText(TextMeshProUGUI text)
        {
            text.textWrappingMode = TextWrappingModes.NoWrap;
            float max = text.enableAutoSizing ? text.fontSizeMax : text.fontSize;
            text.enableAutoSizing = true;
            text.fontSizeMax = max;
            text.fontSizeMin = Mathf.Min(text.fontSizeMin > 0f ? text.fontSizeMin : 4f, max);
        }

        // ---- original payment box of the reused page template (restored before every fill) ----
        private sealed class ShrunkBox
        {
            public readonly RectTransform Box;
            public readonly Vector2 Size;
            public readonly Vector2 Position;
            public readonly TextMeshProUGUI Text;
            public readonly bool AutoSize;
            public readonly float FontSizeMax;
            public readonly float FontSize;
            public readonly TextWrappingModes WordWrapping;

            public ShrunkBox(RectTransform box, Vector2 size, Vector2 position, TextMeshProUGUI text, bool autoSize, float fontSizeMax, float fontSize,
                TextWrappingModes wordWrapping)
            {
                WordWrapping = wordWrapping;
                Box = box;
                Size = size;
                Position = position;
                Text = text;
                AutoSize = autoSize;
                FontSizeMax = fontSizeMax;
                FontSize = fontSize;
            }
        }

        private static readonly List<ShrunkBox> ShrunkBoxes = new List<ShrunkBox>();

        public static void RestorePaymentBox(FrontPageTemplatePaper page)
        {
            for (int i = ShrunkBoxes.Count - 1; i >= 0; i--)
            {
                ShrunkBox entry = ShrunkBoxes[i];

                if (entry.Box == null)
                {
                    ShrunkBoxes.RemoveAt(i);
                    continue;
                }

                if (page != null && !entry.Box.IsChildOf(page.transform))
                    continue;

                entry.Box.sizeDelta = entry.Size;
                entry.Box.anchoredPosition = entry.Position;

                if (entry.Text != null)
                {
                    entry.Text.enableAutoSizing = entry.AutoSize;
                    entry.Text.fontSizeMax = entry.FontSizeMax;
                    entry.Text.fontSize = entry.FontSize;
                    entry.Text.textWrappingMode = entry.WordWrapping;
                }

                ShrunkBoxes.RemoveAt(i);
            }
        }

        private static Sprite? roundedBox;

        /// <summary>White rounded rectangle (9-sliced), corner radius ~12 % of the box height.</summary>
        private static Sprite RoundedBox(float boxHeight)
        {
            if (roundedBox != null)
                return roundedBox;

            const int size = 64;
            const float radius = 16f;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "TSR_RoundedBox",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            Color[] pixels = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;
                    float cx = Mathf.Clamp(px, radius, size - radius);
                    float cy = Mathf.Clamp(py, radius, size - radius);
                    float distance = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                    pixels[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(radius - distance + 0.5f));
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            UnityEngine.Object.DontDestroyOnLoad(texture);

            // canvas units of the corner = radius * 100 / pixelsPerUnit -> pick pixelsPerUnit for ~12 % of the box height
            float corner = Mathf.Max(1f, boxHeight * 0.12f);
            float pixelsPerUnit = Mathf.Clamp(radius * 100f / corner, 1f, 10000f);

            roundedBox = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), pixelsPerUnit, 0,
                SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
            roundedBox.name = "TSR_RoundedBox";
            UnityEngine.Object.DontDestroyOnLoad(roundedBox);
            return roundedBox;
        }

        /// <summary>The coloured box around the payment text (an ancestor with an Image, clearly smaller than the page).</summary>
        private static RectTransform? FindPaymentBox(FrontPageTemplatePaper page, TextMeshProUGUI payment)
        {
            RectTransform? pageRect = page.transform as RectTransform;
            float pageWidth = pageRect != null && pageRect.rect.width > 0f ? pageRect.rect.width : float.MaxValue;
            Transform? current = payment.transform.parent;

            for (int i = 0; i < 3 && current != null && current != page.transform; i++, current = current.parent)
            {
                if (current is RectTransform rect && current.GetComponent<Image>() != null && rect.rect.width < pageWidth * 0.6f)
                    return rect;
            }

            return null;
        }

        /// <summary>SM job: the maintenance license (freight / passenger) in the next free license slot.</summary>
        private static void ShowMaintenanceLicense(FrontPageTemplatePaper page, FrontPageTemplatePaperData data)
        {
            Image[]? slots = page.requiredLicenseSlots;

            if (slots == null || slots.Length == 0)
                return;

            Sprite? icon = null;

            try
            {
                TsrLicenseV2? license = MaintenanceLicenses.RequiredFor(
                    (data.cars ?? new List<Car_data>()).Where(c => c != null).Select(c => (string?)c.ID));
                icon = license != null ? license.icon : GeneralLicenseType.ManualService.ToV2()?.icon;
            }
            catch
            {
                icon = null;
            }

            if (icon == null)
                return;

            foreach (Image slot in slots)
            {
                if (slot == null || slot.gameObject.activeSelf)
                    continue;

                slot.sprite = icon;
                slot.color = Color.white;
                slot.gameObject.SetActive(true);
                return;
            }
        }
    }
}
