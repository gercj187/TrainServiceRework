using System;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using DV.Localization;
using DV.ThingTypes;

namespace TrainServiceRework
{
    public class Settings : UnityModManager.ModSettings
    {
        public bool EnableLogs = false;

        // =====================================================
        // IMMERSIVE SERVICE
        // =====================================================
        public bool ImmersiveService = true;
        public int MaxParallelModules = 2;

        // seconds per display unit
        // (water and coal are excluded – they already have their own filling animation)
        public float SecondsFuel = 0.3f;
        public float SecondsOil = 0.6f;
        public float SecondsSand = 0.5f;
        public float SecondsElectric = 1f;
        public float SecondsBody = 5f;
        public float SecondsWheels = 5f;
        public float SecondsMechanical = 6f;
        public float SecondsElectrical = 6f;

        public bool RefundUnfinished = true;

        // =====================================================
        // DAMAGED CAR JOBS
        // =====================================================
        public bool DamagedJobs = true;
        public float DamagedThreshold = 50f;
        public float SoftDamagedThreshold = 75f;
        public float SoftDamagedChance = 10f;
        public float OverhaulPayFactor = 1.25f;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        // =====================================================
        // GUI
        // =====================================================
        private static GUIStyle? tipStyle;
        private static GUIStyle? tooltipLabelStyle;
        private static GUIStyle? headerStyle;

        public void Draw(UnityModManager.ModEntry modEntry)
        {
            EnsureStyles();

            // Clients in multiplayer: the host decides the service settings.
            if (!ImmersiveServiceMultiplayer.IsClientOnly)
            {
                GUILayout.Label(L(TrainServiceReworkTranslations.S("section_immersive")), headerStyle);

                ImmersiveService = Toggle(ImmersiveService, L(TrainServiceReworkTranslations.S("enabled")), L(TrainServiceReworkTranslations.T("enabled")));

                if (ImmersiveService)
                {
                    MaxParallelModules = IntSlider(L(TrainServiceReworkTranslations.S("max_parallel")), MaxParallelModules, 1, 8, TipParallel());

                    GUILayout.Space(6f);
                    GUILayout.Label(L(TrainServiceReworkTranslations.S("time_header")), headerStyle);

                    SecondsBody = SecondsSlider(ResourceType.Car_DMG, "res_body", SecondsBody, 30f, 0.1f);
                    SecondsWheels = SecondsSlider(ResourceType.Wheels_DMG, "res_wheels", SecondsWheels, 30f, 0.1f);
                    SecondsMechanical = SecondsSlider(ResourceType.MechanicalPowertrain_DMG, "res_mech", SecondsMechanical, 30f, 0.1f);
                    SecondsElectrical = SecondsSlider(ResourceType.ElectricalPowertrain_DMG, "res_elec", SecondsElectrical, 30f, 0.1f);
                    SecondsFuel = SecondsSlider(ResourceType.Fuel, "res_fuel", SecondsFuel, 2f, 0.01f);
                    SecondsOil = SecondsSlider(ResourceType.Oil, "res_oil", SecondsOil, 5f, 0.01f);
                    SecondsSand = SecondsSlider(ResourceType.Sand, "res_sand", SecondsSand, 5f, 0.01f);
                    SecondsElectric = SecondsSlider(ResourceType.ElectricCharge, "res_electric", SecondsElectric, 5f, 0.01f);

                    GUILayout.Space(6f);

                    RefundUnfinished = Toggle(RefundUnfinished, L(TrainServiceReworkTranslations.S("refund")), L(TrainServiceReworkTranslations.T("refund")));
                }

                GUILayout.Space(10f);

                // ---------------- damaged car jobs ----------------
                GUILayout.Label(L(TrainServiceReworkTranslations.S("section_damaged")), headerStyle);

                DamagedJobs = Toggle(DamagedJobs, L(TrainServiceReworkTranslations.S("damaged_enabled")), L(TrainServiceReworkTranslations.T("damaged_enabled")));

                if (DamagedJobs)
                {
                    DamagedThreshold = IntSlider(L(TrainServiceReworkTranslations.S("damaged_threshold")) + " (%)",
                        Mathf.RoundToInt(DamagedThreshold), 10, 90,
                        string.Format(L(TrainServiceReworkTranslations.T("damaged_threshold")), Mathf.RoundToInt(DamagedThreshold)));

                    // first threshold: may become a damaged car when it gets a new job (chance)
                    SoftDamagedThreshold = Mathf.Max(DamagedThreshold, IntSlider(L(TrainServiceReworkTranslations.S("soft_threshold")) + " (%)",
                        Mathf.RoundToInt(SoftDamagedThreshold), 10, 99,
                        string.Format(L(TrainServiceReworkTranslations.T("soft_threshold")), Mathf.RoundToInt(SoftDamagedThreshold),
                            Mathf.RoundToInt(DamagedThreshold), Mathf.RoundToInt(SoftDamagedChance))));

                    SoftDamagedChance = IntSlider(L(TrainServiceReworkTranslations.S("soft_chance")) + " (%)",
                        Mathf.RoundToInt(SoftDamagedChance), 0, 100,
                        string.Format(L(TrainServiceReworkTranslations.T("soft_chance")), Mathf.RoundToInt(SoftDamagedChance),
                            Mathf.RoundToInt(DamagedThreshold), Mathf.RoundToInt(SoftDamagedThreshold)));

                    // (no payment factor any more: a maintenance job pays the shunting effort + the repair at the pit stop)
                }

                GUILayout.Space(10f);
            }

            // debug logs: small, bottom right corner
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EnableLogs = GUILayout.Toggle(EnableLogs, " " + L(TrainServiceReworkTranslations.S("logs")), GUILayout.ExpandWidth(false));
            Tip(L(TrainServiceReworkTranslations.T("logs")));
            GUILayout.EndHorizontal();

            DrawTooltip();
        }

        // =====================================================
        // DYNAMIC TOOLTIPS
        // =====================================================
        private string TipParallel()
        {
            // three modules of 60 s each with the current limit
            int slots = Mathf.Clamp(MaxParallelModules, 1, 8);
            int rounds = Mathf.CeilToInt(3f / slots);
            string total = ImmersiveService_FormatTime(rounds * 60f);

            return string.Format(L(TrainServiceReworkTranslations.T("max_parallel")), MaxParallelModules, total);
        }

        private string TipSeconds(ResourceType type, string labelId, float secondsPerUnit)
        {
            string unitText;
            float exampleA;
            float exampleB;

            switch (type)
            {
                case ResourceType.Car_DMG:
                case ResourceType.Wheels_DMG:
                case ResourceType.MechanicalPowertrain_DMG:
                case ResourceType.ElectricalPowertrain_DMG:
                    unitText = L(TrainServiceReworkTranslations.T("unit_percent"));
                    exampleA = 4.04f;
                    exampleB = 50f;
                    break;
                case ResourceType.Fuel:
                    unitText = L(TrainServiceReworkTranslations.T("unit_litre"));
                    exampleA = 100f;
                    exampleB = 1000f;
                    break;
                case ResourceType.Oil:
                    unitText = L(TrainServiceReworkTranslations.T("unit_litre"));
                    exampleA = 10f;
                    exampleB = 100f;
                    break;
                default:
                    unitText = L(TrainServiceReworkTranslations.T("unit_generic"));
                    exampleA = 10f;
                    exampleB = 100f;
                    break;
            }

            return string.Format(
                L(TrainServiceReworkTranslations.T("seconds")),
                L(TrainServiceReworkTranslations.S(labelId)),
                unitText,
                secondsPerUnit.ToString("0.###"),
                exampleA.ToString("0.##"),
                ImmersiveService_FormatTime(exampleA * secondsPerUnit),
                exampleB.ToString("0.##"),
                ImmersiveService_FormatTime(exampleB * secondsPerUnit));
        }

        private static string ImmersiveService_FormatTime(float seconds)
        {
            return ImmersiveServiceSystem.FormatTime(seconds);
        }

        // =====================================================
        // GUI HELPERS (same pattern as LongerLoadingDelay)
        // =====================================================
        private static string L(string key)
        {
            string value = LocalizationAPI.L(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }

        private static void EnsureStyles()
        {
            if (tipStyle == null)
            {
                tipStyle = new GUIStyle(GUI.skin.label);
                tipStyle.fontStyle = FontStyle.Bold;
                tipStyle.normal.textColor = new Color(1f, 0.8f, 0.3f);
            }

            if (tooltipLabelStyle == null)
            {
                tooltipLabelStyle = new GUIStyle(GUI.skin.label);
                tooltipLabelStyle.wordWrap = true;
                tooltipLabelStyle.richText = true;
                tooltipLabelStyle.padding = new RectOffset(10, 10, 8, 8);
                tooltipLabelStyle.normal.textColor = Color.white;
            }

            if (headerStyle == null)
            {
                headerStyle = new GUIStyle(GUI.skin.label);
                headerStyle.fontStyle = FontStyle.Bold;
            }
        }

        private static void Tip(string tip)
        {
            GUILayout.Label(new GUIContent(" ?", tip), tipStyle, GUILayout.ExpandWidth(false));
        }

        private static bool Toggle(bool value, string label, string tip)
        {
            GUILayout.BeginHorizontal();
            bool result = GUILayout.Toggle(value, " " + label, GUILayout.ExpandWidth(false));
            Tip(tip);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return result;
        }

        private static int IntSlider(string label, int value, int min, int max, string tip)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(380f));
            float raw = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(220f));
            int result = Mathf.Clamp(Mathf.RoundToInt(raw), min, max);
            GUILayout.Label(result.ToString(), GUILayout.Width(60f));
            Tip(tip);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return result;
        }

        private static float FactorSlider(string label, float value, float min, float max, string tip)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(380f));
            float raw = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(220f));
            float result = Mathf.Clamp(Mathf.Round(raw * 20f) / 20f, min, max);
            GUILayout.Label("× " + result.ToString("0.00"), GUILayout.Width(60f));
            Tip(tip);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return result;
        }

        private float SecondsSlider(ResourceType type, string labelId, float value, float max, float step)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("   " + L(TrainServiceReworkTranslations.S(labelId)), GUILayout.Width(380f));
            float raw = GUILayout.HorizontalSlider(value, 0f, max, GUILayout.Width(220f));
            float result = Mathf.Clamp(Mathf.Round(raw / step) * step, 0f, max);
            GUILayout.Label(result.ToString("0.##") + " " + L(TrainServiceReworkTranslations.S("unit_seconds")), GUILayout.Width(110f));
            Tip(TipSeconds(type, labelId, result));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return result;
        }

        /// <summary>
        /// The tooltip is NOT drawn here: inside the UMM window it would be cut off at the window border and covered
        /// by the UMM controls. Text and mouse position (screen) go to an own overlay that is drawn on top of
        /// everything and stays inside the screen.
        /// </summary>
        private static void DrawTooltip()
        {
            if (Event.current.type != EventType.Repaint)
                return;

            string tooltip = GUI.tooltip;

            if (!string.IsNullOrEmpty(tooltip))
                SettingsTooltipOverlay.Show(tooltip, GUIUtility.GUIToScreenPoint(Event.current.mousePosition));

            // otherwise UMM shows it a second time
            GUI.tooltip = string.Empty;
        }
    }

    public static class Main
    {
        private static Harmony? harmony;
        private static UnityModManager.ModEntry? modEntry;

        public static Settings Settings { get; private set; } = new Settings();

        public static string ModId => modEntry != null ? modEntry.Info.Id : "TrainServiceRework";
        public static string ModPath => modEntry != null ? modEntry.Path : string.Empty;

        public static bool Load(UnityModManager.ModEntry entry)
        {
            modEntry = entry;

            try
            {
                Settings loaded = UnityModManager.ModSettings.Load<Settings>(entry);
                Settings = loaded ?? new Settings();
                Settings.Save(entry);

                TrainServiceReworkTranslations.Initialize();

                // own licenses (maintenance) – before a savegame is loaded
                MaintenanceLicenses.Register();

                harmony = new Harmony(entry.Info.Id);
                harmony.PatchAll();

                // Immersive Service runtime + optional multiplayer bridge.
                ImmersiveServiceSystem.CreateRunner();

                // Multiplayer only after all mods are loaded (load order not guaranteed)
                entry.OnLateUpdate += LateInit;

                entry.OnGUI = OnGUI;
                entry.OnSaveGUI = OnSaveGUI;

                entry.Logger.Log("mod loaded successfully");

                if (Settings.EnableLogs)
                {
                    entry.Logger.Log("Debug logging enabled.");
                }

                return true;
            }
            catch (Exception ex)
            {
                entry.Logger.Error(
                    "mod failed to load: " + ex);

                return false;
            }
        }

        /// <summary>true after the first frame: all mods are loaded (optional compatibility is checked once then).</summary>
        public static bool AllModsLoaded { get; private set; }

        private static void LateInit(UnityModManager.ModEntry entry, float dt)
        {
            entry.OnLateUpdate -= LateInit;
            AllModsLoaded = true;
            ImmersiveServiceMultiplayer.Init(entry);

            // optional old Persistent Jobs compatibility (checked once)
            PersistentJobsDamageCompatibility.TryPatch();

            // optional PersistentJobsRework hooks for the damaged car jobs (after all mods are loaded)
            DamagedJobsPersistentJobsCompat.TryPatch();
        }

        private static void OnGUI(UnityModManager.ModEntry entry)
        {
            Settings.Draw(entry);
        }

        private static void OnSaveGUI(UnityModManager.ModEntry entry)
        {
            Settings.Save(entry);
            ImmersiveServiceSystem.RefreshLocalConfig();
            ImmersiveServiceMultiplayer.BroadcastConfig();
        }

        public static void Log(string message)
        {
            if (!Settings.EnableLogs)
                return;

            Debug.Log("[TrainServiceRework] " + message);
        }

        public static void LogWarning(string message)
        {
            Debug.LogWarning("[TrainServiceRework] " + message);
        }

        public static void LogError(string message)
        {
            Debug.LogError("[TrainServiceRework] " + message);
        }
    }

    // =========================================================
    // SETTINGS TOOLTIP OVERLAY
    // Drawn as an own window on top of the UMM window; placed next to the mouse and moved so that it never
    // leaves the screen (left of the mouse if there is no room on the right, above it if there is no room below).
    // =========================================================
    public class SettingsTooltipOverlay : MonoBehaviour
    {
        private const float Width = 460f;
        private const int WindowId = 0x7A51_0187;

        private static SettingsTooltipOverlay? instance;
        private static string text = string.Empty;
        private static Vector2 mouse;
        private static int shownFrame = -10;

        private GUIStyle? labelStyle;
        private GUIContent content = new GUIContent();
        private Rect rect;

        public static void Show(string tooltip, Vector2 screenMouse)
        {
            text = tooltip;
            mouse = screenMouse;
            shownFrame = Time.frameCount;

            if (instance == null)
            {
                GameObject go = new GameObject("[TrainServiceRework] SettingsTooltip");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<SettingsTooltipOverlay>();
            }
        }

        private void OnGUI()
        {
            // only while the settings are drawn (the tooltip is set again every frame)
            if (string.IsNullOrEmpty(text) || Time.frameCount - shownFrame > 2)
                return;

            GUI.depth = -10000;

            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label)
                {
                    wordWrap = true,
                    richText = true,
                    padding = new RectOffset(10, 10, 8, 8)
                };
                labelStyle.normal.textColor = Color.white;
            }

            content.text = text;

            float width = Mathf.Min(Width, Screen.width - 8f);
            float height = Mathf.Min(labelStyle.CalcHeight(content, width), Screen.height - 8f);

            float x = mouse.x + 18f;
            float y = mouse.y + 12f;

            if (x + width > Screen.width - 4f)
                x = mouse.x - width - 18f;

            if (y + height > Screen.height - 4f)
                y = mouse.y - height - 12f;

            x = Mathf.Clamp(x, 4f, Mathf.Max(4f, Screen.width - width - 4f));
            y = Mathf.Clamp(y, 4f, Mathf.Max(4f, Screen.height - height - 4f));
            rect = new Rect(x, y, width, height);

            GUI.Window(WindowId, rect, DrawWindow, GUIContent.none, GUIStyle.none);
            GUI.BringWindowToFront(WindowId);
        }

        private void DrawWindow(int id)
        {
            Rect local = new Rect(0f, 0f, rect.width, rect.height);
            Color oldColor = GUI.color;

            GUI.color = new Color(0f, 0f, 0f, 0.92f);
            GUI.DrawTexture(local, Texture2D.whiteTexture);

            GUI.color = new Color(1f, 0.8f, 0.3f, 1f);
            GUI.DrawTexture(new Rect(0f, 0f, local.width, 2f), Texture2D.whiteTexture);

            GUI.color = oldColor;

            if (labelStyle != null)
                GUI.Label(local, content, labelStyle);
        }
    }
}
