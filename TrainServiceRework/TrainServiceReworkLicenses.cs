// File: TrainServiceReworkLicenses.cs
//
// Two own general licenses for the maintenance jobs (SM), built the same way as the company licenses of
// DVMultiplayerIP (process taken from DVCustomLicenses by WhistleWiz, no extra mod needed):
//
//   TSR_FreightCarMaintenance    "Güterwagen-Instandsetzung"    – maintenance jobs with freight / tank / military cars
//   TSR_PassengerCarMaintenance  "Reisezugwagen-Instandsetzung" – maintenance jobs with passenger cars
//
//  * GeneralLicenseType_v2 created when the mod loads and added to the game's license list (before a savegame is
//    loaded – the game stores bought licenses by their ID),
//  * both need the license "Manual Service" and use its colour,
//  * license paper and sample booklet are copies of the DE2 paper (the picture is rendered with our data),
//  * name / description translated natively (26 languages), entry in the license screen of the career manager.

using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using DV.Booklets;
using DV.Booklets.Rendered;
using DV.Logic.Job;
using DV.RenderTextureSystem;
using DV.RenderTextureSystem.BookletRender;
using DV.ServicePenalty.UI;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using Object = UnityEngine.Object;

namespace TrainServiceRework
{
    /// <summary>License created by this mod.</summary>
    public class TsrLicenseV2 : GeneralLicenseType_v2
    {
        public string ItemKey = "";
        public string InfoItemKey = "";
        public string RenderPrefabName = "";
        public string SampleRenderPrefabName = "";
        public GameObject? RenderPrefab;
        public GameObject? SampleRenderPrefab;
    }

    public static class MaintenanceLicenses
    {
        public const string FreightId = "TSR_FreightCarMaintenance";
        public const string PassengerId = "TSR_PassengerCarMaintenance";

        private const float DefaultPrice = 40000f;

        // enum values far away from the game (and from DVMultiplayerIP: 19001+)
        private const int FreightValue = 19501;
        private const int PassengerValue = 19502;

        public static TsrLicenseV2? Freight { get; private set; }
        public static TsrLicenseV2? Passenger { get; private set; }

        public static readonly List<TsrLicenseV2> Added = new List<TsrLicenseV2>();
        public static readonly HashSet<GeneralLicenseType> AddedValues = new HashSet<GeneralLicenseType>();
        public static readonly Dictionary<string, GameObject> KeyToPrefab = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        private static readonly FieldInfo? itemPrefabNameField = AccessTools.Field(typeof(InventoryItemSpec), "itemPrefabName");
        private static Transform? holder;

        // =====================================================
        // REGISTER (Main.Load)
        // =====================================================
        public static void Register()
        {
            if (Added.Count > 0)
                return;

            try
            {
                DVObjectModel types = DV.Globals.G.Types;

                Freight = Create(types, FreightId, (GeneralLicenseType)FreightValue,
                    TrainServiceReworkTranslations.LicenseFreightKey, "freight_maintenance.rgba");
                Passenger = Create(types, PassengerId, (GeneralLicenseType)PassengerValue,
                    TrainServiceReworkTranslations.LicensePassengerKey, "passenger_maintenance.rgba");

                types.RecalculateCaches();

                // both need Manual Service (set after the caches know every license)
                GeneralLicenseType_v2? manual = GeneralLicenseType.ManualService.ToV2();

                foreach (TsrLicenseV2 license in Added)
                {
                    if (manual != null)
                    {
                        license.requiredGeneralLicense = manual;
                        license.color = manual.color;
                    }
                }

                Main.Log("LICENSES -> created: " + string.Join(", ", Added.Select(l => l.id)));
            }
            catch (Exception ex)
            {
                Main.LogError("LICENSES -> creating the maintenance licenses failed: " + ex);
            }
        }

        private static TsrLicenseV2? Create(DVObjectModel types, string id, GeneralLicenseType value, string key, string iconFile)
        {
            if (types.generalLicenses.Any(l => l != null && l.id == id))
            {
                Main.LogWarning($"LICENSES -> {id} already exists, skipped");
                return null;
            }

            TsrLicenseV2 license = ScriptableObject.CreateInstance<TsrLicenseV2>();
            license.name = "TSR_License_" + id;
            license.id = id;
            license.v1 = value;
            license.localizationKey = key;
            license.localizationKeysDescription = new[] { key + "_desc" };
            license.ItemKey = key;
            license.InfoItemKey = key;
            license.color = new Color32(43, 166, 166, 255);
            license.price = DefaultPrice;
            license.insuranceFeeQuotaIncrease = 0f;
            license.bonusTimeDecreasePercentage = 0f;
            license.icon = LoadIcon(Path.Combine(Path.Combine(Main.ModPath, "Licenses"), iconFile));

            CreatePrefabs(license);

            types.generalLicenses.Add(license);
            Added.Add(license);
            AddedValues.Add(value);
            return license;
        }

        /// <summary>Raw RGBA (256×256, bottom → top) – needs no ImageConversion module.</summary>
        private static Sprite? LoadIcon(string path)
        {
            const int size = 256;

            try
            {
                if (!File.Exists(path))
                {
                    Main.LogWarning("LICENSES -> icon missing: " + path);
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(path);

                if (bytes.Length != size * size * 4)
                {
                    Main.LogWarning("LICENSES -> icon has a wrong size: " + path);
                    return null;
                }

                Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    name = Path.GetFileNameWithoutExtension(path),
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                texture.LoadRawTextureData(bytes);
                texture.Apply(false, true);
                Object.DontDestroyOnLoad(texture);

                Sprite sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
                sprite.name = texture.name;
                Object.DontDestroyOnLoad(sprite);
                return sprite;
            }
            catch (Exception ex)
            {
                Main.LogWarning("LICENSES -> icon not loaded: " + ex.Message);
                return null;
            }
        }

        private static Transform Holder
        {
            get
            {
                if (holder == null)
                {
                    GameObject go = new GameObject("[TrainServiceRework License Holder]");
                    go.SetActive(false);
                    Object.DontDestroyOnLoad(go);
                    holder = go.transform;
                }

                return holder;
            }
        }

        private static T Mock<T>(T source) where T : Object => Object.Instantiate(source, Holder);

        private static void SetSpec(GameObject? prefab, string prefabName, string nameKey)
        {
            InventoryItemSpec? spec = prefab != null ? prefab.GetComponent<InventoryItemSpec>() : null;

            if (spec == null)
                return;

            itemPrefabNameField?.SetValue(spec, prefabName);
            spec.localizationKeyName = nameKey;
        }

        /// <summary>License paper and sample booklet like the DE2 one (picture rendered with this license).</summary>
        private static void CreatePrefabs(TsrLicenseV2 license)
        {
            GeneralLicenseType_v2 de2 = GeneralLicenseType.DE2.ToV2();
            license.RenderPrefabName = $"License{license.id}Render";
            license.SampleRenderPrefabName = $"License{license.id}RenderInfo";

            license.licensePrefab = Mock(de2.licensePrefab);
            license.licensePrefab.name = $"License{license.id}";
            RuntimeRenderedStaticTextureBooklet render = license.licensePrefab.GetComponent<RuntimeRenderedStaticTextureBooklet>();
            string de2Render = render.renderPrefabName;
            render.renderPrefabName = license.RenderPrefabName;
            license.RenderPrefab = Mock(Resources.Load<GameObject>(de2Render));
            license.RenderPrefab.name = license.RenderPrefabName;
            license.RenderPrefab.GetComponent<StaticLicenseBookletRender>().generalLicense = license;

            license.licenseInfoPrefab = Mock(de2.licenseInfoPrefab);
            license.licenseInfoPrefab.name = $"License{license.id}Info";
            RuntimeRenderedStaticTextureBooklet infoRender = license.licenseInfoPrefab.GetComponent<RuntimeRenderedStaticTextureBooklet>();
            string de2InfoRender = infoRender.renderPrefabName;
            infoRender.renderPrefabName = license.SampleRenderPrefabName;
            license.SampleRenderPrefab = Mock(Resources.Load<GameObject>(de2InfoRender));
            license.SampleRenderPrefab.name = license.SampleRenderPrefabName;
            license.SampleRenderPrefab.GetComponent<StaticLicenseBookletRender>().generalLicense = license;

            // item name / prefab name already on the template (papers bought in the career manager are not created
            // through CreateLicense – otherwise they would be saved as "LicenseDE2")
            SetSpec(license.licensePrefab, license.licensePrefab.name, license.ItemKey);
            SetSpec(license.licenseInfoPrefab, license.licenseInfoPrefab.name, license.InfoItemKey);

            KeyToPrefab[license.licensePrefab.name] = license.licensePrefab;
            KeyToPrefab[license.licenseInfoPrefab.name] = license.licenseInfoPrefab;
            KeyToPrefab[license.RenderPrefabName] = license.RenderPrefab;
            KeyToPrefab[license.SampleRenderPrefabName] = license.SampleRenderPrefab;
        }

        // =====================================================
        // JOB REQUIREMENT
        // =====================================================

        /// <summary>Passenger car? (same ID rule as CarCategoryHelper: "xPS...")</summary>
        public static bool IsPassengerId(string? carId)
        {
            return !string.IsNullOrEmpty(carId) && carId!.Length >= 3 &&
                   string.Equals(carId.Substring(1, 2), "PS", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>License needed for a maintenance job with these cars.</summary>
        public static TsrLicenseV2? RequiredFor(IEnumerable<string?> carIds)
        {
            return carIds.Any(IsPassengerId) ? Passenger : Freight;
        }

        public static bool IsAcquired(GeneralLicenseType_v2? license)
        {
            if (license == null)
                return true;   // license could not be created – don't block the job

            LicenseManager manager = LicenseManager.Instance;
            return manager == null || manager.IsGeneralLicenseAcquired(license);
        }
    }

    // =========================================================
    // PATCHES
    // =========================================================

    /// <summary>Resources.Load: paper / picture templates of our licenses.</summary>
    [HarmonyPatch]
    public static class TsrLicenseResourcesPatch
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            typeof(Resources).GetMethods().Where(x => x.Name == nameof(Resources.Load) && x.GetParameters().Length == 1 && !x.IsGenericMethod);

        static bool Prefix(string path, ref Object __result)
        {
            if (path == null || MaintenanceLicenses.KeyToPrefab.Count == 0 || !MaintenanceLicenses.KeyToPrefab.TryGetValue(path, out GameObject go))
                return true;

            __result = go;
            return false;
        }
    }

    /// <summary>Render the picture of the license paper (the template is no game asset, it has to be activated).</summary>
    [HarmonyPatch(typeof(BookletCreator_StaticRenderBooklet), nameof(BookletCreator_StaticRenderBooklet.Render))]
    public static class TsrLicenseRenderPatch
    {
        static bool Prefix(GameObject existingBooklet, string renderPrefabName, ref RenderedTexturesBase __result)
        {
            if (renderPrefabName == null || !MaintenanceLicenses.KeyToPrefab.TryGetValue(renderPrefabName, out GameObject prefab))
                return true;

            StaticTextureRenderBase component = Object.Instantiate(prefab, RenderTextureSystem.Instance.transform.position, Quaternion.identity)
                .GetComponent<StaticTextureRenderBase>();
            component.gameObject.SetActive(true);

            RenderedTexturesBooklet booklet = existingBooklet.GetComponent<RenderedTexturesBooklet>();
            booklet.RegisterTexturesGeneratedEvent(component);
            component.GenerateStaticPagesTextures();

            __result = booklet;
            return false;
        }
    }

    /// <summary>Created license paper: name and prefab name of our license.</summary>
    [HarmonyPatch(typeof(BookletCreator_Licenses))]
    public static class TsrLicenseBookletPatch
    {
        private static readonly FieldInfo? itemPrefabNameField = AccessTools.Field(typeof(InventoryItemSpec), "itemPrefabName");

        [HarmonyPostfix, HarmonyPatch(nameof(BookletCreator_Licenses.CreateLicense),
            new[] { typeof(GeneralLicenseType_v2), typeof(Vector3), typeof(Quaternion), typeof(Transform), typeof(bool) })]
        static void LicensePostfix(GeneralLicenseType_v2 license, GameObject __result)
        {
            if (license is TsrLicenseV2 own && own.licensePrefab != null)
                Apply(__result, own.licensePrefab.name, own.ItemKey);
        }

        [HarmonyPostfix, HarmonyPatch(nameof(BookletCreator_Licenses.CreateLicenseInfo),
            new[] { typeof(GeneralLicenseType_v2), typeof(Vector3), typeof(Quaternion), typeof(Transform), typeof(bool) })]
        static void InfoPostfix(GeneralLicenseType_v2 license, GameObject __result)
        {
            if (license is TsrLicenseV2 own && own.licenseInfoPrefab != null)
                Apply(__result, own.licenseInfoPrefab.name, own.InfoItemKey);
        }

        private static void Apply(GameObject go, string prefabName, string nameKey)
        {
            if (go == null)
                return;

            go.name = prefabName;
            InventoryItemSpec spec = go.GetComponent<InventoryItemSpec>();

            if (spec != null)
            {
                spec.localizationKeyName = nameKey;
                itemPrefabNameField?.SetValue(spec, prefabName);
            }

            go.SetActive(true);
        }
    }

    /// <summary>License screen of the career manager: our licenses are added (entry type is private -> reflection).</summary>
    [HarmonyPatch(typeof(CareerManagerLicensesScreen), "Awake")]
    public static class TsrLicenseScreenPatch
    {
        private static readonly FieldInfo? orderField = AccessTools.Field(typeof(CareerManagerLicensesScreen), "licensesDisplayOrder");
        private static readonly Type? entryType = AccessTools.Inner(typeof(CareerManagerLicensesScreen), "GeneralOrJobLicense");
        private static readonly FieldInfo? generalField = entryType != null ? AccessTools.Field(entryType, "generalLicense") : null;

        static void Prefix(CareerManagerLicensesScreen __instance)
        {
            try
            {
                if (entryType == null || generalField == null || !(orderField?.GetValue(__instance) is System.Collections.IList order))
                    return;

                foreach (TsrLicenseV2 license in MaintenanceLicenses.Added)
                {
                    bool present = false;

                    foreach (object entry in order)
                    {
                        if (entry != null && ReferenceEquals(generalField.GetValue(entry), license))
                        {
                            present = true;
                            break;
                        }
                    }

                    if (present)
                        continue;

                    object created = Activator.CreateInstance(entryType, true);
                    generalField.SetValue(created, license);
                    order.Add(created);
                }
            }
            catch (Exception ex)
            {
                Main.LogWarning("LICENSES -> career manager entry failed: " + ex.Message);
            }
        }
    }

    /// <summary>The enum values of our licenses count as valid (like DVCustomLicenses / DVMultiplayerIP).</summary>
    [HarmonyPatch(typeof(Enum))]
    public static class TsrLicenseEnumPatch
    {
        [HarmonyPostfix, HarmonyPatch(nameof(Enum.GetValues))]
        static void GetValuesPostfix(Type enumType, ref Array __result)
        {
            if (enumType != typeof(GeneralLicenseType) || MaintenanceLicenses.AddedValues.Count == 0)
                return;

            GeneralLicenseType[] add = MaintenanceLicenses.AddedValues.ToArray();
            Array result = Array.CreateInstance(typeof(GeneralLicenseType), __result.Length + add.Length);
            Array.Copy(__result, result, __result.Length);
            Array.Copy(add, 0, result, __result.Length, add.Length);
            __result = result;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Enum.IsDefined))]
        static bool IsDefinedPrefix(Type enumType, object value, ref bool __result)
        {
            if (enumType != typeof(GeneralLicenseType) || MaintenanceLicenses.AddedValues.Count == 0)
                return true;

            if (value is int i && MaintenanceLicenses.AddedValues.Contains((GeneralLicenseType)i) ||
                value is GeneralLicenseType g && MaintenanceLicenses.AddedValues.Contains(g))
            {
                __result = true;
                return false;
            }

            return true;
        }
    }
}
