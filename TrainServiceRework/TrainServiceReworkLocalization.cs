// File: TrainServiceReworkLocalization.cs
//
// Native translations – no DVLangHelper needed.
// The texts are put into an own I2 Localization source (the system the game uses itself) and added to the game's
// LocalizationManager once the game has loaded its own languages (DV.Localization.LocalizationLoader.Start).
// Same API as DVLangHelper (TranslationInjector.AddTranslation), so TrainServiceReworkTranslations stays unchanged.
// Works next to an installed DVLangHelper as well (separate source).

using HarmonyLib;
using I2.Loc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TrainServiceRework
{
    /// <summary>The 26 game languages.</summary>
    public enum DVLanguage
    {
        English,
        Bulgarian,
        Chinese_Simple,
        Chinese_Trad,
        Czech,
        Danish,
        Dutch,
        Finnish,
        French,
        German,
        Hindi,
        Hungarian,
        Italian,
        Japanese,
        Korean,
        Norwegian,
        Polish,
        Portuguese,
        Portuguese_BR,
        Romanian,
        Russian,
        Slovak,
        Spanish,
        Swedish,
        Turkish,
        Ukrainian
    }

    public static class DVLanguageInfo
    {
        /// <summary>I2 language code (the game finds the language by this code).</summary>
        public static string Code(this DVLanguage language)
        {
            switch (language)
            {
                case DVLanguage.English: return "en";
                case DVLanguage.Bulgarian: return "bg";
                case DVLanguage.Chinese_Simple: return "zh-CN";
                case DVLanguage.Chinese_Trad: return "zh-TW";
                case DVLanguage.Czech: return "cs";
                case DVLanguage.Danish: return "da";
                case DVLanguage.Dutch: return "nl";
                case DVLanguage.Finnish: return "fi";
                case DVLanguage.French: return "fr";
                case DVLanguage.German: return "de";
                case DVLanguage.Hindi: return "hi";
                case DVLanguage.Hungarian: return "hu";
                case DVLanguage.Italian: return "it";
                case DVLanguage.Japanese: return "ja";
                case DVLanguage.Korean: return "ko";
                case DVLanguage.Norwegian: return "nb";
                case DVLanguage.Polish: return "pl";
                case DVLanguage.Portuguese: return "pt";
                case DVLanguage.Portuguese_BR: return "pt-br";
                case DVLanguage.Romanian: return "ro";
                case DVLanguage.Russian: return "ru";
                case DVLanguage.Slovak: return "sk";
                case DVLanguage.Spanish: return "es";
                case DVLanguage.Swedish: return "sv";
                case DVLanguage.Turkish: return "tr";
                case DVLanguage.Ukrainian: return "uk";
                default: return "en";
            }
        }

        /// <summary>I2 language name.</summary>
        public static string Name(this DVLanguage language)
        {
            switch (language)
            {
                case DVLanguage.Chinese_Simple: return "Chinese (Simplified)";
                case DVLanguage.Chinese_Trad: return "Chinese (Traditional)";
                case DVLanguage.Portuguese_BR: return "Portuguese (Brazil)";
                default: return language.ToString();
            }
        }
    }

    /// <summary>Own I2 language source of this mod (same use as DVLangHelper's TranslationInjector).</summary>
    public sealed class TranslationInjector
    {
        private static readonly List<TranslationInjector> instances = new List<TranslationInjector>();
        private static readonly MethodInfo? addSourceMethod = AccessTools.Method(typeof(LocalizationManager), "AddSource");
        private static bool injectionStarted;

        public readonly string Id;
        private readonly LanguageSourceData data;
        private bool injected;

        public TranslationInjector(string id)
        {
            Id = id;

            GameObject holder = new GameObject(id + "_Translations");
            UnityEngine.Object.DontDestroyOnLoad(holder);

            LanguageSource source = holder.AddComponent<LanguageSource>();
            data = source.SourceData;
            data.ClearAllData();

            foreach (DVLanguage language in Enum.GetValues(typeof(DVLanguage)))
            {
                data.AddLanguage(language.Name(), language.Code());
                data.EnableLanguage(language.Name(), true);
            }

            instances.Add(this);

            // the game has loaded its languages already (mod loaded late): add the source right away
            if (injectionStarted)
                Inject();
        }

        public void AddTranslation(string key, DVLanguage language, string value)
        {
            if (string.IsNullOrEmpty(key) || value == null)
                return;

            int index = data.GetLanguageIndexFromCode(language.Code());

            if (index < 0)
                return;

            TermData term = data.GetTermData(key) ?? data.AddTerm(key);
            term.SetTranslation(index, value);
        }

        private void Inject()
        {
            if (injected || addSourceMethod == null)
                return;

            try
            {
                addSourceMethod.Invoke(null, new object[] { data });
                injected = true;
                Main.Log($"TRANSLATIONS -> language source {Id} added ({data.mTerms.Count} texts, {data.mLanguages.Count} languages)");
            }
            catch (Exception ex)
            {
                Main.LogError($"TRANSLATIONS -> adding language source {Id} failed: {ex.Message}");
            }
        }

        public static void StartInjection()
        {
            injectionStarted = true;

            foreach (TranslationInjector instance in instances)
                instance.Inject();
        }
    }

    /// <summary>After the game has loaded its own languages, our texts are added.</summary>
    [HarmonyPatch(typeof(DV.Localization.LocalizationLoader), "Start")]
    public static class TrainServiceReworkLocalizationLoaderPatch
    {
        static void Postfix(ref IEnumerator __result)
        {
            __result = Wrap(__result);
        }

        private static IEnumerator Wrap(IEnumerator source)
        {
            while (source.MoveNext())
                yield return source.Current;

            TranslationInjector.StartInjection();
        }
    }
}
