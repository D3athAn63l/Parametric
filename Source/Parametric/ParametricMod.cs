using System;
using System.Collections.Generic;
using HarmonyLib;
using Parametric.LoadSupport;
using RimWorld;
using UnityEngine;
using Verse;

namespace Parametric
{
    /// <summary>
    /// Parametric: a lightweight background systems mod.
    /// Modules: Load Support (body-derived carrying capability) and Overload (routine capacity above comfortable,
    /// reactive movement slowdown, event-driven excess-cargo spill).
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

            // ---------------- Overload module ----------------
            Text.Font = GameFont.Medium;
            ls.Label("Overload_SectionHeader".Translate());
            Text.Font = GameFont.Small;
            ls.Label("Overload_SectionDesc".Translate());
            ls.Gap(6f);
            ls.CheckboxLabeled("Overload_SettingEnabled".Translate(), ref Settings.overloadEnabled, "Overload_SettingEnabledDesc".Translate());
            if (ls.ButtonTextLabeled("Overload_SettingPlayerDefault".Translate(),
                    Parametric.Overload.Command_OverloadPolicy.PolicyMenuLabel(Settings.overloadPlayerDefault)))
                Find.WindowStack.Add(new FloatMenu(PolicyOptions(v => Settings.overloadPlayerDefault = v)));
            if (ls.ButtonTextLabeled("Overload_SettingNonPlayerDefault".Translate(),
                    Parametric.Overload.Command_OverloadPolicy.PolicyMenuLabel(Settings.overloadNonPlayerDefault)))
                Find.WindowStack.Add(new FloatMenu(PolicyOptions(v => Settings.overloadNonPlayerDefault = v)));
            ls.Label("Overload_SettingDefaultsDesc".Translate());
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
            Parametric.Overload.OverloadUtility.InvalidateAll();
            // A stricter default (or re-enabling the module) can leave pawns above their routine limit: queue a check
            // for every spawned pawn that carries something. One-off, only when the settings window is closed.
            try
            {
                Parametric.Overload.OverloadGameComponent comp = Parametric.Overload.OverloadGameComponent.Instance;
                if (comp != null && Settings.overloadEnabled && Current.ProgramState == ProgramState.Playing && Find.Maps != null)
                    foreach (Map map in Find.Maps)
                        foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                            if (Parametric.Overload.OverloadUtility.HasDroppableCargo(p)) comp.RequestReconciliation(p, 0);
            }
            catch (Exception ex)
            {
                Log.Warning(Parametric.Overload.OverloadLog.Prefix + "Could not queue reconciliation after a settings change: " + ex.Message);
            }
        }

        private static List<FloatMenuOption> PolicyOptions(Action<float> set)
        {
            var options = new List<FloatMenuOption>();
            foreach (float preset in Parametric.Overload.OverloadFormula.Presets)
            {
                float v = preset;
                options.Add(new FloatMenuOption(Parametric.Overload.Command_OverloadPolicy.PolicyMenuLabel(v), () => set(v)));
            }
            return options;
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

            try
            {
                Parametric.Overload.StatPart_Overload.InjectInto(StatDefOf.MoveSpeed);
                // After StatPart_LoadSupport (injected above): comfortable hand capacity includes Load Support.
                Parametric.Overload.StatPart_OverloadHandCarry.InjectInto(StatDefOf.CarryingCapacity);
            }
            catch (Exception ex)
            {
                Log.Error(Parametric.Overload.OverloadLog.Prefix + "Failed to attach to the MoveSpeed/CarryingCapacity stats: " + ex);
            }

            if (ParametricMod.Settings != null && ParametricMod.Settings.debugLogging)
                Parametric.Debug.ParametricDebug.LogStartupReport();
        }
    }
}
