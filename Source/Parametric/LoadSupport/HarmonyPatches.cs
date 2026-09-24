using System;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Parametric.LoadSupport
{
    /// <summary>
    /// PATCH 1 — MassUtility.Capacity(Pawn, StringBuilder)   [Postfix]
    ///
    /// Inventory / caravan mass capacity in RimWorld 1.6 is NOT the CarryingCapacity stat. It is
    /// MassUtility.Capacity = BodySize × 35 (0 if the pawn can never carry). Every caravan, transport pod,
    /// shuttle and trade-gift mass check funnels through CollectionsMassCalculator.Capacity, which calls this
    /// method once per pawn; on-map encumbrance (MassUtility.IsOverEncumbered/FreeSpace) calls it too.
    /// A single multiplicative postfix therefore covers caravans with no second subsystem, and it composes with
    /// any other mod's postfix/prefix on the same method (we multiply whatever result we are handed).
    /// MassUtility.Capacity has NO Manipulation factor, so no ManipulationCompensation is applied here.
    /// </summary>
    [HarmonyPatch(typeof(MassUtility), nameof(MassUtility.Capacity))]
    [HarmonyPatch(new Type[] { typeof(Pawn), typeof(StringBuilder) })]
    public static class Patch_MassUtility_Capacity
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Pawn p, StringBuilder explanation, ref float __result)
        {
            if (StatPart_LoadSupport.Bypass) return;
            if (!(__result > 0f)) return; // cannot carry at all (babies, non-pack animals...): leave untouched
            ParametricSettings s = ParametricMod.Settings;
            if (s == null || !s.applyToMassCapacity || !s.LoadSupportAppliesTo(p)) return;

            float factor = LoadSupportCache.Get(p);
            if (!(factor > 0f) || float.IsInfinity(factor) || Math.Abs(factor - 1f) < 0.0001f) return;

            __result *= factor;

            if (explanation != null)
            {
                // Vanilla wrote "  - Name: +35 kg" on the current line; extend that line.
                explanation.Append(" ");
                explanation.Append("LoadSupport_MassExplanation".Translate(
                    factor.ToStringByStyle(ToStringStyle.FloatTwo, ToStringNumberSense.Factor),
                    __result.ToStringMass()).Resolve());
            }
        }
    }

    /// <summary>
    /// PATCH 2 — HediffSet.DirtyCache()   [Postfix]
    ///
    /// Vanilla calls this whenever the hediff set changes (hediff added/removed, part lost, prosthetic installed,
    /// part restored, stage change via Notify_HediffChanged…). We only flip a bool on our cache entry for that pawn —
    /// no allocation, no calculation. The time-based expiry in LoadSupportCache covers anything this misses.
    /// </summary>
    [HarmonyPatch(typeof(HediffSet), nameof(HediffSet.DirtyCache))]
    public static class Patch_HediffSet_DirtyCache
    {
        public static void Postfix(HediffSet __instance)
        {
            if (__instance != null) LoadSupportCache.MarkDirty(__instance.pawn);
        }
    }

    /// <summary>
    /// PATCH 3 — Pawn.GetInspectString()   [Postfix]   (optional display, default OFF)
    ///
    /// Appends "Load support: x1.52" to the selected pawn's inspect pane when the setting is on.
    /// Returns immediately when the setting is off, so the cost is one bool check.
    /// </summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetInspectString))]
    public static class Patch_Pawn_GetInspectString
    {
        public static void Postfix(Pawn __instance, ref string __result)
        {
            ParametricSettings s = ParametricMod.Settings;
            if (s == null || !s.showInInspectPane) return;
            if (__instance == null || !s.LoadSupportAppliesTo(__instance) || __instance.Dead) return;

            try
            {
                float factor = LoadSupportCache.Get(__instance);
                string line = "LoadSupport_InspectLine".Translate(
                    factor.ToStringByStyle(ToStringStyle.FloatTwo, ToStringNumberSense.Factor)).Resolve();
                __result = string.IsNullOrEmpty(__result) ? line : __result.TrimEnd() + "\n" + line;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce(LoadSupportLog.Prefix + "Inspect string failed: " + ex, 0x50524D01);
            }
        }
    }
}
