// File: TrainServiceReworkPatch.cs

using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using DV;
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

            Debug.Log($"[TrainServiceRework] Repair via PitStop: {changeAmount}%");

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
    // SETUP
    // =========================================================
    [HarmonyPatch(typeof(TrainCar), "InitializeLogicCarRelatedScript")]
    public static class CarRepairSetup
    {
        static void Postfix(TrainCar __instance)
        {
            if (__instance == null || __instance.logicCar == null)
                return;

            if (__instance.CarDamage != null &&
                __instance.GetComponent<CarPitStopParametersBase>() == null &&
                CarTypes.IsRegularCar(__instance.carLivery))
            {
                var fake = __instance.gameObject.AddComponent<FakeCarPitStopParameters>();
                fake.Init(__instance);

                Debug.Log($"[TrainServiceRework] Inject (CAR ONLY) -> {__instance.logicCar.ID}");
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
            var car = TrainCar.Resolve(other.gameObject);

            if (car == null)
                return;

            if (!CarTypes.IsRegularCar(car.carLivery))
                return;

            if (!CarRepairHelper.IsMainCarBody(other))
                return;

            Vector3 worldPitPos = __instance.transform.position - WorldMover.currentMove;

            // =====================================================
            // SELF CHECK
            // =====================================================
            bool blockSelf = false;

            if (!car.IsLoco && car.carType != TrainCarType.Tender)
            {
                if (car.logicCar != null)
                {
                    bool hasCargo = CargoHelper.ShouldBlockBecauseOfCargo(car); // CHANGE

                    if (hasCargo)
                    {
                        blockSelf = true;
                        Debug.Log($"[TrainServiceRework] BLOCKED (HAS CARGO) -> {car.ID}");
                    }
                }

                var selfCategory = CarCategoryHelper.GetCarCategory(car);

                if (!CarRepairHelper.IsCategoryAllowed(worldPitPos, selfCategory))
                {
                    blockSelf = true;
                    Debug.Log($"[TrainServiceRework] BLOCKED (WRONG CATEGORY) -> {car.ID} | {selfCategory}");
                }
            }

            car.preventService = blockSelf;

            if (car.preventService)
                return;

            // =====================================================
            // TRAINSET CHECK
            // =====================================================
            if (car.trainset != null)
            {
                foreach (var c in car.trainset.cars)
                {
                    if (c == null)
                        continue;

                    // Loks + Tender IMMER erlaubt
                    if (c.IsLoco || c.carType == TrainCarType.Tender)
                    {
                        c.preventService = false;
                        continue;
                    }

                    bool block = false;

                    if (c.logicCar != null)
                    {
                        bool hasCargo = CargoHelper.ShouldBlockBecauseOfCargo(c); // CHANGE

                        if (hasCargo)
                        {
                            block = true;
                            Debug.Log($"[TrainServiceRework] BLOCKED (HAS CARGO) -> {c.ID}");
                        }
                    }

                    var cat = CarCategoryHelper.GetCarCategory(c);

                    if (!CarRepairHelper.IsCategoryAllowed(worldPitPos, cat))
                    {
                        block = true;
                        Debug.Log($"[TrainServiceRework] BLOCKED (WRONG CATEGORY) -> {c.ID} | {cat}");
                    }

                    c.preventService = block;

                    Debug.Log($"[TrainServiceRework] SET preventService -> {c.ID} = {block}");
                }
            }

            var comp = car.GetComponent<CarPitStopParametersBase>();
            if (comp == null)
                return;

            if (__instance.IsCarInPitStop())
                return;

            Debug.Log($"[TrainServiceRework] FORCE WAGON ENTRY -> {car.ID}");

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
            var car = TrainCar.Resolve(other.gameObject);

            if (car == null)
                return;

            if (!CarTypes.IsRegularCar(car.carLivery))
                return;

            if (!CarRepairHelper.IsMainCarBody(other))
                return;

            car.preventService = false;

            var comp = car.GetComponent<CarPitStopParametersBase>();
            if (comp == null)
                return;

            CarPitStopParametersBase? current = null;

            try
            {
                current = __instance.GetCarParameters();
            }
            catch
            {
                return;
            }

            if (current == null)
                return;

            if (current == comp)
            {
                Debug.Log($"[TrainServiceRework] FORCE WAGON EXIT -> {car.ID}");
                __instance.SendMessage("CarExit");
            }
        }
    }
	
	// =========================================================
	// FINAL FIX: SELECTOR FILTER (DO NOT REMOVE ANYTHING ELSE)
	// =========================================================
	[HarmonyPatch]
	public static class PitStop_CarEnter_Filter
	{
		static System.Reflection.MethodBase TargetMethod()
		{
			// PRIVATE METHOD → deshalb via Reflection
			return AccessTools.Method(typeof(PitStop), "CarEnter");
		}

		static void Postfix(PitStop __instance)
		{
			if (!__instance.IsCarInPitStop())
				return;

			var carListField = AccessTools.Field(typeof(PitStop), "carList");
			var paramsListField = AccessTools.Field(typeof(PitStop), "paramsList");
			var indexField = AccessTools.Field(typeof(PitStop), "currentCarIndex");

			var carList = carListField.GetValue(__instance) as List<TrainCar>;
			var paramsList = paramsListField.GetValue(__instance) as List<CarPitStopParametersBase>;

			if (carList == null || paramsList == null)
				return;

			Vector3 worldPitPos = __instance.transform.position - WorldMover.currentMove;

			for (int i = carList.Count - 1; i >= 0; i--)
			{
				var c = carList[i];
				if (c == null)
					continue;

				// 🔥 LOK + TENDER IMMER DRIN LASSEN
				if (c.IsLoco || c.carType == TrainCarType.Tender)
					continue;

				bool remove = false;

				// ============================
				// CARGO CHECK
				// ============================
				if (c.logicCar != null)
				{
					bool hasCargo = CargoHelper.ShouldBlockBecauseOfCargo(c); // CHANGE

					if (hasCargo)
					{
						remove = true;
						Debug.Log($"[TrainServiceRework] REMOVE (HAS CARGO) -> {c.ID}");
					}
				}

				// ============================
				// CATEGORY CHECK
				// ============================
				var cat = CarCategoryHelper.GetCarCategory(c);

				if (!CarRepairHelper.IsCategoryAllowed(worldPitPos, cat))
				{
					remove = true;
					Debug.Log($"[TrainServiceRework] REMOVE (WRONG CATEGORY) -> {c.ID} | {cat}");
				}

				// ============================
				// 🔥 ENTSCHEIDENDER FIX
				// ============================
				if (remove)
				{
					carList.RemoveAt(i);
					paramsList.RemoveAt(i);
				}
			}

			// ============================
			// INDEX FIX (CRITICAL)
			// ============================
			int index = (int)indexField.GetValue(__instance);

			if (carList.Count == 0)
			{
				indexField.SetValue(__instance, -1);
				return;
			}

			if (index >= carList.Count)
			{
				indexField.SetValue(__instance, carList.Count - 1);
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

            Debug.Log($"[ExplosionFix] FULL RESET -> {car.ID}");

            // =========================
            // 1. TRAINCAR MODEL RESET
            // =========================
            var handler = car.GetComponent<ExplosionModelHandler>();

            if (handler != null)
            {
                // Fix: internal state erzwingen
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

                // 👉 CargoReactionBase & derived classes
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

            Debug.Log($"[ExplosionFix] FULL RESET DONE -> {car.ID}");
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

            // Nur wenn explodiert
            if (!car.isExploded)
                return;

            float percent = __instance.EffectiveHealthPercentage100Notation;

            Debug.Log($"[ExplosionFix] {car.ID} health = {percent}");

            // Vollständig repariert
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

			// 🔥 WICHTIG: EXPLOSION OVERRIDE
			if (car.isExploded)
			{
				Debug.Log($"[CargoCheck] IGNORE (EXPLODED) -> {car.ID}");
				return false;
			}

			// Normale Cargo Logik
			bool hasCargo =
				car.logicCar.CurrentCargoTypeInCar != CargoType.None &&
				car.logicCar.LoadedCargoAmount > 0f;

			if (hasCargo)
			{
				Debug.Log($"[CargoCheck] HAS CARGO -> {car.ID} | Amount: {car.logicCar.LoadedCargoAmount}");
			}

			return hasCargo;
		}
	}

    // =========================================================
    // MARKER
    // =========================================================
    public class CarRepairTriggerMarker : MonoBehaviour { }
}