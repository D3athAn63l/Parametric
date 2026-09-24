using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace LoadSupport
{
    /// <summary>
    /// Mod entry point: settings + Harmony bootstrap.
    /// Rename note: the mod name lives in About.xml, the translation strings in Languages/, the package id in
    /// About.xml and <see cref="HarmonyId"/>. Nothing else hard-codes the name.
    /// </summary>
    public class LoadSupportMod : Mod
    {
        public const string HarmonyId = "aRed.LoadSupport";

        public static LoadSupportSettings Settings;
        public static LoadSupportMod Instance;

        public LoadSupportMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<LoadSupportSettings>();
            Settings.superhumanExponent = LoadSupportFormula.ClampExponent(Settings.superhumanExponent);

            try
            {
                new Harmony(HarmonyId).PatchAll(typeof(LoadSupportMod).Assembly);
            }
            catch (Exception ex)
            {
                Log.Error("[LoadSupport] Harmony patching failed; caravan/inventory scaling and cache events are disabled. " + ex);
            }
        }

        public override string SettingsCategory()
        {
            return "LoadSupport_ModName".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard();
            ls.Begin(inRect);

            ls.CheckboxLabeled("LoadSupport_SettingEnabled".Translate(), ref Settings.enabled,
                "LoadSupport_SettingEnabledDesc".Translate());
            ls.GapLine();

            float exp = ls.SliderLabeled(
                "LoadSupport_SettingExponent".Translate(Settings.superhumanExponent.ToString("0.00")),
                Settings.superhumanExponent, LoadSupportFormula.MinExponent, LoadSupportFormula.MaxExponent,
                0.5f, "LoadSupport_SettingExponentDesc".Translate());
            Settings.superhumanExponent = Mathf.Round(exp * 20f) / 20f; // 0.05 steps

            // Live preview of what the exponent does.
            ls.Label("LoadSupport_ExponentPreview".Translate(
                Preview(1.25f), Preview(1.5f), Preview(2f), Preview(3f), Preview(5f)));
            ls.GapLine();

            ls.CheckboxLabeled("LoadSupport_SettingMass".Translate(), ref Settings.applyToMassCapacity,
                "LoadSupport_SettingMassDesc".Translate());
            ls.CheckboxLabeled("LoadSupport_SettingNonHumanlike".Translate(), ref Settings.includeNonHumanlike,
                "LoadSupport_SettingNonHumanlikeDesc".Translate());
            ls.GapLine();

            ls.CheckboxLabeled("LoadSupport_SettingInspect".Translate(), ref Settings.showInInspectPane,
                "LoadSupport_SettingInspectDesc".Translate());
            ls.CheckboxLabeled("LoadSupport_SettingDebug".Translate(), ref Settings.debugLogging,
                "LoadSupport_SettingDebugDesc".Translate());
            ls.Gap();

            if (ls.ButtonText("LoadSupport_ResetDefaults".Translate()))
                Settings.ResetToDefaults();

            ls.End();

            // Settings can change any formula input: drop cached values, but only when something changed.
            int after = SettingsHash();
            if (after != lastSettingsHash)
            {
                lastSettingsHash = after;
                LoadSupportCache.InvalidateAll();
            }
        }

        private int lastSettingsHash;

        private static int SettingsHash()
        {
            LoadSupportSettings s = Settings;
            unchecked
            {
                int h = s.superhumanExponent.GetHashCode();
                h = h * 31 + (s.enabled ? 1 : 0);
                h = h * 31 + (s.applyToMassCapacity ? 1 : 0);
                h = h * 31 + (s.includeNonHumanlike ? 1 : 0);
                return h;
            }
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            LoadSupportCache.InvalidateAll();
        }

        private static string Preview(float efficiency)
        {
            return "x" + LoadSupportFormula.EfficiencyToStrength(efficiency, Settings.superhumanExponent).ToString("0.##");
        }
    }

    /// <summary>
    /// Runs after all Defs are loaded. Injects the StatPart into CarryingCapacity.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class LoadSupportBootstrap
    {
        static LoadSupportBootstrap()
        {
            try
            {
                StatPart_LoadSupport.InjectInto(StatDefOf.CarryingCapacity);
            }
            catch (Exception ex)
            {
                Log.Error("[LoadSupport] Failed to attach to the CarryingCapacity stat: " + ex);
            }

            if (LoadSupportMod.Settings != null && LoadSupportMod.Settings.debugLogging)
                LoadSupportDebug.LogStartupReport();
        }
    }
}
