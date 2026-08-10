// File: TrainServiceReworkPatch.cs

using HarmonyLib;
using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using DV;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Damage;

namespace TrainServiceRework
{
    // =========================================================
    // CAR CATEGORY ENUM
    // =========================================================
    public enum CarCategory
    {
        Passenger,
        Freight,
        Tank,
        Military
    }

    // =========================================================
    // CATEGORY HELPER (ID BASED)
    // =========================================================
    public static class CarCategoryHelper
    {
        public static CarCategory GetCarCategory(TrainCar car)
        {
            if (car == null || string.IsNullOrEmpty(car.ID) || car.ID.Length < 3)
                return CarCategory.Freight;

            string prefix = car.ID.Substring(1, 2).ToUpper();

            if (prefix == "OL" || prefix == "GS" || prefix == "CH" || prefix == "FD")
                return CarCategory.Tank;

            if (prefix == "PS")
                return CarCategory.Passenger;

            if (prefix == "XB" || prefix == "XF" || prefix == "XN" || prefix == "MB")
                return CarCategory.Military;

            return CarCategory.Freight;
        }
    }

    // =========================================================
    // FAKE PITSTOP PARAMS
    // =========================================================
    public class FakeCarPitStopParameters : CarPitStopParametersBase
    {
        private TrainCar? car;

        public void Init(TrainCar c)
        {
            car = c;
            InitPitStopParameters();
        }

        protected override void InitPitStopParameters()
        {
            if (car == null || car.CarDamage == null)
                return;

            carPitStopParameters = new Dictionary<ResourceType, LocoParameterData>();

            float percent = car.CarDamage.EffectiveHealthPercentage100Notation;

            carPitStopParameters[ResourceType.Car_DMG] =
                new LocoParameterData(percent, 100f);
        }

        public override void UpdateCarPitStopParameter(ResourceType parameter, float changeAmount)
        {
            if (parameter != ResourceType.Car_DMG)
                return;

            if (car == null || car.CarDamage == null)
                return;

            Main.Log($"Repair via PitStop: {changeAmount}%");

            car.CarDamage.RepairCarEffectivePercentage(changeAmount / 100f);
        }

        protected override void RefreshParameters()
        {
            if (car == null || car.CarDamage == null)
                return;

            if (carPitStopParameters == null)
                return;

            carPitStopParameters[ResourceType.Car_DMG].value =
                car.CarDamage.EffectiveHealthPercentage100Notation;
        }
    }
	
	// =========================================================
	// CAR BODY REPAIR PRICE FALLBACK
	// =========================================================
	[HarmonyPatch(typeof(LocoResourceModule),nameof(LocoResourceModule.UpdateResourcePricePerUnit))]
	public static class CarBodyRepairPriceFallbackPatch
	{
		private const float DefaultBodyPricePerUnit = 150f;
		private const float CraneBodyPricePerUnit = 850f;

		private static readonly HashSet<string> loggedPriceChanges = new HashSet<string>();

		static void Prefix(LocoResourceModule __instance,TrainCar trainCar,ref float newPricePerUnit)
		{
			if (__instance == null)
				return;

			if (__instance.resourceType != ResourceType.Car_DMG)
				return;

			if (trainCar == null || trainCar.carLivery == null)
				return;

			string liveryId =
				trainCar.carLivery.id ?? string.Empty;

			float originalPricePerUnit =
				newPricePerUnit;

			// =====================================================
			// BREAKDOWN CRANE
			// =====================================================
			if (string.Equals(liveryId,"Crane",StringComparison.OrdinalIgnoreCase))
			{
				newPricePerUnit = CraneBodyPricePerUnit;
				LogPriceChangeOnce(trainCar,originalPricePerUnit,newPricePerUnit,"CRANE OVERRIDE");

				return;
			}

			// =====================================================
			// DEFAULT FALLBACK
			// =====================================================
			bool invalidPrice = float.IsNaN(newPricePerUnit) || float.IsInfinity(newPricePerUnit) || newPricePerUnit <= 0f;

			if (!invalidPrice)
				return;

			newPricePerUnit = DefaultBodyPricePerUnit;

			LogPriceChangeOnce(trainCar,originalPricePerUnit,newPricePerUnit,"DEFAULT FALLBACK");
		}

		private static void LogPriceChangeOnce(TrainCar trainCar,float oldPricePerUnit,float newPricePerUnit,string reason)
		{
			string key = $"{trainCar.CarGUID}|" + $"{reason}";

			if (!loggedPriceChanges.Add(key))
				return;

			Main.Log(
				$"BODY PRICE {reason}: " +
				$"Car={trainCar.ID} | " +
				$"Livery={trainCar.carLivery?.id ?? "null"} | " +
				$"UnitPrice={oldPricePerUnit} -> {newPricePerUnit} | " +
				$"FullBodyPrice={newPricePerUnit * 100f}");
		}
	}

    // =========================================================
    // SETUP
    // =========================================================
    [HarmonyPatch(typeof(TrainCar), "InitializeLogicCarRelatedScript")]
    public static class CarRepairSetup
    {
        static void Postfix(TrainCar __instance)
		{
			if (__instance == null || __instance.logicCar == null)
				return;

			if (__instance.CarDamage != null && __instance.GetComponent<CarPitStopParametersBase>() == null &&  CarTypes.IsRegularCar(__instance.carLivery))
			{
				var fake = __instance.gameObject.AddComponent<FakeCarPitStopParameters>();
				fake.Init(__instance);

				Main.Log($"Inject (CAR ONLY) -> {__instance.logicCar.ID}");
			}

            var existing = __instance.GetComponentInChildren<CarRepairTriggerMarker>();
            if (existing != null)
                return;

            var root = __instance.transform.Find("[colliders]");
            if (root == null)
                return;

            var collision = root.Find("[collision]");
            if (collision == null)
                return;

            var go = new GameObject("CarRepairTrigger");
            go.transform.SetParent(collision);
            go.transform.localPosition = new Vector3(0f, 1.5f, 0f);

            var col = go.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = new Vector3(3f, 3f, 10f);

            go.tag = "MainTriggerCollider";
            go.AddComponent<CarRepairTriggerMarker>();
        }
    }

    // =========================================================
	// ENTER
	// =========================================================
	[HarmonyPatch(typeof(PitStop), "OnTriggerEnter")]
	public static class PitStopCarSupport
	{
		static void Postfix(PitStop __instance, Collider other)
		{
			if (__instance == null || other == null)
				return;

			TrainCar car = TrainCar.Resolve(other.gameObject);

			if (car == null)
				return;

			bool isLocomotiveOrTender = CarRepairHelper.IsLocomotiveOrTender(car);

			bool isSupportedVehicle = isLocomotiveOrTender || (car.carLivery != null && CarTypes.IsRegularCar(car.carLivery));

			if (!isSupportedVehicle)
			{
				Main.Log(
					$"IGNORE UNSUPPORTED VEHICLE -> " +
					$"{car.ID} | " +
					$"Livery={car.carLivery?.id ?? "null"} | " +
					$"Type={car.carType} | " +
					$"IsLoco={car.IsLoco}");

				return;
			}

			if (!CarRepairHelper.IsMainCarBody(other))
				return;

			Vector3 worldPitPos = __instance.transform.position - WorldMover.currentMove;

			// =====================================================
			// SELF CHECK
			// =====================================================
			bool blockSelf = false;

			if (!isLocomotiveOrTender)
			{
				if (car.logicCar != null)
				{
					bool hasCargo =
						CargoHelper.ShouldBlockBecauseOfCargo(car);

					if (hasCargo)
					{
						blockSelf = true;

						Main.Log(
							$"BLOCKED (HAS CARGO) -> {car.ID}");
					}
				}

				CarCategory selfCategory = CarCategoryHelper.GetCarCategory(car);

				if (!CarRepairHelper.IsCategoryAllowed(worldPitPos,selfCategory))
				{
					blockSelf = true;

					Main.Log(
						$"BLOCKED (WRONG CATEGORY) -> " +
						$"{car.ID} | {selfCategory}");
				}
			}
			else
			{
				blockSelf = false;

				Main.Log(
					$"LOCO/TENDER SERVICE ALLOWED -> " +
					$"{car.ID} | " +
					$"Livery={car.carLivery?.id ?? "null"} | " +
					$"Type={car.carType} | " +
					$"IsLoco={car.IsLoco}");
			}

			car.preventService = blockSelf;

			if (car.preventService)
				return;

			// =====================================================
			// TRAINSET CHECK
			// =====================================================
			if (car.trainset != null)
			{
				foreach (TrainCar c in car.trainset.cars)
				{
					if (c == null)
						continue;

					if (CarRepairHelper.IsLocomotiveOrTender(c))
					{
						c.preventService = false;

						Main.Log(
							$"TRAINSET LOCO/TENDER ALLOWED -> " +
							$"{c.ID} | " +
							$"Livery={c.carLivery?.id ?? "null"}");

						continue;
					}

					bool block = false;

					if (c.logicCar != null)
					{
						bool hasCargo = CargoHelper.ShouldBlockBecauseOfCargo(c);

						if (hasCargo)
						{
							block = true;

							Main.Log(
								$"BLOCKED (HAS CARGO) -> {c.ID}");
						}
					}

					CarCategory category = CarCategoryHelper.GetCarCategory(c);

					if (!CarRepairHelper.IsCategoryAllowed(
							worldPitPos,
							category))
					{
						block = true;

						Main.Log(
							$"BLOCKED (WRONG CATEGORY) -> " +
							$"{c.ID} | {category}");
					}

					c.preventService = block;

					Main.Log(
						$"SET preventService -> " +
						$"{c.ID} = {block}");
				}
			}

			CarPitStopParametersBase comp = car.GetComponent<CarPitStopParametersBase>();

			if (comp == null)
			{
				comp = car.GetComponentInChildren
					<CarPitStopParametersBase>(true);
			}

			if (comp == null)
			{
				Main.LogWarning(
					$"NO PITSTOP PARAMETERS -> " +
					$"Car={car.ID} | " +
					$"Livery={car.carLivery?.id ?? "null"} | " +
					$"Type={car.carType} | " +
					$"IsLoco={car.IsLoco}");

				return;
			}

			if (__instance.IsCarInPitStop())
				return;

			Main.Log(
				$"FORCE VEHICLE ENTRY -> " +
				$"{car.ID} | " +
				$"Params={comp.GetType().FullName}");

			__instance.SendMessage("CarEnter", comp);
		}
	}

    // =========================================================
	// EXIT
	// =========================================================
	[HarmonyPatch(typeof(PitStop), "OnTriggerExit")]
	public static class PitStopCarExitSupport
	{
		static void Postfix(PitStop __instance, Collider other)
		{
			if (__instance == null || other == null)
				return;

			TrainCar car = TrainCar.Resolve(other.gameObject);

			if (car == null)
				return;

			bool isSupportedVehicle = CarRepairHelper.IsLocomotiveOrTender(car) || (car.carLivery != null && CarTypes.IsRegularCar(car.carLivery));

			if (!isSupportedVehicle)
				return;

			if (!CarRepairHelper.IsMainCarBody(other))
				return;

			car.preventService = false;

			CarPitStopParametersBase comp = car.GetComponent<CarPitStopParametersBase>();

			if (comp == null)
			{
				comp = car.GetComponentInChildren
					<CarPitStopParametersBase>(true);
			}

			if (comp == null)
				return;

			CarPitStopParametersBase? current = null;

			try
			{
				current = __instance.GetCarParameters();
			}
			catch (Exception ex)
			{
				Main.LogWarning(
					$"PITSTOP EXIT READ FAILED -> " +
					$"{car.ID} | {ex.Message}");

				return;
			}

			if (current == null)
				return;

			if (current == comp)
			{
				Main.Log(
					$"FORCE VEHICLE EXIT -> {car.ID}");

				__instance.SendMessage("CarExit");
			}
		}
	}
	
	// =========================================================
	// FINAL FIX: SELECTOR FILTER
	// =========================================================
	[HarmonyPatch]
	public static class PitStop_CarEnter_Filter
	{
		static MethodBase TargetMethod()
		{
			return AccessTools.Method(typeof(PitStop),"CarEnter");
		}

		static void Postfix(PitStop __instance)
		{
			if (__instance == null)
				return;

			if (!__instance.IsCarInPitStop())
				return;

			FieldInfo carListField = AccessTools.Field(typeof(PitStop),"carList");
			FieldInfo paramsListField = AccessTools.Field(typeof(PitStop),"paramsList");
			FieldInfo indexField = AccessTools.Field(typeof(PitStop),"currentCarIndex");

			if (carListField == null || paramsListField == null || indexField == null)
			{
				Main.LogWarning(
					"PitStop selector fields could not be found.");

				return;
			}

			List<TrainCar>? carList = carListField.GetValue(__instance) as List<TrainCar>;

			List<CarPitStopParametersBase>? paramsList = paramsListField.GetValue(__instance) as List<CarPitStopParametersBase>;

			if (carList == null || paramsList == null)
				return;

			Vector3 worldPitPos = __instance.transform.position - WorldMover.currentMove;

			int safeCount = Mathf.Min(carList.Count, paramsList.Count);
			
			while (carList.Count > safeCount)
			{
				carList.RemoveAt(carList.Count - 1);
			}

			while (paramsList.Count > safeCount)
			{
				paramsList.RemoveAt(paramsList.Count - 1);
			}

			for (int i = carList.Count - 1; i >= 0; i--)
			{
				TrainCar c = carList[i];

				if (c == null)
				{
					carList.RemoveAt(i);
					paramsList.RemoveAt(i);
					continue;
				}
				
				if (CarRepairHelper.IsLocomotiveOrTender(c))
				{
					c.preventService = false;

					Main.Log(
						$"KEEP LOCO/TENDER IN SELECTOR -> " +
						$"{c.ID} | " +
						$"Livery={c.carLivery?.id ?? "null"}");

					continue;
				}

				bool remove = false;

				// ============================
				// CARGO CHECK
				// ============================
				if (c.logicCar != null)
				{
					bool hasCargo = CargoHelper.ShouldBlockBecauseOfCargo(c);

					if (hasCargo)
					{
						remove = true;

						Main.Log(
							$"REMOVE (HAS CARGO) -> {c.ID}");
					}
				}

				// ============================
				// CATEGORY CHECK
				// ============================
				CarCategory category = CarCategoryHelper.GetCarCategory(c);

				if (!CarRepairHelper.IsCategoryAllowed(worldPitPos,category))
				{
					remove = true;

					Main.Log(
						$"REMOVE (WRONG CATEGORY) -> " +
						$"{c.ID} | {category}");
				}

				if (remove)
				{
					carList.RemoveAt(i);
					paramsList.RemoveAt(i);
				}
			}

			// ============================
			// INDEX FIX
			// ============================
			int index = (int)indexField.GetValue(__instance);

			if (carList.Count == 0)
			{
				indexField.SetValue(__instance, -1);
				return;
			}

			if (index < 0)
			{
				indexField.SetValue(__instance, 0);
				return;
			}

			if (index >= carList.Count)
			{
				indexField.SetValue(__instance,carList.Count - 1);
			}
		}
	}
	
    // =========================================================
    // EXPLOSION FULL RESET (TrainCar + Cargo)
    // =========================================================
    public static class ExplosionFullReset
    {
        public static void ResetCar(TrainCar car)
        {
            if (car == null)
                return;

            Main.Log($"[ExplosionFix] FULL RESET -> {car.ID}");

            // =========================
            // 1. TRAINCAR MODEL RESET
            // =========================
            var handler = car.GetComponent<ExplosionModelHandler>();

            if (handler != null)
            {
                var field = AccessTools.Field(typeof(ExplosionModelHandler), "usingExplodedModel");
                field?.SetValue(handler, true);

                handler.RevertToUnexplodedModel();
            }

            car.isExploded = false;
            car.RefreshLoadedPrefabsExplodedState();

            if (car.PaintExterior != null)
                car.PaintExterior.enabled = true;

            if (car.PaintInterior != null)
                car.PaintInterior.enabled = true;

            // =========================
            // 2. CARGO SYSTEM RESET
            // =========================
            var components = car.GetComponentsInChildren<MonoBehaviour>(true);

            foreach (var comp in components)
            {
                if (comp == null)
                    continue;

                var type = comp.GetType();

                if (type.Name.Contains("CargoReaction"))
                {
                    AccessTools.Field(type, "isExploded")?.SetValue(comp, false);
                    AccessTools.Field(type, "aboutToExplode")?.SetValue(comp, false);
                    AccessTools.Field(type, "initialized")?.SetValue(comp, false);
                }
            }

            // =========================
            // 3. CARGO DAMAGE RESET
            // =========================
            if (car.CargoDamage != null)
            {
                car.CargoDamage.currentDamageState = DamageState.WithinSafeLimits;
            }

            Main.Log($"[ExplosionFix] FULL RESET DONE -> {car.ID}");
        }
    }


    // =========================================================
    // HOOK: NACH REPARATUR
    // =========================================================
    [HarmonyPatch(typeof(CarDamageModel), "RepairCarEffectivePercentage")]
    public static class ExplosionRevert_OnRepair
    {
        static void Postfix(CarDamageModel __instance)
        {
            if (__instance == null)
                return;

            var car = __instance.GetComponent<TrainCar>();
            if (car == null)
                return;

            if (!car.isExploded)
                return;

            float percent = __instance.EffectiveHealthPercentage100Notation;

            Main.Log($"[ExplosionFix] {car.ID} health = {percent}");

            if (percent >= 99.9f)
            {
                ExplosionFullReset.ResetCar(car);
            }
        }
    }

    // =========================================================
    // HELPER
    // =========================================================
    public static class CarRepairHelper
    {
        private static readonly Dictionary<Vector3, HashSet<CarCategory>> pitStopRules =
            new Dictionary<Vector3, HashSet<CarCategory>>()
        {
            { new Vector3(11545.4f, 122.2f, 11621.0f), new HashSet<CarCategory>{ CarCategory.Tank } },
            { new Vector3(9330.8f, 119.3f, 13358.7f), new HashSet<CarCategory>{ CarCategory.Freight } },
            { new Vector3(1847.3f, 122.2f, 5615.8f), new HashSet<CarCategory>{ CarCategory.Passenger } },
            { new Vector3(8038.7f, 131.8f, 7136.4f), new HashSet<CarCategory>{ CarCategory.Freight, CarCategory.Military } },
            { new Vector3(12890.5f, 140.2f, 11007.6f), new HashSet<CarCategory>{ CarCategory.Freight } }
        };
		
		public static bool IsLocomotiveOrTender(TrainCar car)
		{
			if (car == null)
				return false;

			if (car.IsLoco)
				return true;

			if (car.carType == TrainCarType.Tender)
				return true;

			if (car.carLivery != null && CarTypes.IsAnyLocomotiveOrTender(car.carLivery))
			{
				return true;
			}

			return false;
		}

        public static bool IsMainCarBody(Collider col)
        {
            Transform t = col.transform;

            while (t != null)
            {
                if (t.name == "[collision]")
                    return true;

                t = t.parent;
            }

            return false;
        }

        public static bool IsCategoryAllowed(Vector3 worldPitPos, CarCategory category)
        {
            float tolerance = 15f;
            float maxDistSqr = tolerance * tolerance;

            foreach (var kvp in pitStopRules)
            {
                if ((worldPitPos - kvp.Key).sqrMagnitude <= maxDistSqr)
                {
                    return kvp.Value.Contains(category);
                }
            }

            return false;
        }
		
		// =========================================================
		// REPAIR STATION ROUTING
		// =========================================================
		
		public static bool IsRepairStationForCategories(StationController station,IReadOnlyCollection<CarCategory> categories)
		{
			if (station == null || categories == null || categories.Count == 0)
				return false;

			foreach (var rule in pitStopRules)
			{
				if (!RuleSupportsAllCategories(rule.Value,categories))
				{
					continue;
				}

				StationController? repairStation = GetNearestStationToRepairPoint(rule.Key);

				if (repairStation == null)
					continue;

				if (IsSameStation(station,repairStation))
				{
					return true;
				}
			}

			return false;
		}
		
		public static StationController? FindRandomRepairStation(StationController startingStation,IReadOnlyCollection<CarCategory> categories,System.Random random)
		{
			if (startingStation == null || categories == null || categories.Count == 0 || random == null)
			{
				return null;
			}

			List<StationController> candidates = new List<StationController>();

			foreach (var rule in pitStopRules)
			{
				if (!RuleSupportsAllCategories(rule.Value,categories))
				{
					continue;
				}

				StationController? candidate =GetNearestStationToRepairPoint(rule.Key);

				if (candidate == null)
					continue;

				if (IsSameStation(startingStation, candidate))
					continue;

				bool alreadyAdded = false;

				for (int i = 0; i < candidates.Count; i++)
				{
					if (IsSameStation(candidates[i], candidate))
					{
						alreadyAdded = true;
						break;
					}
				}

				if (!alreadyAdded)
				{
					candidates.Add(candidate);
				}
			}

			if (candidates.Count == 0)
				return null;

			return candidates[random.Next(candidates.Count)];
		}
		
		private static bool RuleSupportsAllCategories(HashSet<CarCategory> allowedCategories,IReadOnlyCollection<CarCategory> requiredCategories)
		{
			foreach (CarCategory category in requiredCategories)
			{
				if (!allowedCategories.Contains(category))
					return false;
			}

			return true;
		}
		
		private static StationController? GetNearestStationToRepairPoint(Vector3 repairWorldPosition)
		{
			StationController? closestStation = null;
			float closestDistanceSqr = float.MaxValue;

			foreach (StationController station in StationController.allStations)
			{
				if (station == null || station.gameObject == null)
					continue;

				Vector3 stationWorldPosition = GetAbsoluteStationPosition(station);

				float distanceSqr = (stationWorldPosition - repairWorldPosition).sqrMagnitude;

				if (distanceSqr >= closestDistanceSqr)
					continue;

				closestDistanceSqr = distanceSqr;
				closestStation = station;
			}

			return closestStation;
		}

		private static Vector3 GetAbsoluteStationPosition(StationController station)
		{
			return station.transform.position - WorldMover.currentMove;
		}

		private static bool IsSameStation(StationController first,StationController second)
		{
			if (first == null || second == null)
				return false;

			if (first.stationInfo == null || second.stationInfo == null)
			{
				return first == second;
			}

			return string.Equals(first.stationInfo.YardID,second.stationInfo.YardID,StringComparison.Ordinal);
		}
    }
	
	// =========================================================
	// EXPLOSION + CARGO HELPER
	// =========================================================
	public static class CargoHelper
	{
		public static bool ShouldBlockBecauseOfCargo(TrainCar car)
		{
			if (car == null || car.logicCar == null)
				return false;

			if (car.isExploded)
			{
				Main.Log($"[CargoCheck] IGNORE (EXPLODED) -> {car.ID}");
				return false;
			}

			bool hasCargo = car.logicCar.CurrentCargoTypeInCar != CargoType.None && car.logicCar.LoadedCargoAmount > 0f;

			if (hasCargo)
			{
				Main.Log($"[CargoCheck] HAS CARGO -> {car.ID} | Amount: {car.logicCar.LoadedCargoAmount}");
			}

			return hasCargo;
		}
	}

    // =========================================================
    // OPTIONAL PERSISTENT JOBS DAMAGE COMPATIBILITY
    // =========================================================
    public static class PersistentJobsDamageCompatibility
    {
        private const float TransportDamageSplitCondition = 50f;
        private const float ShuntingLoadDamageCondition = 50f;
        private const float EmptyHaulRepairCondition = 50f;

        private const string CompatibilityHarmonyId = "TrainServiceRework.PersistentJobsDamageCompatibility";
        private static readonly Harmony compatibilityHarmony = new Harmony(CompatibilityHarmonyId);

        private static bool patchesApplied = false;
        private static bool patchingFailed = false;

        private static MethodInfo? persistentEmptyHaulMethod;
        private static MethodInfo? persistentFinalizeMethod;
        private static MethodInfo? persistentFindNearestTrackMethod;

        public static void TryPatch()
        {
            if (patchesApplied || patchingFailed)
                return;

            Type? emptyHaulGeneratorType = AccessTools.TypeByName("PersistentJobsMod.JobGenerators.EmptyHaulJobGenerator");
            Type? unusedTrainCarDeleterPatchesType = AccessTools.TypeByName("PersistentJobsMod.HarmonyPatches.JobGeneration.UnusedTrainCarDeleter_Patches");
            Type? carTrackAssignmentType = AccessTools.TypeByName("PersistentJobsMod.Utilities.CarTrackAssignment");

            if (emptyHaulGeneratorType == null || unusedTrainCarDeleterPatchesType == null || carTrackAssignmentType == null)
            {
                return;
            }

            try
            {
                MethodInfo? emptyHaulMethod = FindStaticMethod(emptyHaulGeneratorType,"GenerateEmptyHaulJobWithExistingCarsOrNull",6);
                MethodInfo? divideEmptyGroupsMethod = FindStaticMethod(unusedTrainCarDeleterPatchesType,"DivideEmptyConsecutiveTrainCarGroupsIntoLoadableAndNotLoadable",2);
                MethodInfo? divideLoadedGroupsMethod = FindStaticMethod(unusedTrainCarDeleterPatchesType,"DivideLoadedConsecutiveTrainCarGroupsIntoUnloadableAndNotUnloadable",2);
                MethodInfo? shuntingLoadExtendMethod = FindStaticMethod(unusedTrainCarDeleterPatchesType,"TryExtendTrainCarsWithNextTrainCarGroupForShuntingLoad",6);
                MethodInfo? shuntingLoadReassignMethod = FindStaticMethod(unusedTrainCarDeleterPatchesType,"TryCreateAndFinalizeShuntingLoadJobChainController",5);
                MethodInfo? finalizeMethod = FindStaticMethod(unusedTrainCarDeleterPatchesType,"FinalizeJobChainControllerAndGenerateFirstJob",1);
                MethodInfo? findNearestTrackMethod = FindStaticMethod(carTrackAssignmentType,"FindNearestNamedTrackOrNull",1);

                if (emptyHaulMethod == null || 
					divideEmptyGroupsMethod == null || 
					divideLoadedGroupsMethod == null || 
					shuntingLoadExtendMethod == null || 
					shuntingLoadReassignMethod == null || 
					finalizeMethod == null || 
					findNearestTrackMethod == null)
                {
                    patchingFailed = true;

                    Main.LogWarning(
                        "Persistent Jobs detected, but one or more " +
                        "required job generation methods could not be found. " +
                        "Damage-based job routing was not installed.");

                    return;
                }

                persistentEmptyHaulMethod = emptyHaulMethod;
                persistentFinalizeMethod = finalizeMethod;
                persistentFindNearestTrackMethod = findNearestTrackMethod;

                // =================================================
                // GROUP SPLITTING
                // =================================================
				
                compatibilityHarmony.Patch(divideEmptyGroupsMethod,postfix: new HarmonyMethod(typeof(PersistentJobsDamageCompatibility),nameof(DivideEmptyGroupsPostfix)));
                compatibilityHarmony.Patch(divideLoadedGroupsMethod,postfix: new HarmonyMethod(typeof(PersistentJobsDamageCompatibility),nameof(DivideLoadedGroupsPostfix)));
                compatibilityHarmony.Patch(shuntingLoadExtendMethod,prefix: new HarmonyMethod(typeof(PersistentJobsDamageCompatibility),nameof(ShuntingLoadExtendPrefix)));

                // =================================================
                // EMPTY HAUL / LOGISTIC
                // =================================================

                compatibilityHarmony.Patch(emptyHaulMethod,prefix: new HarmonyMethod(typeof(PersistentJobsDamageCompatibility),nameof(EmptyHaulPrefix)));

                // =================================================
                // SHUNTING LOAD
                // =================================================

                compatibilityHarmony.Patch(shuntingLoadReassignMethod,prefix: new HarmonyMethod(typeof(PersistentJobsDamageCompatibility),nameof(ShuntingLoadReassignPrefix)));

                patchesApplied = true;

                Main.Log(
                    "PERSISTENT JOBS COMPAT -> ACTIVE | " +
                    "Transport <50% = SEPARATE FREIGHT GROUP, NOT BLOCKED | " +
                    "EmptyHaul <=50% = RANDOM VALID REPAIR DESTINATION ONLY | " +
                    "ShuntingLoad <50% = CONVERT TO REPAIR EMPTY HAUL | " +
                    "ShuntingUnload = UNPATCHED");
            }
            catch (Exception ex)
            {
                patchingFailed = true;

                Main.LogError(
                    "Persistent Jobs damage compatibility patch failed: " +
                    ex);
            }
        }

        // =====================================================
        // FIND METHOD SAFELY
        // =====================================================
        private static MethodInfo? FindStaticMethod(Type type,string methodName,int parameterCount)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo? foundMethod = null;

            foreach (MethodInfo method in methods)
            {
                if (method.Name != methodName)
                    continue;

                if (method.GetParameters().Length != parameterCount)
                    continue;

                if (foundMethod != null)
                {
                    Main.LogWarning(
                        $"Persistent Jobs method ambiguous -> " +
                        $"{type.FullName}.{methodName}");

                    return null;
                }

                foundMethod = method;
            }

            return foundMethod;
        }

        // =====================================================
        // EMPTY GROUP SPLIT
        // =====================================================
        private static void DivideEmptyGroupsPostfix(object __result)
        {
            if (__result == null)
                return;

            try
            {
                SplitLoadableShuntingGroupsByCondition(__result,"Item1",ShuntingLoadDamageCondition);
                SplitSimpleCarTupleGroupsByCondition(__result,"Item2",EmptyHaulRepairCondition,true,"EMPTY HAUL");
            }
            catch (Exception ex)
            {
                Main.LogError(
                    "Persistent Jobs empty-group damage split failed: " +
                    ex);
            }
        }

        // =====================================================
        // LOADED GROUP SPLIT
        // =====================================================
        private static void DivideLoadedGroupsPostfix(object __result)
        {
            if (__result == null)
                return;

            try
            {
                SplitSimpleCarTupleGroupsByCondition(__result,"Item2",TransportDamageSplitCondition,false,"TRANSPORT");
            }
            catch (Exception ex)
            {
                Main.LogError(
                    "Persistent Jobs transport-group damage split failed: " +
                    ex);
            }
        }

        // =====================================================
        // SIMPLE GROUP SPLITTER
        // =====================================================
		
        private static void SplitSimpleCarTupleGroupsByCondition(object result,string resultFieldName,float threshold,bool inclusive,string logLabel)
        {
            FieldInfo? resultField = result.GetType().GetField(resultFieldName,BindingFlags.Instance | BindingFlags.Public);

            if (resultField == null)
                return;

            object? outerObject = resultField.GetValue(result);

            System.Collections.IList? outerList = outerObject as System.Collections.IList;

            if (outerList == null || outerList.Count == 0)
                return;

            List<object> originalGroups = new List<object>();

            foreach (object group in outerList)
            {
                if (group != null)
                    originalGroups.Add(group);
            }

            List<object> replacementGroups = new List<object>();

            foreach (object group in originalGroups)
            {
                System.Collections.IList? tupleList = group as System.Collections.IList;

                if (tupleList == null || tupleList.Count == 0)
                    continue;

                object? currentReplacementGroup = null;
                System.Collections.IList? currentReplacementList = null;
                bool? currentRestrictedState = null;

                for (int i = 0; i < tupleList.Count; i++)
                {
                    object tuple = tupleList[i];

                    TrainCar? car = GetTrainCarFromTuple(tuple, "Item1");

                    if (car == null)
                        continue;

                    bool restrictedState = IsConditionMatched(GetConditionPercent(car),threshold,inclusive);

                    if (currentReplacementList == null || !currentRestrictedState.HasValue || currentRestrictedState.Value != restrictedState)
                    {
                        currentReplacementGroup = Activator.CreateInstance(group.GetType());
                        currentReplacementList = currentReplacementGroup as System.Collections.IList;

                        if (currentReplacementList == null)
                        {
                            Main.LogWarning(
                                $"{logLabel} DAMAGE SPLIT -> " +
                                $"Could not create mutable group of type " +
                                $"{group.GetType().FullName}");

                            return;
                        }

                        replacementGroups.Add(currentReplacementGroup!);
                        currentRestrictedState = restrictedState;
                    }

                    currentReplacementList.Add(tuple);
                }
            }

            if (replacementGroups.Count == originalGroups.Count)
                return;

            outerList.Clear();

            foreach (object replacement in replacementGroups)
            {
                outerList.Add(replacement);
            }

            Main.Log(
                $"{logLabel} DAMAGE SPLIT -> " +
                $"Groups {originalGroups.Count} -> {replacementGroups.Count}");
        }

        // =====================================================
        // SHUNTING LOAD TYPE-GROUP SPLITTER
        // =====================================================
		
        private static void SplitLoadableShuntingGroupsByCondition(object result,string resultFieldName,float threshold)
        {
            FieldInfo? resultField = result.GetType().GetField(resultFieldName,BindingFlags.Instance | BindingFlags.Public);

            if (resultField == null)
                return;

            object? outerObject = resultField.GetValue(result);

            System.Collections.IEnumerable? outerEnumerable = outerObject as System.Collections.IEnumerable;

            if (outerEnumerable == null)
                return;

            int splitTupleCount = 0;

            foreach (object group in outerEnumerable)
            {
                System.Collections.IList? tupleList = group as System.Collections.IList;

                if (tupleList == null || tupleList.Count == 0)
                    continue;

                List<object> originalTuples = new List<object>();

                foreach (object tuple in tupleList)
                {
                    if (tuple != null)
                        originalTuples.Add(tuple);
                }

                List<object> replacementTuples = new List<object>();

                foreach (object tuple in originalTuples)
                {
                    FieldInfo? trainCarsField = tuple.GetType().GetField("Item2",BindingFlags.Instance | BindingFlags.Public);

                    if (trainCarsField == null)
                    {
                        replacementTuples.Add(tuple);
                        continue;
                    }

                    IReadOnlyList<TrainCar>? trainCars = trainCarsField.GetValue(tuple) as IReadOnlyList<TrainCar>;

                    if (trainCars == null || trainCars.Count == 0)
                    {
                        replacementTuples.Add(tuple);
                        continue;
                    }

                    List<List<TrainCar>> segments = SplitTrainCarsConsecutively(trainCars,threshold,false);

                    if (segments.Count <= 1)
                    {
                        replacementTuples.Add(tuple);
                        continue;
                    }

                    splitTupleCount++;

                    foreach (List<TrainCar> segment in segments)
                    {
                        object? clonedTuple = CloneTupleReplacingField(tuple,"Item2",segment);

                        if (clonedTuple != null)
                        {
                            replacementTuples.Add(clonedTuple);
                        }
                    }
                }

                if (replacementTuples.Count == originalTuples.Count)
                    continue;

                tupleList.Clear();

                foreach (object tuple in replacementTuples)
                {
                    tupleList.Add(tuple);
                }
            }

            if (splitTupleCount > 0)
            {
                Main.Log(
                    $"SHUNTING LOAD DAMAGE SPLIT -> " +
                    $"Split {splitTupleCount} mixed train-car type group(s)");
            }
        }

        // =====================================================
        // SHUNTING LOAD MULTI-PICKUP GUARD
        // =====================================================
		
        private static bool ShuntingLoadExtendPrefix(object[] __args,ref List<TrainCar>? __result)
        {
            if (__args == null || __args.Length < 3)
                return true;

            IReadOnlyList<TrainCar>? currentTrainCars = __args[0] as IReadOnlyList<TrainCar>;
            IReadOnlyList<TrainCar>? nextTrainCars =__args[2] as IReadOnlyList<TrainCar>;

            if (currentTrainCars == null || nextTrainCars == null || currentTrainCars.Count == 0 || nextTrainCars.Count == 0)
            {
                return true;
            }

            ConditionGroupState currentState = GetConditionGroupState(currentTrainCars,ShuntingLoadDamageCondition,false);
            ConditionGroupState nextState = GetConditionGroupState(nextTrainCars,ShuntingLoadDamageCondition,false);

            if (currentState == ConditionGroupState.Mixed || nextState == ConditionGroupState.Mixed || currentState != nextState)
            {
                __result = null;

                Main.Log(
                    $"SHUNTING LOAD EXTEND BLOCK -> " +
                    $"Current={currentState} | Next={nextState}");

                return false;
            }

            return true;
        }

        // =====================================================
        // LOGISTIC / EMPTY HAUL
        // =====================================================
		
        private static bool EmptyHaulPrefix(StationController startingStation,ref StationController destinationStation,IReadOnlyList<TrainCar> trainCars,System.Random random,ref Track? targetTrack,ref JobChainController? __result)
        {
            if (startingStation == null || destinationStation == null || trainCars == null || trainCars.Count == 0 || random == null)
            {
                return true;
            }

            List<TrainCar> repairCars = new List<TrainCar>();

            HashSet<CarCategory> requiredCategories = new HashSet<CarCategory>();

            for (int i = 0; i < trainCars.Count; i++)
            {
                TrainCar car = trainCars[i];

                if (car == null)
                    continue;

                float condition = GetConditionPercent(car);

                if (condition > EmptyHaulRepairCondition)
                    continue;

                repairCars.Add(car);
                requiredCategories.Add(CarCategoryHelper.GetCarCategory(car));
            }

            if (repairCars.Count == 0)
                return true;

            if (repairCars.Count != trainCars.Count)
            {
                Main.LogWarning(
                    $"EMPTY HAUL MIXED DAMAGE GROUP BLOCKED | " +
                    $"Cars={GetCarIds(trainCars)}");

                __result = null;
                return false;
            }

            if (CarRepairHelper.IsRepairStationForCategories(startingStation,requiredCategories))
            {
                Main.Log(
                    $"JOB BLOCK -> EMPTY HAUL | " +
                    $"Already at valid repair station " +
                    $"{startingStation.logicStation.ID} | " +
                    $"Cars={GetCarIds(repairCars)}");

                __result = null;
                return false;
            }

            StationController? repairDestination = CarRepairHelper.FindRandomRepairStation(startingStation,requiredCategories,random);

            if (repairDestination == null)
            {
                Main.LogWarning(
                    $"EMPTY HAUL REPAIR ROUTE FAILED | " +
                    $"No valid repair station found | " +
                    $"Cars={GetCarIds(repairCars)}");

                __result = null;
                return false;
            }

            string oldDestination = destinationStation.logicStation.ID;
            string newDestination = repairDestination.logicStation.ID;

            destinationStation = repairDestination;

            targetTrack = null;

            Main.Log(
                $"EMPTY HAUL REPAIR REROUTE | " +
                $"Cars={GetCarIds(repairCars)} | " +
                $"From={startingStation.logicStation.ID} | " +
                $"OldDestination={oldDestination} | " +
                $"NewDestination={newDestination}");

            return true;
        }

        // =====================================================
        // SHUNTING LOAD -> REPAIR EMPTY HAUL
        // =====================================================
		
        private static bool ShuntingLoadReassignPrefix(object[] __args,ref JobChainController? __result)
        {
            if (__args == null || __args.Length < 5)
                return true;

            IReadOnlyList<TrainCar> trainCars = FindNestedTrainCars(__args[3]);

            if (trainCars.Count == 0)
            {
                Main.LogWarning(
                    "Persistent Jobs ShuntingLoad compatibility " +
                    "could not extract any TrainCars.");

                return true;
            }

            ConditionGroupState damageState = GetConditionGroupState(trainCars,ShuntingLoadDamageCondition,false);

            if (damageState == ConditionGroupState.Normal)
                return true;

            if (damageState == ConditionGroupState.Mixed)
            {
                Main.LogWarning(
                    $"SHUNTING LOAD MIXED DAMAGE GROUP BLOCKED | " +
                    $"Cars={GetCarIds(trainCars)}");

                __result = null;
                return false;
            }

            TrainCar? blockedCar = FindCarBelowCondition(trainCars,ShuntingLoadDamageCondition);

            if (blockedCar == null)
                return true;

            float condition = GetConditionPercent(blockedCar);

            StationController? sourceStation = __args[0] as StationController;

            if (sourceStation == null)
            {
                Main.LogWarning(
                    "SHUNTING LOAD DAMAGE CHECK FAILED | " +
                    "Source station is null.");

                __result = null;
                return false;
            }

            HashSet<CarCategory> requiredCategories = new HashSet<CarCategory>();

            for (int i = 0; i < trainCars.Count; i++)
            {
                TrainCar car = trainCars[i];

                if (car == null)
                    continue;

                requiredCategories.Add(CarCategoryHelper.GetCarCategory(car));
            }

            if (CarRepairHelper.IsRepairStationForCategories(sourceStation,requiredCategories))
            {
                Main.Log(
                    $"JOB BLOCK -> DAMAGED CAR ALREADY AT REPAIR STATION | " +
                    $"Car={blockedCar.ID} | " +
                    $"Condition={condition:F2}% | " +
                    $"Station={sourceStation.logicStation.ID} | " +
                    $"Cars={GetCarIds(trainCars)}");

                __result = null;
                return false;
            }

            System.Random? random = __args[4] as System.Random;

            if (random == null || persistentEmptyHaulMethod == null || persistentFinalizeMethod == null || persistentFindNearestTrackMethod == null)
            {
                Main.LogWarning(
                    $"REPAIR EMPTY HAUL FAILED | " +
                    $"Required Persistent Jobs method unavailable | " +
                    $"Cars={GetCarIds(trainCars)}");

                __result = null;
                return false;
            }

            Track? startingTrack = null;

            try
            {
                object? foundTrack = persistentFindNearestTrackMethod.Invoke(null,new object[]{trainCars});

                startingTrack = foundTrack as Track;
            }
            catch (Exception ex)
            {
                Main.LogWarning(
                    "REPAIR EMPTY HAUL START TRACK FAILED | " +
                    ex.Message);
            }

            if (startingTrack == null)
            {
                Main.LogWarning(
                    $"REPAIR EMPTY HAUL FAILED | " +
                    $"No starting track found | " +
                    $"Cars={GetCarIds(trainCars)}");

                __result = null;
                return false;
            }

            try
            {
                object? generated = persistentEmptyHaulMethod.Invoke(null,new object?[]{sourceStation,sourceStation,startingTrack,trainCars,random,null});

                JobChainController? repairJob = generated as JobChainController;

                if (repairJob == null)
                {
                    Main.LogWarning(
                        $"REPAIR EMPTY HAUL NOT GENERATED | " +
                        $"Cars={GetCarIds(trainCars)}");

                    __result = null;
                    return false;
                }

                persistentFinalizeMethod.Invoke(null,new object[]{repairJob});

                __result = repairJob;

                Main.Log(
                    $"SHUNTING LOAD -> REPAIR EMPTY HAUL | " +
                    $"Car={blockedCar.ID} | " +
                    $"Condition={condition:F2}% | " +
                    $"From={sourceStation.logicStation.ID} | " +
                    $"Cars={GetCarIds(trainCars)}");

                return false;
            }
            catch (Exception ex)
            {
                Main.LogError(
                    "SHUNTING LOAD -> REPAIR EMPTY HAUL FAILED: " +
                    ex);

                __result = null;
                return false;
            }
        }

        // =====================================================
        // CONDITION GROUP HELPERS
        // =====================================================
		
        private enum ConditionGroupState
        {
            Normal,
            Restricted,
            Mixed
        }

        private static ConditionGroupState GetConditionGroupState(IReadOnlyList<TrainCar> trainCars,float threshold,bool inclusive)
        {
            bool hasNormal = false;
            bool hasRestricted = false;

            for (int i = 0; i < trainCars.Count; i++)
            {
                TrainCar car = trainCars[i];

                if (car == null)
                    continue;

                bool restricted = IsConditionMatched(GetConditionPercent(car),threshold,inclusive);

                if (restricted)
                    hasRestricted = true;
                else
                    hasNormal = true;

                if (hasRestricted && hasNormal)
                    return ConditionGroupState.Mixed;
            }

            return hasRestricted
                ? ConditionGroupState.Restricted
                : ConditionGroupState.Normal;
        }

        private static bool IsConditionMatched(float condition,float threshold,bool inclusive)
        {
            return inclusive
                ? condition <= threshold
                : condition < threshold;
        }

        private static List<List<TrainCar>> SplitTrainCarsConsecutively(IReadOnlyList<TrainCar> trainCars,float threshold,bool inclusive)
        {
            List<List<TrainCar>> result = new List<List<TrainCar>>();

            List<TrainCar>? current = null;
            bool? currentRestricted = null;

            for (int i = 0; i < trainCars.Count; i++)
            {
                TrainCar car = trainCars[i];

                if (car == null)
                    continue;

                bool restricted = IsConditionMatched(GetConditionPercent(car),threshold,inclusive);

                if (current == null || !currentRestricted.HasValue || currentRestricted.Value != restricted)
                {
                    current = new List<TrainCar>();
                    result.Add(current);
                    currentRestricted = restricted;
                }

                current.Add(car);
            }

            return result;
        }

        // =====================================================
        // REFLECTION TUPLE HELPERS
        // =====================================================
		
        private static TrainCar? GetTrainCarFromTuple(object tuple,string fieldName)
        {
            if (tuple == null)
                return null;

            FieldInfo? field = tuple.GetType().GetField(fieldName,BindingFlags.Instance | BindingFlags.Public);

            if (field == null)
                return null;

            return field.GetValue(tuple) as TrainCar;
        }

        private static object? CloneTupleReplacingField(object sourceTuple,string fieldName,object replacementValue)
        {
            if (sourceTuple == null)
                return null;

            Type tupleType = sourceTuple.GetType();

            object? clone = Activator.CreateInstance(tupleType);

            if (clone == null)
                return null;

            FieldInfo[] fields = tupleType.GetFields(BindingFlags.Instance | BindingFlags.Public);

            foreach (FieldInfo field in fields)
            {
                object? value = field.Name == fieldName
                        ? replacementValue
                        : field.GetValue(sourceTuple);

                field.SetValue(clone, value);
            }

            return clone;
        }

        // =====================================================
        // FIND TRAIN CARS IN NESTED SHUNTING STRUCTURE
        // =====================================================
		
        private static IReadOnlyList<TrainCar> FindNestedTrainCars(object? root)
        {
            List<TrainCar> result = new List<TrainCar>();
            CollectNestedTrainCars(root,result,0);

            return result;
        }

        private static void CollectNestedTrainCars(object? value,List<TrainCar> result,int depth)
        {
            if (value == null || depth > 8)
                return;

            if (value is TrainCar trainCar)
            {
                if (!result.Contains(trainCar))
                    result.Add(trainCar);
                return;
            }

            if (value is string)
                return;

            if (value is System.Collections.IEnumerable enumerable)
            {
                foreach (object? item in enumerable)
                {
                    CollectNestedTrainCars(item,result,depth + 1);
                }
                return;
            }

            Type type = value.GetType();

            string? fullName = type.FullName;

            if (fullName == null || !fullName.StartsWith("System.ValueTuple`",StringComparison.Ordinal))
            {
                return;
            }

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);

            foreach (FieldInfo field in fields)
            {
                CollectNestedTrainCars(field.GetValue(value),result,depth + 1);
            }
        }

        // =====================================================
        // HEALTH
        // =====================================================
		
        private static float GetConditionPercent(
            TrainCar car)
        {
            if (car == null)
                return 100f;

            if (car.CarDamage == null)
                return 100f;

            return Mathf.Clamp(car.CarDamage.EffectiveHealthPercentage100Notation,0f,100f);
        }

        // =====================================================
        // CAR ID LOGGING
        // =====================================================
		
        private static string GetCarIds(IReadOnlyList<TrainCar> trainCars)
        {
            if (trainCars == null || trainCars.Count == 0)
                return "-";

            string result = string.Empty;

            for (int i = 0; i < trainCars.Count; i++)
            {
                TrainCar car = trainCars[i];

                if (car == null)
                    continue;

                if (result.Length > 0)
                    result += ", ";

                result += car.ID;
            }

            return result;
        }

        // =====================================================
        // CONDITION < LIMIT
        // =====================================================
		
        private static TrainCar? FindCarBelowCondition(IReadOnlyList<TrainCar> trainCars,float minimumCondition)
        {
            for (int i = 0; i < trainCars.Count; i++)
            {
                TrainCar car = trainCars[i];

                if (car == null)
                    continue;

                if (GetConditionPercent(car) < minimumCondition)
                    return car;
            }

            return null;
        }
    }

    // =========================================================
    // OPTIONAL PERSISTENT JOBS PATCH RETRY
    // =========================================================
	
    [HarmonyPatch(typeof(UnusedTrainCarDeleter),"TrainCarsDeleteCheck")]
    public static class PersistentJobsDamageCompatibilityBootstrap
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix()
        {
            PersistentJobsDamageCompatibility.TryPatch();
        }
    }

    // =========================================================
    // MARKER
    // =========================================================
    public class CarRepairTriggerMarker : MonoBehaviour { }
}