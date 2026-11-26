// File: TrainServiceReworkImmersiveService.cs
//
// Optional "Immersive Service":
//  - repairing / refilling at pit stop modules takes time (seconds per unit, per module)
//  - work only starts after paying at the cash register
//  - free display line of every module shows DURATION (before paying) / REMAINING (while working)
//  - main pit stop display shows the total time of all modules (respecting the parallel limit)
//  - module lamp: steady yellow = waiting for a free slot, blinking yellow = working
//  - the lever of a busy module is locked (no second order into a running one)
//  - pit stop sounds while working (see TrainServiceReworkServiceAudio.cs)
//  - any movement of the vehicle aborts all of its work (unfinished part refunded, configurable)
//  - running work is stored in the savegame and resumes after loading
//  - multiplayer: host runs everything, clients get state + settings from the host

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using DV.CabControls;
using DV.InventorySystem;
using DV.Localization;
using DV.ThingTypes;

namespace TrainServiceRework
{
    // =========================================================
    // CONFIG (local settings or host settings in multiplayer)
    // =========================================================
    public sealed class ImmersiveServiceConfig
    {
        public bool Enabled;
        public int MaxParallelModules = 2;

        public float SecondsFuel;
        public float SecondsOil;
        public float SecondsSand;
        public float SecondsElectric;
        public float SecondsBody;
        public float SecondsWheels;
        public float SecondsMechanical;
        public float SecondsElectrical;

        public bool RefundUnfinished = true;

        public int ParallelLimit => Mathf.Clamp(MaxParallelModules, 1, 8);

        public static ImmersiveServiceConfig FromSettings(Settings s)
        {
            return new ImmersiveServiceConfig
            {
                Enabled = s.ImmersiveService,
                MaxParallelModules = s.MaxParallelModules,
                SecondsFuel = s.SecondsFuel,
                SecondsOil = s.SecondsOil,
                SecondsSand = s.SecondsSand,
                SecondsElectric = s.SecondsElectric,
                SecondsBody = s.SecondsBody,
                SecondsWheels = s.SecondsWheels,
                SecondsMechanical = s.SecondsMechanical,
                SecondsElectrical = s.SecondsElectrical,
                RefundUnfinished = s.RefundUnfinished
            };
        }

        public float GetSecondsPerUnit(ResourceType type)
        {
            float value;

            switch (type)
            {
                case ResourceType.Fuel: value = SecondsFuel; break;
                case ResourceType.Oil: value = SecondsOil; break;
                case ResourceType.Sand: value = SecondsSand; break;
                // Water / Coal: excluded on purpose (own filling animation) -> vanilla
                case ResourceType.ElectricCharge: value = SecondsElectric; break;
                case ResourceType.Car_DMG: value = SecondsBody; break;
                case ResourceType.Wheels_DMG: value = SecondsWheels; break;
                case ResourceType.MechanicalPowertrain_DMG: value = SecondsMechanical; break;
                case ResourceType.ElectricalPowertrain_DMG: value = SecondsElectrical; break;
                default: value = 0f; break;
            }

            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0f;

            return Mathf.Max(0f, value);
        }
    }

    // =========================================================
    // JOB MODEL
    // =========================================================
    public enum ServiceJobState : byte
    {
        Queued = 0,
        Running = 1,
        Finished = 2 // short marker after a normal finish (ding on every machine)
    }

    public sealed class ServiceJob
    {
        public int Id;
        public string StationKey = string.Empty;
        public int ModuleIndex;
        public ResourceType Resource;
        public string CarGuid = string.Empty;
        public TrainCar? Car;
        public CarPitStopParametersBase? Params;

        public float TotalUnits;     // signed, exactly what was paid for
        public float AppliedUnits;   // signed, already applied to the vehicle
        public double TotalPrice;
        public float SecondsPerUnit;
        public float Duration;
        public float Elapsed;
        public float NextApplyTime;

        public ServiceJobState State;
        public long Order;
        public Vector3 StartPosition; // absolute world position (origin shift removed)
    }

    // What every machine needs to draw displays / lamps / locks.
    public struct ServiceJobView
    {
        public int JobId;
        public int ModuleIndex;
        public string CarGuid;
        public ServiceJobState State;
        public float Remaining; // own work time left (only counts down while Running), at ViewTime
        public float Eta;       // seconds until this job is finished (queue included), at ViewTime
    }

    internal sealed class ModuleRow
    {
        public TextMeshPro? Label;
        public TextMeshPro? Value;
        public string LastLabel = "\u0001";
        public string LastValue = "\u0001";
    }

    internal sealed class StationState
    {
        public string Key = string.Empty;
        public PitStopStation? Station;
        public LocoResourceModule[] Modules = new LocoResourceModule[0];
        public ModuleRow?[] Rows = new ModuleRow?[0];
        public TextMeshPro? TotalText;
        public string LastTotal = "\u0001";
        public bool VisualsActive;
        public readonly List<ServiceJobView> Views = new List<ServiceJobView>();
        public float ViewTime;
    }

    internal sealed class LampOverride
    {
        public Color Emission;
        public Color Glare;
        public Color LightColor;
        public bool HasLight;
        public int Mode = -1;
    }

    internal struct SimItem
    {
        public int Module;
        public bool Running;
        public float Remaining;
        public float Duration;
        public float Eta;
    }

    // =========================================================
    // RUNNER
    // =========================================================
    public class ImmersiveServiceRunner : MonoBehaviour
    {
        private void Update()
        {
            ImmersiveServiceSystem.Tick(Time.deltaTime);
            DamagedJobSystem.Tick();
        }
    }

    // =========================================================
    // CORE
    // =========================================================
    public static class ImmersiveServiceSystem
    {
        private const string SaveKey = "TrainServiceRework_ImmersiveService";
        private const float MoveAbortDistance = 0.5f;
        private const float ApplyInterval = 1f;
        private const float VisualInterval = 0.2f;
        private const float ViewRebuildInterval = 0.25f;
        private const float StateResendInterval = 2f;
        private const float RestoreTimeout = 90f;

        // ---------------- reflection ----------------
        private static readonly AccessTools.FieldRef<LocoResourceModule, List<CashRegisterModule.CashRegisterModuleData>>? resourceDataRef =
            SafeRef<LocoResourceModule, List<CashRegisterModule.CashRegisterModuleData>>("resourceData");

        private static readonly AccessTools.FieldRef<LocoResourceModule, PitStopStation>? modulePitStopStationRef =
            SafeRef<LocoResourceModule, PitStopStation>("pitStopStation");

        private static readonly FieldInfo? onResourceBoughtField = AccessTools.Field(typeof(LocoResourceModule), "OnResourceBought");
        private static readonly FieldInfo? globalBoughtField = AccessTools.Field(typeof(LocoResourceModule), "LocoResourceBoughtGlobalEvent");
        private static readonly MethodInfo? emissionSetColorMethod = AccessTools.Method(typeof(IndicatorEmission), "SetColor", new[] { typeof(float) });

        // ---------------- state ----------------
        private static readonly Dictionary<string, StationState> stations = new Dictionary<string, StationState>();
        private static readonly Dictionary<PitStopStation, StationState> byStation = new Dictionary<PitStopStation, StationState>();
        private static readonly Dictionary<LocoResourceModule, int> moduleIndex = new Dictionary<LocoResourceModule, int>();
        private static readonly Dictionary<LocoResourceModule, StationState> moduleStation = new Dictionary<LocoResourceModule, StationState>();

        private static readonly List<ServiceJob> jobs = new List<ServiceJob>();

        // finished jobs stay visible for a moment as "Finished" marker → ding on host and clients
        private const float FinishedMarkerTime = 2f;
        private sealed class FinishedMarker
        {
            public float Until;
            public string Station = string.Empty;
            public ServiceJobView View;
        }

        private static readonly List<FinishedMarker> finishedMarkers = new List<FinishedMarker>();
        private static readonly HashSet<int> dingedJobs = new HashSet<int>();
        private static readonly List<JObject> pendingRestore = new List<JObject>();
        private static float pendingRestoreSince;

        private static readonly Dictionary<LampControl, LampOverride> lampOverrides = new Dictionary<LampControl, LampOverride>();
        private static bool lampBypass;

        private static readonly Dictionary<LocoResourceModule, ControlImplBase?> moduleLevers = new Dictionary<LocoResourceModule, ControlImplBase?>();
        private static readonly HashSet<ControlImplBase> lockedLevers = new HashSet<ControlImplBase>();

        // nozzle / charging plug of a busy module can't be pulled out of the vehicle
        private static readonly Dictionary<PluggableObject, List<Collider>> lockedHoses = new Dictionary<PluggableObject, List<Collider>>();

        private static readonly AccessTools.FieldRef<PluggableObject, ControlImplBase>? plugControlRef =
            SafeRef<PluggableObject, ControlImplBase>("controlBase");

        private static readonly MethodInfo? registerUnitsChangedMethod =
            AccessTools.Method(typeof(DV.CashRegister.CashRegisterWithModules), "OnUnitsToBuyChanged");

        private static readonly AccessTools.FieldRef<LocoResourceModule, int>? flowMultiplierRef =
            SafeRef<LocoResourceModule, int>("flowMultiplier");

        private static readonly Dictionary<string, TrainCar> carCache = new Dictionary<string, TrainCar>();
        private static float carCacheTime = -100f;

        private static ImmersiveServiceConfig localConfig = new ImmersiveServiceConfig();
        private static ImmersiveServiceConfig? hostConfig;

        private static GameObject? runnerObject;
        private static bool worldActive;
        private static bool restored;
        private static bool stateDirty;
        private static bool lastSentEmpty = true;
        private static int nextJobId;
        private static long nextOrder;

        private static float configTimer;
        private static float visualTimer;
        private static float stateTimer;
        private static float stationScanTimer;
        private static float viewTimer;


        // =====================================================
        // PUBLIC API
        // =====================================================
        public static ImmersiveServiceConfig Config
        {
            get
            {
                if (hostConfig != null && ImmersiveServiceMultiplayer.IsClientOnly)
                    return hostConfig;

                return localConfig;
            }
        }

        public static void CreateRunner()
        {
            RefreshLocalConfig();

            if (runnerObject != null)
                return;

            runnerObject = new GameObject("TrainServiceRework_ImmersiveService");
            UnityEngine.Object.DontDestroyOnLoad(runnerObject);
            runnerObject.AddComponent<ImmersiveServiceRunner>();
        }

        public static void RefreshLocalConfig()
        {
            localConfig = ImmersiveServiceConfig.FromSettings(Main.Settings);
        }

        public static void ApplyHostConfig(ImmersiveServiceConfig config)
        {
            hostConfig = config;
            Main.Log("IMMERSIVE SERVICE -> host settings received");
        }

        public static void ClearHostConfig()
        {
            hostConfig = null;
        }

        public static string FormatTime(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds))
                seconds = 0f;

            int total = Mathf.CeilToInt(Mathf.Max(0f, seconds));
            int hours = total / 3600;
            int minutes = (total / 60) % 60;
            int secs = total % 60;

            return hours.ToString("00") + ":" + minutes.ToString("00") + ":" + secs.ToString("00");
        }

        // =====================================================
        // TICK
        // =====================================================
        public static void Tick(float dt)
        {
            bool loaded;

            try
            {
                loaded = WorldStreamingInit.IsLoaded;
            }
            catch
            {
                loaded = false;
            }

            if (!loaded)
            {
                if (worldActive)
                    OnWorldUnloaded();

                return;
            }

            worldActive = true;

            try
            {
                configTimer -= Time.unscaledDeltaTime;

                if (configTimer <= 0f)
                {
                    configTimer = 1f;
                    RefreshLocalConfig();
                }

                stationScanTimer -= Time.unscaledDeltaTime;

                if (stationScanTimer <= 0f)
                {
                    stationScanTimer = byStation.Count == 0 ? 5f : 60f;
                    ScanStations();
                }

                bool clientOnly = ImmersiveServiceMultiplayer.IsClientOnly;

                if (!clientOnly)
                {
                    if (!restored)
                    {
                        restored = true;
                        ReadSave();
                    }

                    ResolvePendingRestore();
                    TickHost(dt);
                }

                visualTimer -= Time.unscaledDeltaTime;

                if (visualTimer <= 0f)
                {
                    visualTimer = VisualInterval;
                    UpdateVisuals();
                }
            }
            catch (Exception ex)
            {
                Main.LogError("IMMERSIVE SERVICE TICK FAILED: " + ex);
            }
        }

        private static void OnWorldUnloaded()
        {
            worldActive = false;
            restored = false;

            jobs.Clear();
            pendingRestore.Clear();
            lampOverrides.Clear();
            moduleLevers.Clear();
            lockedHoses.Clear();
            finishedMarkers.Clear();
            dingedJobs.Clear();
            ImmersiveServiceAudio.Clear();
            lockedLevers.Clear();
            stations.Clear();
            byStation.Clear();
            moduleIndex.Clear();
            moduleStation.Clear();
            carCache.Clear();

            stateDirty = false;
            lastSentEmpty = true;

            Main.Log("IMMERSIVE SERVICE -> world unloaded, state cleared");
        }

        // =====================================================
        // STATION REGISTRY
        // =====================================================
        public static void RegisterStation(PitStopStation station)
        {
            if (station == null)
                return;

            StationState? known;

            if (byStation.TryGetValue(station, out known) && known != null)
                return;

            string key = MakeStationKey(station);

            StationState? state;

            if (!stations.TryGetValue(key, out state) || state == null)
            {
                state = new StationState { Key = key };
                stations[key] = state;
            }

            state.Station = station;

            LocoResourceModule[] modules = new LocoResourceModule[0];

            if (station.locoResourceModules != null && station.locoResourceModules.resourceModules != null)
                modules = station.locoResourceModules.resourceModules;

            state.Modules = modules;
            state.Rows = new ModuleRow?[modules.Length];
            state.TotalText = null;
            state.LastTotal = "\u0001";
            state.VisualsActive = false;

            byStation[station] = state;

            for (int i = 0; i < modules.Length; i++)
            {
                LocoResourceModule module = modules[i];

                if (module == null)
                    continue;

                moduleIndex[module] = i;
                moduleStation[module] = state;
            }

            Main.Log($"IMMERSIVE SERVICE -> pit stop registered {key} | Modules={modules.Length}");
        }

        private static void ScanStations()
        {
            PitStopStation[] found = UnityEngine.Object.FindObjectsOfType<PitStopStation>();

            for (int i = 0; i < found.Length; i++)
            {
                RegisterStation(found[i]);
            }
        }

        private static string MakeStationKey(PitStopStation station)
        {
            Vector3 p = station.transform.position - WorldMover.currentMove;

            return Mathf.RoundToInt(p.x) + ":" + Mathf.RoundToInt(p.y) + ":" + Mathf.RoundToInt(p.z);
        }

        private static StationState GetOrCreateState(string key)
        {
            StationState? state;

            if (!stations.TryGetValue(key, out state) || state == null)
            {
                state = new StationState { Key = key };
                stations[key] = state;
            }

            return state;
        }

        private static bool TryGetModule(LocoResourceModule module, out StationState? state, out int index)
        {
            state = null;
            index = -1;

            if (module == null)
                return false;

            if (moduleStation.TryGetValue(module, out state) && state != null && moduleIndex.TryGetValue(module, out index))
                return true;

            // fallback: station not registered yet
            if (modulePitStopStationRef != null)
            {
                PitStopStation station = modulePitStopStationRef(module);

                if (station != null)
                {
                    RegisterStation(station);

                    if (moduleStation.TryGetValue(module, out state) && state != null && moduleIndex.TryGetValue(module, out index))
                        return true;
                }
            }

            state = null;
            index = -1;
            return false;
        }

        // =====================================================
        // REPAIR OF AN EXPLODED CAR (host / singleplayer)
        // An exploded car may be repaired loaded – its destroyed cargo is removed when the repair is paid,
        // so the repaired car doesn't keep a load (and doesn't get a DH job for it). A not yet accepted job of the
        // car is deleted (it was made for the damaged / loaded car); the car gets a new job with the next job generation.
        // =====================================================
        /// <summary>Order data per car of a resource module (for the repair fee rules).</summary>
        public static List<CashRegisterModule.CashRegisterModuleData>? ResourceData(LocoResourceModule module) =>
            module != null && resourceDataRef != null ? resourceDataRef(module) : null;

        /// <summary>Car selected at the pit stop of this module (null if none).</summary>
        public static TrainCar? SelectedCar(LocoResourceModule module)
        {
            try
            {
                PitStopStation? station = module != null && modulePitStopStationRef != null ? modulePitStopStationRef(module) : null;
                PitStop? pit = station != null ? station.pitstop : null;
                return pit != null && pit.IsCarInPitStop() ? pit.SelectedCar : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Refresh the register display after changing an order.</summary>
        public static void RefreshRegister(DV.CashRegister.CashRegisterWithModules register)
        {
            try
            {
                registerUnitsChangedMethod?.Invoke(register, null);
            }
            catch
            {
                // display only
            }
        }

        public static void RemoveDestroyedCargoBeforeRepair(DV.CashRegister.CashRegisterWithModules register)
        {
            if (ImmersiveServiceMultiplayer.IsClientOnly || register == null || register.registerModules == null ||
                resourceDataRef == null || modulePitStopStationRef == null)
            {
                return;
            }

            foreach (CashRegisterModule baseModule in register.registerModules)
            {
                if (!(baseModule is LocoResourceModule module) || module.resourceType != ResourceType.Car_DMG)
                    continue;

                List<CashRegisterModule.CashRegisterModuleData> data = resourceDataRef(module);

                bool repairOrdered = false;

                if (data != null)
                {
                    foreach (CashRegisterModule.CashRegisterModuleData entry in data)
                    {
                        if (entry != null && entry.unitsToBuy > 0.0001f)
                            repairOrdered = true;
                    }
                }

                if (!repairOrdered)
                    continue;

                PitStopStation station = modulePitStopStationRef(module);
                PitStop? pit = station != null ? station.pitstop : null;
                TrainCar? car = pit != null && pit.IsCarInPitStop() ? pit.SelectedCar : null;

                if (car == null || car.logicCar == null)
                    continue;

                DV.Logic.Job.Car logic = car.logicCar;

                if (logic.CurrentCargoTypeInCar == CargoType.None || logic.LoadedCargoAmount <= 0f)
                    continue;

                // only exploded cars – cargo at 0 % alone (e.g. a destroyed container) stays on the car
                if (!car.isExploded)
                    continue;

                CargoType cargo = logic.CurrentCargoTypeInCar;

                try
                {
                    // vanilla unload (events -> cargo model, multiplayer)
                    logic.UnloadCargo(logic.LoadedCargoAmount, cargo, null);
                }
                catch
                {
                    logic.DumpCargo();
                }

                Main.Log($"IMMERSIVE SERVICE -> {car.ID}: destroyed cargo ({cargo}) removed for the repair");

                try
                {
                    DV.Logic.Job.JobsManager jobs = DV.Logic.Job.JobsManager.Instance;
                    DV.Logic.Job.Job? job = jobs != null ? jobs.GetJobOfCar(logic, false) : null;

                    if (job != null && job.State == JobState.Available && DamagedJobTrash.Expire(job))
                        Main.Log($"IMMERSIVE SERVICE -> {car.ID}: not yet accepted job {job.ID} deleted (cargo removed)");
                }
                catch
                {
                    // job stays
                }
            }
        }

        // =====================================================
        // BEFORE PAYING (host / singleplayer)
        // A module with nozzle / charging plug only takes an order if the plug is still connected.
        // Otherwise its selection is removed BEFORE the price is taken – only this module is cancelled.
        // =====================================================
        public static void CheckConnectionsBeforeBuy(DV.CashRegister.CashRegisterWithModules register)
        {
            ImmersiveServiceConfig cfg = Config;

            if (!cfg.Enabled || ImmersiveServiceMultiplayer.IsClientOnly || register == null || register.registerModules == null || resourceDataRef == null)
                return;

            bool changed = false;

            for (int m = 0; m < register.registerModules.Length; m++)
            {
                LocoResourceModule? module = register.registerModules[m] as LocoResourceModule;

                if (module == null)
                    continue;

                if (cfg.GetSecondsPerUnit(module.resourceType) <= 0f)
                    continue; // vanilla module (water / coal / 0 s) – untouched

                // module state READY = nozzle / plug connected to the vehicle.
                // (PluggableObject.State is also "PluggedIn" while the nozzle rests in the pump holder,
                //  so the module state is the only reliable check – status UNPLUGGED = not ready)
                if (module.IsReady)
                    continue;

                List<CashRegisterModule.CashRegisterModuleData> data = resourceDataRef(module);

                if (data == null)
                    continue;

                for (int i = 0; i < data.Count; i++)
                {
                    CashRegisterModule.CashRegisterModuleData entry = data[i];

                    if (entry == null || Mathf.Abs(entry.unitsToBuy) < 0.0001f)
                        continue;

                    Main.Log($"IMMERSIVE SERVICE -> {module.resourceType} order cancelled: not connected to the vehicle | Units={entry.unitsToBuy:F2}");

                    entry.unitsToBuy = 0f;
                    changed = true;
                }
            }

            if (changed && registerUnitsChangedMethod != null)
            {
                try
                {
                    registerUnitsChangedMethod.Invoke(register, null);
                }
                catch
                {
                    // display refresh only
                }
            }
        }

        // =====================================================
        // PURCHASE (host / singleplayer)
        // returns false = original GetBoughtResource is skipped
        // =====================================================
        public static bool HandleBoughtResource(LocoResourceModule module)
        {
            ImmersiveServiceConfig cfg = Config;

            if (!cfg.Enabled)
                return true;

            // In multiplayer the host processes every purchase.
            if (ImmersiveServiceMultiplayer.IsClientOnly)
                return true;

            float secondsPerUnit = cfg.GetSecondsPerUnit(module.resourceType);

            if (secondsPerUnit <= 0f)
                return true; // vanilla for this module

            StationState? state;
            int index;

            if (!TryGetModule(module, out state, out index) || state == null)
                return true;

            PitStopStation? station = state.Station;

            if (station == null || station.pitstop == null || resourceDataRef == null)
                return true;

            List<CashRegisterModule.CashRegisterModuleData> data = resourceDataRef(module);

            if (data == null)
                return true;

            Action<CarPitStopParametersBase, ResourceType, float>? onBought =
                onResourceBoughtField != null ? onResourceBoughtField.GetValue(module) as Action<CarPitStopParametersBase, ResourceType, float> : null;

            Action<float, bool>? globalBought =
                globalBoughtField != null ? globalBoughtField.GetValue(null) as Action<float, bool> : null;

            float minLimit = module.BuyMinLimit;

            for (int i = 0; i < data.Count; i++)
            {
                CashRegisterModule.CashRegisterModuleData entry = data[i];

                if (entry == null)
                    continue;

                float units = entry.unitsToBuy;

                if (units <= minLimit)
                    continue;

                CarPitStopParametersBase? carParams = null;

                try
                {
                    carParams = station.pitstop.GetCarParameters(i);
                }
                catch (Exception ex)
                {
                    Main.LogWarning("IMMERSIVE SERVICE -> car parameters not readable: " + ex.Message);
                }

                TrainCar? car = entry.car;
                float price = entry.TotalPrice;

                if (car != null && carParams != null && Mathf.Abs(units) > 0.0001f)
                {
                    CreateJob(state, index, module.resourceType, car, carParams, units, price, secondsPerUnit);
                }
                else if (onBought != null && carParams != null)
                {
                    // vanilla instant behaviour
                    onBought(carParams, module.resourceType, units);
                }

                bool uniqueOrPlayerSpawned = car != null && (car.uniqueCar || car.playerSpawnedCar);

                if (globalBought != null)
                    globalBought(price, uniqueOrPlayerSpawned);

                entry.unitsToBuy = 0f;
            }

            DispatchAll();
            RebuildHostViews();

            // module is busy now: stop a held lever and lock it right away
            StopModuleFlow(module);
            visualTimer = 0f;

            return false;
        }

        private static void CreateJob(StationState state, int module, ResourceType resource, TrainCar car, CarPitStopParametersBase carParams, float units, double price, float secondsPerUnit)
        {
            ServiceJob job = new ServiceJob
            {
                Id = ++nextJobId,
                StationKey = state.Key,
                ModuleIndex = module,
                Resource = resource,
                CarGuid = car.CarGUID ?? string.Empty,
                Car = car,
                Params = carParams,
                TotalUnits = units,
                AppliedUnits = 0f,
                TotalPrice = price,
                SecondsPerUnit = secondsPerUnit,
                Duration = Mathf.Abs(units) * secondsPerUnit,
                Elapsed = 0f,
                State = ServiceJobState.Queued,
                Order = ++nextOrder,
                StartPosition = AbsolutePosition(car)
            };

            jobs.Add(job);
            stateDirty = true;

            Main.Log(
                $"IMMERSIVE SERVICE -> job #{job.Id} queued | Station={state.Key} | Module={module} ({resource}) | " +
                $"Car={car.ID} | Units={units:F2} | Duration={FormatTime(job.Duration)} | Price={price:F2}");
        }

        // =====================================================
        // HOST SIMULATION
        // =====================================================
        private static void TickHost(float dt)
        {
            if (jobs.Count > 0)
            {
                List<ServiceJob>? invalid = null;
                HashSet<TrainCar>? moved = null;

                for (int i = 0; i < jobs.Count; i++)
                {
                    ServiceJob job = jobs[i];

                    if (job.Car == null || job.Params == null)
                    {
                        if (invalid == null)
                            invalid = new List<ServiceJob>();

                        invalid.Add(job);
                        continue;
                    }

                    if (HasMoved(job))
                    {
                        if (moved == null)
                            moved = new HashSet<TrainCar>();

                        moved.Add(job.Car);
                    }
                }

                if (invalid != null)
                {
                    for (int i = 0; i < invalid.Count; i++)
                        Abort(invalid[i], "vehicle removed");
                }

                if (moved != null)
                {
                    ServiceJob[] snapshot = jobs.ToArray();

                    for (int i = 0; i < snapshot.Length; i++)
                    {
                        TrainCar? car = snapshot[i].Car;

                        if (car != null && moved.Contains(car))
                            Abort(snapshot[i], "vehicle moved");
                    }
                }

                for (int i = jobs.Count - 1; i >= 0; i--)
                {
                    ServiceJob job = jobs[i];

                    if (job.State != ServiceJobState.Running)
                        continue;

                    job.Elapsed += dt;

                    if (job.Elapsed >= job.Duration)
                    {
                        job.Elapsed = job.Duration;
                        ApplyProgress(job, true);
                        Complete(job);
                    }
                    else
                    {
                        ApplyProgress(job, false);
                    }
                }

                DispatchAll();
            }

            viewTimer -= Time.unscaledDeltaTime;

            if (stateDirty || viewTimer <= 0f)
            {
                viewTimer = ViewRebuildInterval;
                RebuildHostViews();
            }

            // ---------------- network ----------------
            stateTimer -= Time.unscaledDeltaTime;

            bool empty = jobs.Count == 0 && finishedMarkers.Count == 0;
            bool resend = !empty && stateTimer <= 0f;

            if (stateDirty || resend || (empty && !lastSentEmpty))
            {
                stateDirty = false;
                stateTimer = StateResendInterval;
                lastSentEmpty = empty;

                ImmersiveServiceMultiplayer.SendState(BuildSnapshot());
            }
        }

        private static bool HasMoved(ServiceJob job)
        {
            TrainCar? car = job.Car;

            if (car == null)
                return true;

            if (car.derailed)
                return true;

            Vector3 delta = AbsolutePosition(car) - job.StartPosition;

            return delta.sqrMagnitude > MoveAbortDistance * MoveAbortDistance;
        }

        private static Vector3 AbsolutePosition(TrainCar car)
        {
            return car.transform.position - WorldMover.currentMove;
        }

        private static void ApplyProgress(ServiceJob job, bool force)
        {
            if (job.Params == null)
                return;

            float fraction = job.Duration <= 0.0001f ? 1f : Mathf.Clamp01(job.Elapsed / job.Duration);
            float target = job.TotalUnits * fraction;
            float delta = target - job.AppliedUnits;

            if (!force && Time.time < job.NextApplyTime)
                return;

            job.NextApplyTime = Time.time + ApplyInterval;

            if (Mathf.Abs(delta) < 0.00001f)
                return;

            try
            {
                Dictionary<ResourceType, LocoParameterData> parameters = job.Params.GetCarPitStopParameters();

                if (parameters != null && parameters.ContainsKey(job.Resource))
                {
                    job.Params.UpdateCarPitStopParameter(job.Resource, delta);
                }
            }
            catch (Exception ex)
            {
                Main.LogWarning($"IMMERSIVE SERVICE -> apply failed for job #{job.Id}: {ex.Message}");
            }

            job.AppliedUnits = target;
        }

        private static void Complete(ServiceJob job)
        {
            jobs.Remove(job);
            stateDirty = true;

            ServiceJobView marker = new ServiceJobView
            {
                JobId = job.Id,
                ModuleIndex = job.ModuleIndex,
                CarGuid = job.CarGuid,
                State = ServiceJobState.Finished,
                Remaining = 0f,
                Eta = 0f
            };

            finishedMarkers.Add(new FinishedMarker { Until = Time.time + FinishedMarkerTime, Station = job.StationKey, View = marker });

            Main.Log($"IMMERSIVE SERVICE -> job #{job.Id} finished | Car={job.CarGuid} | Units={job.TotalUnits:F2}");
        }

        private static void Abort(ServiceJob job, string reason)
        {
            if (job.State == ServiceJobState.Running)
                ApplyProgress(job, true);

            float done = Mathf.Abs(job.TotalUnits) > 0.0001f ? Mathf.Clamp01(job.AppliedUnits / job.TotalUnits) : 1f;
            double refund = job.TotalPrice * (1.0 - done);

            jobs.Remove(job);
            stateDirty = true;

            if (Config.RefundUnfinished && refund > 0.01)
            {
                try
                {
                    Inventory.Instance.AddMoney(refund);
                }
                catch (Exception ex)
                {
                    Main.LogWarning("IMMERSIVE SERVICE -> refund failed: " + ex.Message);
                }
            }

            Main.Log(
                $"IMMERSIVE SERVICE -> job #{job.Id} aborted ({reason}) | Car={job.CarGuid} | Done={done * 100f:F1}% | " +
                $"Refund={(Config.RefundUnfinished ? refund : 0.0):F2}");
        }

        // ---------------- dispatching ----------------
        private static void DispatchAll()
        {
            if (jobs.Count == 0)
                return;

            int limit = Config.ParallelLimit;

            Dictionary<string, int> running = new Dictionary<string, int>();
            HashSet<string> busyModules = new HashSet<string>();

            for (int i = 0; i < jobs.Count; i++)
            {
                ServiceJob job = jobs[i];

                if (job.State != ServiceJobState.Running)
                    continue;

                int count;
                running.TryGetValue(job.StationKey, out count);
                running[job.StationKey] = count + 1;
                busyModules.Add(job.StationKey + "#" + job.ModuleIndex);
            }

            for (int i = 0; i < jobs.Count; i++)
            {
                ServiceJob job = jobs[i];

                if (job.State != ServiceJobState.Queued)
                    continue;

                int count;
                running.TryGetValue(job.StationKey, out count);

                if (count >= limit)
                    continue;

                string moduleKey = job.StationKey + "#" + job.ModuleIndex;

                if (busyModules.Contains(moduleKey))
                    continue;

                job.State = ServiceJobState.Running;
                job.NextApplyTime = Time.time + ApplyInterval;
                running[job.StationKey] = count + 1;
                busyModules.Add(moduleKey);
                stateDirty = true;

                Main.Log($"IMMERSIVE SERVICE -> job #{job.Id} started | Module={job.ModuleIndex} ({job.Resource}) | Duration={FormatTime(job.Duration)}");
            }
        }

        // ---------------- views (host) ----------------
        private static void RebuildHostViews()
        {
            foreach (StationState state in stations.Values)
            {
                state.Views.Clear();
                state.ViewTime = Time.time;
            }

            // finished markers (expire after a moment)
            for (int m = finishedMarkers.Count - 1; m >= 0; m--)
            {
                FinishedMarker marker = finishedMarkers[m];

                if (marker.Until < Time.time)
                {
                    finishedMarkers.RemoveAt(m);
                    continue;
                }

                GetOrCreateState(marker.Station).Views.Add(marker.View);
            }

            if (jobs.Count == 0)
                return;

            int limit = Config.ParallelLimit;

            Dictionary<string, List<ServiceJob>> grouped = new Dictionary<string, List<ServiceJob>>();

            for (int i = 0; i < jobs.Count; i++)
            {
                ServiceJob job = jobs[i];
                List<ServiceJob>? list;

                if (!grouped.TryGetValue(job.StationKey, out list) || list == null)
                {
                    list = new List<ServiceJob>();
                    grouped[job.StationKey] = list;
                }

                list.Add(job);
            }

            foreach (KeyValuePair<string, List<ServiceJob>> pair in grouped)
            {
                StationState state = GetOrCreateState(pair.Key);
                List<ServiceJob> list = pair.Value;
                List<SimItem> items = new List<SimItem>(list.Count);

                for (int i = 0; i < list.Count; i++)
                {
                    ServiceJob job = list[i];

                    items.Add(new SimItem
                    {
                        Module = job.ModuleIndex,
                        Running = job.State == ServiceJobState.Running,
                        Remaining = Mathf.Max(0f, job.Duration - job.Elapsed),
                        Duration = job.Duration
                    });
                }

                Simulate(items, limit);

                for (int i = 0; i < list.Count; i++)
                {
                    state.Views.Add(new ServiceJobView
                    {
                        JobId = list[i].Id,
                        ModuleIndex = list[i].ModuleIndex,
                        CarGuid = list[i].CarGuid,
                        State = list[i].State,
                        Remaining = Mathf.Max(0f, list[i].Duration - list[i].Elapsed),
                        Eta = items[i].Eta
                    });
                }

                state.ViewTime = Time.time;
            }
        }

        // Greedy list scheduling – exactly what DispatchAll does over time.
        // Returns the makespan, fills Eta of every item.
        private static float Simulate(List<SimItem> items, int limit)
        {
            int count = items.Count;

            if (count == 0)
                return 0f;

            float[] finish = new float[count];
            List<int> runningList = new List<int>();
            List<int> queued = new List<int>();

            for (int i = 0; i < count; i++)
            {
                if (items[i].Running)
                {
                    finish[i] = items[i].Remaining;
                    runningList.Add(i);
                }
                else
                {
                    queued.Add(i);
                }
            }

            float time = 0f;
            float makespan = 0f;
            int guard = 0;

            while ((runningList.Count > 0 || queued.Count > 0) && guard++ < 10000)
            {
                for (int q = 0; q < queued.Count && runningList.Count < limit;)
                {
                    int candidate = queued[q];
                    bool busy = false;

                    for (int r = 0; r < runningList.Count; r++)
                    {
                        if (items[runningList[r]].Module == items[candidate].Module)
                        {
                            busy = true;
                            break;
                        }
                    }

                    if (busy)
                    {
                        q++;
                        continue;
                    }

                    finish[candidate] = time + items[candidate].Duration;
                    runningList.Add(candidate);
                    queued.RemoveAt(q);
                }

                if (runningList.Count == 0)
                    break;

                float next = float.MaxValue;

                for (int r = 0; r < runningList.Count; r++)
                    next = Mathf.Min(next, finish[runningList[r]]);

                time = next;

                for (int r = runningList.Count - 1; r >= 0; r--)
                {
                    int index = runningList[r];

                    if (finish[index] <= time + 0.0001f)
                    {
                        SimItem item = items[index];
                        item.Eta = finish[index];
                        items[index] = item;
                        makespan = Mathf.Max(makespan, finish[index]);
                        runningList.RemoveAt(r);
                    }
                }
            }

            return makespan;
        }

        // =====================================================
        // NETWORK SNAPSHOT
        // =====================================================
        public static List<KeyValuePair<string, List<ServiceJobView>>> BuildSnapshot()
        {
            List<KeyValuePair<string, List<ServiceJobView>>> snapshot = new List<KeyValuePair<string, List<ServiceJobView>>>();

            foreach (StationState state in stations.Values)
            {
                if (state.Views.Count == 0)
                    continue;

                float age = Time.time - state.ViewTime;
                List<ServiceJobView> copy = new List<ServiceJobView>(state.Views.Count);

                for (int i = 0; i < state.Views.Count; i++)
                {
                    ServiceJobView view = state.Views[i];
                    view.Eta = Mathf.Max(0f, view.Eta - age);

                    if (view.State == ServiceJobState.Running)
                        view.Remaining = Mathf.Max(0f, view.Remaining - age);
                    copy.Add(view);
                }

                snapshot.Add(new KeyValuePair<string, List<ServiceJobView>>(state.Key, copy));
            }

            return snapshot;
        }

        public static void ApplyRemoteState(List<KeyValuePair<string, List<ServiceJobView>>> snapshot)
        {
            foreach (StationState state in stations.Values)
            {
                state.Views.Clear();
                state.ViewTime = Time.time;
            }

            for (int i = 0; i < snapshot.Count; i++)
            {
                StationState state = GetOrCreateState(snapshot[i].Key);
                state.Views.AddRange(snapshot[i].Value);
                state.ViewTime = Time.time;
            }

            visualTimer = 0f;
        }

        // =====================================================
        // VISUALS (every machine)
        // =====================================================
        private static void UpdateVisuals()
        {
            ImmersiveServiceConfig cfg = Config;

            foreach (StationState state in stations.Values)
            {
                PitStopStation? station = state.Station;

                if (station == null || station.pitstop == null)
                    continue;

                if (!cfg.Enabled && state.Views.Count == 0)
                {
                    if (state.VisualsActive)
                        HideStation(state);

                    continue;
                }

                state.VisualsActive = true;

                PitStop pit = station.pitstop;
                TrainCar? selected = pit.IsCarInPitStop() ? pit.SelectedCar : null;
                string selectedGuid = selected != null ? (selected.CarGUID ?? string.Empty) : string.Empty;
                float age = Time.time - state.ViewTime;
                bool[] runningModules = new bool[state.Modules.Length];

                for (int i = 0; i < state.Modules.Length; i++)
                {
                    LocoResourceModule module = state.Modules[i];

                    if (module == null)
                        continue;

                    bool moduleRunning = false;
                    bool moduleQueued = false;
                    bool hasSelectedJob = false;
                    bool selectedRunning = false;
                    float selectedRemaining = 0f;

                    for (int v = 0; v < state.Views.Count; v++)
                    {
                        ServiceJobView view = state.Views[v];

                        if (view.ModuleIndex != i)
                            continue;

                        if (view.State == ServiceJobState.Finished)
                        {
                            // module finished normally → ding once (host and clients)
                            if (dingedJobs.Add(view.JobId))
                                ImmersiveServiceAudio.PlayDing(module);

                            continue;
                        }

                        bool running = view.State == ServiceJobState.Running;

                        if (running)
                            moduleRunning = true;
                        else
                            moduleQueued = true;

                        if (string.IsNullOrEmpty(selectedGuid) || view.CarGuid != selectedGuid)
                            continue;

                        // own time of this module only – counts down only while the module works
                        float remaining = running ? Mathf.Max(0f, view.Remaining - age) : view.Remaining;

                        if (!hasSelectedJob || (running && !selectedRunning))
                        {
                            selectedRemaining = remaining;
                            selectedRunning = running;
                        }

                        hasSelectedJob = true;
                    }

                    string label;
                    string value;

                    if (hasSelectedJob)
                    {
                        label = L(selectedRunning ? TrainServiceReworkTranslations.PitRemainingKey : TrainServiceReworkTranslations.PitDurationKey);
                        value = FormatTime(selectedRemaining);
                    }
                    else if (cfg.Enabled && selected != null && module.AbsoluteMaxValue > 0f && cfg.GetSecondsPerUnit(module.resourceType) > 0f)
                    {
                        // only for compatible modules that actually take time
                        float seconds = Mathf.Abs(module.UnitsToBuy) * cfg.GetSecondsPerUnit(module.resourceType);
                        label = L(TrainServiceReworkTranslations.PitDurationKey);
                        value = FormatTime(seconds);
                    }
                    else
                    {
                        label = string.Empty;
                        value = string.Empty;
                    }

                    SetRow(state, i, module, label, value);

                    runningModules[i] = moduleRunning;

                    // a busy module can't take a second order, and its nozzle / plug stays in the vehicle
                    SetLeverLocked(module, moduleRunning || moduleQueued);
                    SetHoseLocked(module, moduleRunning || moduleQueued);

                    if (moduleRunning)
                        SetLampMode(module, 2);
                    else if (moduleQueued)
                        SetLampMode(module, 1);
                    else
                        SetLampMode(module, 0);
                }

                // ---------------- main display: total ----------------
                string total = string.Empty;

                bool anyActive = false;
                float max = 0f;

                for (int v = 0; v < state.Views.Count; v++)
                {
                    if (state.Views[v].State == ServiceJobState.Finished)
                        continue;

                    anyActive = true;
                    max = Mathf.Max(max, state.Views[v].Eta - age);
                }

                if (anyActive)
                {
                    total = L(TrainServiceReworkTranslations.PitRemainingKey) + " " + FormatTime(max);
                }
                else if (cfg.Enabled && selected != null)
                {
                    List<SimItem> items = new List<SimItem>();

                    for (int i = 0; i < state.Modules.Length; i++)
                    {
                        LocoResourceModule module = state.Modules[i];

                        if (module == null)
                            continue;

                        float seconds = Mathf.Abs(module.UnitsToBuy) * cfg.GetSecondsPerUnit(module.resourceType);

                        if (seconds <= 0f)
                            continue;

                        items.Add(new SimItem { Module = i, Running = false, Duration = seconds });
                    }

                    float makespan = Simulate(items, cfg.ParallelLimit);

                    if (makespan > 0f)
                        total = L(TrainServiceReworkTranslations.PitDurationKey) + " " + FormatTime(makespan);
                }

                SetTotal(state, pit, total);

                // sounds (every machine hears the same – driven by the job views)
                ImmersiveServiceAudio.UpdateStation(station, state.Modules, runningModules, cfg.Enabled);
            }
        }

        private static void HideStation(StationState state)
        {
            state.VisualsActive = false;

            for (int i = 0; i < state.Modules.Length; i++)
            {
                LocoResourceModule module = state.Modules[i];

                if (module == null)
                    continue;

                SetRow(state, i, module, string.Empty, string.Empty);
                SetLeverLocked(module, false);
                SetHoseLocked(module, false);
                SetLampMode(module, 0);
            }

            if (state.Station != null && state.Station.pitstop != null)
                SetTotal(state, state.Station.pitstop, string.Empty);

            if (state.Station != null)
                ImmersiveServiceAudio.StopStation(state.Station, state.Modules);
        }

        private static void SetRow(StationState state, int index, LocoResourceModule module, string label, string value)
        {
            if (state.Rows.Length != state.Modules.Length)
                state.Rows = new ModuleRow?[state.Modules.Length];

            ModuleRow? row = state.Rows[index];

            if (row == null || row.Label == null || row.Value == null)
            {
                if (string.IsNullOrEmpty(label) && string.IsNullOrEmpty(value))
                    return; // nothing to show, don't create objects yet

                row = CreateRow(module);
                state.Rows[index] = row;

                if (row == null)
                    return;
            }

            if (row.Label != null && row.LastLabel != label)
            {
                row.LastLabel = label;
                row.Label.text = label;
            }

            if (row.Value != null && row.LastValue != value)
            {
                row.LastValue = value;
                row.Value.text = value;
            }
        }

        private static ModuleRow? CreateRow(LocoResourceModule module)
        {
            TextMeshPro staticText = module.differenceValueStaticText;
            TextMeshPro valueText = module.differenceValueText;
            TextMeshPro priceText = module.pricePerUnitText;

            if (staticText == null || valueText == null || priceText == null)
                return null;

            // free line = half way between REPAIR row and UNIT COST row
            Vector3 offset = (priceText.transform.position - valueText.transform.position) * 0.5f;

            TextMeshPro? label = CloneText(staticText, "TSR_ServiceTimeLabel", offset);
            TextMeshPro? value = CloneText(valueText, "TSR_ServiceTimeValue", offset);

            if (label == null || value == null)
                return null;

            return new ModuleRow { Label = label, Value = value };
        }

        private static void SetTotal(StationState state, PitStop pit, string text)
        {
            if (state.TotalText == null)
            {
                if (string.IsNullOrEmpty(text))
                    return;

                TextMeshPro manual = pit.manualServiceText;
                TextMeshPro selectedText = pit.selectedCarText;

                if (manual == null || selectedText == null)
                    return;

                Vector3 offset = (selectedText.transform.position - manual.transform.position) * 0.5f;
                state.TotalText = CloneText(manual, "TSR_ServiceTimeTotal", offset);
                state.LastTotal = "\u0001";

                if (state.TotalText == null)
                    return;
            }

            if (state.LastTotal == text)
                return;

            state.LastTotal = text;
            state.TotalText.text = text;
        }

        private static TextMeshPro? CloneText(TextMeshPro source, string name, Vector3 worldOffset)
        {
            try
            {
                Transform sourceTransform = source.transform;
                GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, sourceTransform.parent);
                clone.name = name;

                // keep only the text renderer – no localization / update scripts on the copy
                MonoBehaviour[] behaviours = clone.GetComponents<MonoBehaviour>();

                for (int i = 0; i < behaviours.Length; i++)
                {
                    if (!(behaviours[i] is TMP_Text))
                        UnityEngine.Object.Destroy(behaviours[i]);
                }

                clone.transform.position = sourceTransform.position + worldOffset;
                clone.transform.rotation = sourceTransform.rotation;
                clone.transform.localScale = sourceTransform.localScale;

                TextMeshPro text = clone.GetComponent<TextMeshPro>();

                if (text == null)
                    return null;

                text.text = string.Empty;
                return text;
            }
            catch (Exception ex)
            {
                Main.LogWarning("IMMERSIVE SERVICE -> display line could not be created: " + ex.Message);
                return null;
            }
        }

        // =====================================================
        // MODULE LEVER LOCK
        // =====================================================
        private static void SetLeverLocked(LocoResourceModule module, bool locked)
        {
            ControlImplBase? lever;

            if (!moduleLevers.TryGetValue(module, out lever))
            {
                lever = module.GetComponentInChildren<ControlImplBase>(true);
                moduleLevers[module] = lever;
            }

            if (lever == null)
                return;

            if (locked)
            {
                if (lockedLevers.Contains(lever) || !lever.InteractionAllowed)
                    return;

                lever.InteractionAllowed = false;
                lockedLevers.Add(lever);
            }
            else if (lockedLevers.Remove(lever))
            {
                lever.InteractionAllowed = true;
            }
        }

        // ---------------- nozzle / charging plug ----------------
        private static void SetHoseLocked(LocoResourceModule module, bool locked)
        {
            PluggableObject hose = module.resourceHose;

            if (hose == null)
                return;

            List<Collider>? disabled;
            bool isLocked = lockedHoses.TryGetValue(hose, out disabled) && disabled != null;

            if (locked)
            {
                if (isLocked)
                    return;

                // only lock while it is actually connected to the vehicle (module READY) and not held
                if (!module.IsReady || hose.State != PluggableObject.PluggableState.PluggedIn || hose.IsHeldInHand)
                    return;

                List<Collider> colliders = new List<Collider>();
                ControlImplBase? control = plugControlRef != null ? plugControlRef(hose) : null;

                // no collider = no raycast = can't be grabbed / telegrabbed
                Component root = control != null ? (Component)control : hose;

                foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                {
                    if (collider != null && collider.enabled)
                    {
                        collider.enabled = false;
                        colliders.Add(collider);
                    }
                }

                lockedHoses[hose] = colliders;
            }
            else if (isLocked && disabled != null)
            {
                lockedHoses.Remove(hose);

                for (int i = 0; i < disabled.Count; i++)
                {
                    if (disabled[i] != null)
                        disabled[i].enabled = true;
                }
            }
        }

        public static bool IsHoseLocked(PluggableObject hose)
        {
            return lockedHoses.Count > 0 && hose != null && lockedHoses.ContainsKey(hose);
        }

        public static bool IsModuleBusy(LocoResourceModule module)
        {
            if (module == null)
                return false;

            StationState? state;
            int index;

            if (!moduleStation.TryGetValue(module, out state) || state == null || !moduleIndex.TryGetValue(module, out index))
                return false;

            for (int v = 0; v < state.Views.Count; v++)
            {
                if (state.Views[v].ModuleIndex == index && state.Views[v].State != ServiceJobState.Finished)
                    return true;
            }

            return false;
        }

        // called instead of the lever handler while the module is busy
        public static void StopModuleFlow(LocoResourceModule module)
        {
            if (flowMultiplierRef != null)
                flowMultiplierRef(module) = 0;
        }

        // =====================================================
        // LAMPS: 0 = vanilla, 1 = steady yellow, 2 = blinking yellow
        // =====================================================
        private static void SetLampMode(LocoResourceModule module, int mode)
        {
            LampControl lamp = module.lamp;

            if (lamp == null)
                return;

            LampOverride? existing;
            bool hasOverride = lampOverrides.TryGetValue(lamp, out existing) && existing != null;

            if (mode == 0)
            {
                if (hasOverride && existing != null)
                    ReleaseLamp(lamp, existing);

                return;
            }

            LampOverride lampOverride;

            if (hasOverride && existing != null)
            {
                lampOverride = existing;
            }
            else
            {
                lampOverride = new LampOverride();
                IndicatorEmission indicator = lamp.lampInd;

                if (indicator != null)
                {
                    lampOverride.Emission = indicator.emissionColor;
                    lampOverride.Glare = indicator.glareColor;

                    indicator.emissionColor = Yellow(lampOverride.Emission);
                    indicator.glareColor = Yellow(lampOverride.Glare);

                    if (indicator.emissionLight != null)
                    {
                        lampOverride.HasLight = true;
                        lampOverride.LightColor = indicator.emissionLight.color;
                        indicator.emissionLight.color = new Color(1f, 0.75f, 0.15f, lampOverride.LightColor.a);
                    }
                }

                lampOverrides[lamp] = lampOverride;
            }

            if (lampOverride.Mode == mode)
                return;

            lampOverride.Mode = mode;
            SetLampStateBypass(lamp, mode == 2 ? LampControl.LampState.Blinking : LampControl.LampState.On);
            RefreshEmission(lamp);
        }

        private static void ReleaseLamp(LampControl lamp, LampOverride lampOverride)
        {
            lampOverrides.Remove(lamp);

            IndicatorEmission indicator = lamp.lampInd;

            if (indicator != null)
            {
                indicator.emissionColor = lampOverride.Emission;
                indicator.glareColor = lampOverride.Glare;

                if (lampOverride.HasLight && indicator.emissionLight != null)
                    indicator.emissionLight.color = lampOverride.LightColor;
            }

            SetLampStateBypass(lamp, LampControl.LampState.Off);
            RefreshEmission(lamp);
        }

        private static Color Yellow(Color original)
        {
            float intensity = Mathf.Max(original.r, Mathf.Max(original.g, original.b));

            if (intensity <= 0.001f)
                intensity = 1f;

            return new Color(intensity, intensity * 0.72f, 0f, original.a);
        }

        private static void SetLampStateBypass(LampControl lamp, LampControl.LampState state)
        {
            lampBypass = true;

            try
            {
                lamp.SetLampState(state, false);
            }
            finally
            {
                lampBypass = false;
            }
        }

        private static void RefreshEmission(LampControl lamp)
        {
            IndicatorEmission indicator = lamp.lampInd;

            if (indicator == null || emissionSetColorMethod == null)
                return;

            try
            {
                emissionSetColorMethod.Invoke(indicator, new object[] { indicator.EmissionValue });
            }
            catch
            {
                // purely visual
            }
        }

        public static bool AllowLampChange(LampControl lamp)
        {
            if (lampBypass || lampOverrides.Count == 0 || lamp == null)
                return true;

            return !lampOverrides.ContainsKey(lamp);
        }

        private static TrainCar? FindCar(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return null;

            TrainCar? car;

            if (carCache.TryGetValue(guid, out car) && car != null)
                return car;

            if (Time.unscaledTime - carCacheTime < 1f)
                return null;

            carCacheTime = Time.unscaledTime;
            carCache.Clear();

            try
            {
                List<TrainCar> all = CarSpawner.Instance.AllCars;

                for (int i = 0; i < all.Count; i++)
                {
                    TrainCar candidate = all[i];

                    if (candidate != null && !string.IsNullOrEmpty(candidate.CarGUID))
                        carCache[candidate.CarGUID] = candidate;
                }
            }
            catch (Exception ex)
            {
                Main.LogWarning("IMMERSIVE SERVICE -> car lookup failed: " + ex.Message);
            }

            if (carCache.TryGetValue(guid, out car) && car != null)
                return car;

            return null;
        }

        // =====================================================
        // SAVEGAME
        // =====================================================
        public static void WriteSave(SaveGameData data)
        {
            if (data == null || ImmersiveServiceMultiplayer.IsClientOnly)
                return;

            try
            {
                if (jobs.Count == 0 && pendingRestore.Count == 0)
                {
                    data.RemoveData(SaveKey);
                    return;
                }

                JArray list = new JArray();

                for (int i = 0; i < jobs.Count; i++)
                {
                    ServiceJob job = jobs[i];

                    list.Add(new JObject
                    {
                        ["station"] = job.StationKey,
                        ["module"] = job.ModuleIndex,
                        ["resource"] = (int)job.Resource,
                        ["car"] = job.CarGuid,
                        ["total"] = job.TotalUnits,
                        ["applied"] = job.AppliedUnits,
                        ["price"] = job.TotalPrice,
                        ["spu"] = job.SecondsPerUnit,
                        ["duration"] = job.Duration,
                        ["elapsed"] = job.Elapsed,
                        ["state"] = (int)job.State,
                        ["order"] = job.Order
                    });
                }

                // not yet resolved jobs from the last load stay in the save
                for (int i = 0; i < pendingRestore.Count; i++)
                    list.Add(pendingRestore[i]);

                data.SetJObject(SaveKey, new JObject
                {
                    ["version"] = 1,
                    ["jobs"] = list
                });

                Main.Log($"IMMERSIVE SERVICE -> saved {list.Count} job(s)");
            }
            catch (Exception ex)
            {
                Main.LogError("IMMERSIVE SERVICE -> save failed: " + ex);
            }
        }

        private static void ReadSave()
        {
            pendingRestore.Clear();

            try
            {
                SaveGameManager manager = SaveGameManager.Instance;

                if (manager == null || manager.data == null)
                    return;

                JObject root = manager.data.GetJObject(SaveKey);

                if (root == null)
                    return;

                JArray? list = root["jobs"] as JArray;

                if (list == null)
                    return;

                foreach (JToken token in list)
                {
                    JObject? entry = token as JObject;

                    if (entry != null)
                        pendingRestore.Add(entry);
                }

                pendingRestoreSince = Time.unscaledTime;

                Main.Log($"IMMERSIVE SERVICE -> {pendingRestore.Count} job(s) found in savegame");
            }
            catch (Exception ex)
            {
                Main.LogError("IMMERSIVE SERVICE -> reading savegame failed: " + ex);
            }
        }

        private static void ResolvePendingRestore()
        {
            if (pendingRestore.Count == 0)
                return;

            bool timeout = Time.unscaledTime - pendingRestoreSince > RestoreTimeout;
            bool added = false;

            for (int i = pendingRestore.Count - 1; i >= 0; i--)
            {
                JObject entry = pendingRestore[i];

                if (TryRestoreJob(entry))
                {
                    pendingRestore.RemoveAt(i);
                    added = true;
                }
                else if (timeout)
                {
                    Main.LogWarning("IMMERSIVE SERVICE -> saved job dropped (vehicle or pit stop not found): " + entry.ToString(Newtonsoft.Json.Formatting.None));
                    pendingRestore.RemoveAt(i);
                }
            }

            if (added)
            {
                jobs.Sort((a, b) => a.Order.CompareTo(b.Order));
                DispatchAll();
                stateDirty = true;
            }
        }

        private static bool TryRestoreJob(JObject entry)
        {
            string stationKey = (string?)entry["station"] ?? string.Empty;
            string carGuid = (string?)entry["car"] ?? string.Empty;
            int module = (int?)entry["module"] ?? -1;

            StationState? state;

            if (!stations.TryGetValue(stationKey, out state) || state == null || state.Station == null)
                return false;

            if (module < 0 || module >= state.Modules.Length || state.Modules[module] == null)
                return false;

            TrainCar? car = FindCar(carGuid);

            if (car == null)
                return false;

            CarPitStopParametersBase? carParams = car.GetComponent<CarPitStopParametersBase>();

            if (carParams == null)
                carParams = car.GetComponentInChildren<CarPitStopParametersBase>(true);

            if (carParams == null)
                return false;

            ServiceJob job = new ServiceJob
            {
                Id = ++nextJobId,
                StationKey = stationKey,
                ModuleIndex = module,
                Resource = (ResourceType)((int?)entry["resource"] ?? (int)state.Modules[module].resourceType),
                CarGuid = carGuid,
                Car = car,
                Params = carParams,
                TotalUnits = (float?)entry["total"] ?? 0f,
                AppliedUnits = (float?)entry["applied"] ?? 0f,
                TotalPrice = (double?)entry["price"] ?? 0.0,
                SecondsPerUnit = (float?)entry["spu"] ?? 0f,
                Duration = (float?)entry["duration"] ?? 0f,
                Elapsed = (float?)entry["elapsed"] ?? 0f,
                State = ((int?)entry["state"] ?? 0) == 1 ? ServiceJobState.Running : ServiceJobState.Queued,
                Order = (long?)entry["order"] ?? 0L,
                // the car may settle a bit while loading – measure movement from now on
                StartPosition = AbsolutePosition(car)
            };

            if (job.Order > nextOrder)
                nextOrder = job.Order;

            jobs.Add(job);

            Main.Log($"IMMERSIVE SERVICE -> job restored | Car={car.ID} | Module={module} ({job.Resource}) | Remaining={FormatTime(job.Duration - job.Elapsed)}");

            return true;
        }

        // =====================================================
        // HELPERS
        // =====================================================
        private static string L(string key)
        {
            string value = LocalizationAPI.L(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }

        private static AccessTools.FieldRef<T, F>? SafeRef<T, F>(string fieldName) where T : class
        {
            try
            {
                if (AccessTools.Field(typeof(T), fieldName) == null)
                {
                    Main.LogWarning($"IMMERSIVE SERVICE -> field {typeof(T).Name}.{fieldName} not found");
                    return null;
                }

                return AccessTools.FieldRefAccess<T, F>(fieldName);
            }
            catch (Exception ex)
            {
                Main.LogWarning($"IMMERSIVE SERVICE -> field {typeof(T).Name}.{fieldName} not accessible: {ex.Message}");
                return null;
            }
        }
    }

    // =========================================================
    // HARMONY PATCHES
    // =========================================================
    [HarmonyPatch(typeof(LocoResourceModule), nameof(LocoResourceModule.GetBoughtResource))]
    public static class ImmersiveService_GetBoughtResourcePatch
    {
        static bool Prefix(LocoResourceModule __instance)
        {
            try
            {
                return ImmersiveServiceSystem.HandleBoughtResource(__instance);
            }
            catch (Exception ex)
            {
                Main.LogError("IMMERSIVE SERVICE -> purchase handling failed, falling back to vanilla: " + ex);
                return true;
            }
        }
    }

    // Lever of a busy module does nothing (second order into a running one is impossible)
    [HarmonyPatch(typeof(LocoResourceModule), "OnValvePositionChange")]
    public static class ImmersiveService_ModuleLeverPatch
    {
        static bool Prefix(LocoResourceModule __instance)
        {
            if (!ImmersiveServiceSystem.IsModuleBusy(__instance))
                return true;

            ImmersiveServiceSystem.StopModuleFlow(__instance);
            return false;
        }
    }

    // Safety net: a locked nozzle / charging plug is never unplugged (grab, telegrab, use …)
    [HarmonyPatch(typeof(PluggableObject), nameof(PluggableObject.Unplug))]
    public static class ImmersiveService_HoseUnplugPatch
    {
        static bool Prefix(PluggableObject __instance)
        {
            return !ImmersiveServiceSystem.IsHoseLocked(__instance);
        }
    }

    // Exploded car: its destroyed cargo is removed when the repair is paid (after the connection check)
    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterWithModules), nameof(DV.CashRegister.CashRegisterWithModules.Buy))]
    public static class ImmersiveService_BuyRemoveDestroyedCargoPatch
    {
        [HarmonyPriority(Priority.Low)]
        static void Prefix(DV.CashRegister.CashRegisterWithModules __instance)
        {
            try
            {
                ImmersiveServiceSystem.RemoveDestroyedCargoBeforeRepair(__instance);
            }
            catch (Exception ex)
            {
                Main.LogError("IMMERSIVE SERVICE -> removing destroyed cargo failed: " + ex.Message);
            }
        }
    }

    // Plug of the fuel / charging module must still be connected when paying
    [HarmonyPatch(typeof(DV.CashRegister.CashRegisterWithModules), nameof(DV.CashRegister.CashRegisterWithModules.Buy))]
    public static class ImmersiveService_BuyConnectionCheckPatch
    {
        static void Prefix(DV.CashRegister.CashRegisterWithModules __instance)
        {
            try
            {
                ImmersiveServiceSystem.CheckConnectionsBeforeBuy(__instance);
            }
            catch (Exception ex)
            {
                Main.LogError("IMMERSIVE SERVICE -> connection check failed: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(PitStopStation), "OnEnable")]
    public static class ImmersiveService_PitStopStationEnablePatch
    {
        static void Postfix(PitStopStation __instance)
        {
            ImmersiveServiceSystem.RegisterStation(__instance);
        }
    }

    [HarmonyPatch(typeof(LampControl), nameof(LampControl.SetLampState))]
    public static class ImmersiveService_LampPatch
    {
        static bool Prefix(LampControl __instance)
        {
            return ImmersiveServiceSystem.AllowLampChange(__instance);
        }
    }

    [HarmonyPatch(typeof(SaveGameManager), "UpdateInternalData")]
    public static class ImmersiveService_SavePatch
    {
        static void Postfix(SaveGameManager __instance)
        {
            ImmersiveServiceSystem.WriteSave(__instance.data);
        }
    }
}
