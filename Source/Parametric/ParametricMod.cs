using System;
using HarmonyLib;
using Parametric.LoadSupport;
using RimWorld;
using UnityEngine;
using Verse;

namespace Parametric
{
    /// <summary>
    /// Parametric: a lightweight background systems mod.
    /// v0.1 contains one module, Load Support (body-derived carrying capability).
    ///
    /// This class owns the settings and the single Harmony instance for the whole mod.
    /// </summary>
    public class ParametricMod : Mod
    {
        /// <summary>Stable, unique Harmony ID for the whole mod (all modules patch through it).</summary>
        public const string HarmonyId = "aRed.Parametric";

        /// <summary>Prefix for mod-level log lines.</summary>
        public const string LogPrefix = "[Parametric] ";

        public static ParametricSettings Settings;
        public static ParametricMod Instance;

        public ParametricMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<ParametricSettings>();
            Settings.superhumanExponent = LoadSupportFormula.ClampExponent(Settings.superhumanExponent);

            try
            {
                new Harmony(HarmonyId).PatchAll(typeof(ParametricMod).Assembly);
            }
            catch (Exception ex)
            {
                Log.Error(LogPrefix + "Harmony patching failed; Load Support caravan/inventory scaling and cache events are disabled. " + ex);
            }
        }

        public override string SettingsCategory()
        {
            return "Parametric_ModName".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard();
            ls.Begin(inRect);

            // ---------------- Load Support module ----------------
            Text.Font = GameFont.Medium;
            ls.Label("LoadSupport_SectionHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Label("LoadSupport_SectionDesc".Translate());
            ls.Gap(6f);

            ls.CheckboxLabeled("LoadSupport_SettingEnabled".Translate(), ref Settings.loadSupportEnabled,
                "LoadSupport_SettingEnabledDesc".Translate());

            float exp = ls.SliderLabeled(
                "LoadSupport_SettingExponent".Translate(Settings.superhumanExponent.ToString("0.00")),
                Settings.superhumanExponent, LoadSupportFormula.MinExponent, LoadSupportFormula.MaxExponent,
                0.5f, "LoadSupport_SettingExponentDesc".Translate());
            Settings.superhumanExponent = Mathf.Round(exp * 20f) / 20f; // 0.05 steps

            ls.Label("LoadSupport_ExponentPreview".Translate(
                Preview(1.25f), Preview(1.5f), Preview(2f), Preview(3f), Preview(5f)));

            ls.CheckboxLabeled("LoadSupport_SettingMass".Translate(), ref Settings.applyToMassCapacity,
                "LoadSupport_SettingMassDesc".Translate());
            ls.CheckboxLabeled("LoadSupport_SettingNonHumanlike".Translate(), ref Settings.includeNonHumanlike,
                "LoadSupport_SettingNonHumanlikeDesc".Translate());
            ls.CheckboxLabeled("LoadSupport_SettingInspect".Translate(), ref Settings.showInInspectPane,
                "LoadSupport_SettingInspectDesc".Translate());
            ls.GapLine();

            // ---------------- Mod-level ----------------
            ls.CheckboxLabeled("Parametric_SettingDebug".Translate(), ref Settings.debugLogging,
                "Parametric_SettingDebugDesc".Translate());
            ls.Gap();

            if (ls.ButtonText("Parametric_ResetDefaults".Translate()))
                Settings.ResetToDefaults();

            ls.End();

            // Only the exponent changes cached values; the toggles are read live. Invalidate only on change.
            if (Settings.superhumanExponent != lastExponent)
            {
                lastExponent = Settings.superhumanExponent;
                LoadSupportCache.InvalidateAll();
            }
        }

        private float lastExponent = float.NaN;

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

    /// <summary>Runs after all Defs are loaded (and after XML patches). Attaches module hooks that need Defs.</summary>
    [StaticConstructorOnStartup]
    public static class ParametricBootstrap
    {
        static ParametricBootstrap()
        {
            try
            {
                StatPart_LoadSupport.InjectInto(StatDefOf.CarryingCapacity);
            }
            catch (Exception ex)
            {
                Log.Error(LoadSupportLog.Prefix + "Failed to attach to the CarryingCapacity stat: " + ex);
            }

            if (ParametricMod.Settings != null && ParametricMod.Settings.debugLogging)
                Parametric.Debug.ParametricDebug.LogStartupReport();
        }
    }
}
