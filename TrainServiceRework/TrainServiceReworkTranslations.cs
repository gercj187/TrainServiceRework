using DVLangHelper.Data;
using DVLangHelper.Runtime;

namespace TrainServiceRework
{
    // =========================================================
    // TRAIN SERVICE REWORK TRANSLATIONS
    // =========================================================
    public static class TrainServiceReworkTranslations
    {
        public const string DamagedFreightJobTypeKey = "train_service_rework/job/damaged_freight";
        public const string RepairEmptyHaulJobTypeKey = "train_service_rework/job/repair_empty_haul";

        private static TranslationInjector? injector;

        public static void Initialize()
        {
            if (injector != null)
                return;

            TranslationInjector translations = new TranslationInjector("CJ187.TrainServiceRework");

            injector = translations;

            // =================================================
            // FREIGHT HAUL < 50 %
            // =================================================

            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.English, "SPECIAL HAUL");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Czech, "SPECIÁLNÍ PŘEPRAVA");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Danish, "SPECIALTRANSPORT");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.German, "SONDERBEFÖRDERUNG");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Spanish, "TRANSPORTE ESPECIAL");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Finnish, "ERIKOISKULJETUS");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.French, "TRANSPORT SPÉCIAL");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Hindi, "विशेष परिवहन");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Hungarian, "KÜLÖNLEGES FUVAR");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Italian, "TRASPORTO SPECIALE");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Japanese, "特別輸送");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Korean, "특별 수송");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Norwegian, "SPESIALTRANSPORT");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Dutch, "SPECIAAL TRANSPORT");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Polish, "TRANSPORT SPECJALNY");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Portuguese, "TRANSPORTE ESPECIAL");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Portuguese_BR, "TRANSPORTE ESPECIAL");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Romanian, "TRANSPORT SPECIAL");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Russian, "СПЕЦИАЛЬНАЯ ПЕРЕВОЗКА");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Slovak, "ŠPECIÁLNA PREPRAVA");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Swedish, "SPECIALTRANSPORT");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Turkish, "ÖZEL TAŞIMA");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Ukrainian, "СПЕЦІАЛЬНЕ ПЕРЕВЕЗЕННЯ");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Chinese_Simple, "特殊运输");
            translations.AddTranslation(DamagedFreightJobTypeKey, DVLanguage.Chinese_Trad, "特殊運輸");

            // =================================================
            // LOGISTICAL HAUL <= 50 % / REPAIR ROUTE
            // =================================================

            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.English, "MAINTENANCE HAUL");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Czech, "PŘEPRAVA DO OPRAVY");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Danish, "VEDLIGEHOLDELSESTRANSPORT");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.German, "SCHADWAGENTRANSPORT");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Spanish, "TRANSPORTE DE MANTENIMIENTO");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Finnish, "HUOLTOKULJETUS");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.French, "TRANSPORT DE MAINTENANCE");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Hindi, "रखरखाव परिवहन");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Hungarian, "KARBANTARTÁSI FUVAR");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Italian, "TRASPORTO DI MANUTENZIONE");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Japanese, "整備輸送");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Korean, "정비 수송");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Norwegian, "VEDLIKEHOLDSTRANSPORT");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Dutch, "ONDERHOUDSTRANSPORT");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Polish, "TRANSPORT SERWISOWY");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Portuguese, "TRANSPORTE DE MANUTENÇÃO");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Portuguese_BR, "TRANSPORTE DE MANUTENÇÃO");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Romanian, "TRANSPORT PENTRU MENTENANȚĂ");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Russian, "ПЕРЕГОН НА РЕМОНТ");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Slovak, "PREPRAVA DO OPRAVY");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Swedish, "UNDERHÅLLSTRANSPORT");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Turkish, "BAKIM TAŞIMASI");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Ukrainian, "ПЕРЕГІН НА РЕМОНТ");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Chinese_Simple, "维修运输");
            translations.AddTranslation(RepairEmptyHaulJobTypeKey, DVLanguage.Chinese_Trad, "維修運輸");

            Main.Log("TRAIN SERVICE REWORK TRANSLATIONS -> REGISTERED");
        }
    }
}