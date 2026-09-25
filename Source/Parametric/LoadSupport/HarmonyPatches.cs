using System;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Parametric.LoadSupport
{
    /// <summary>
    /// PATCH 1 — MassUtility.Capacity(Pawn, StringBuilder)   [Prefix (First) + Postfix (Last) + Finalizer]
    ///
    /// Inventory / caravan mass capacity in RimWorld 1.6 is NOT the CarryingCapacity stat. It is
    /// MassUtility.Capacity = BodySize × 35 (0 if the pawn can never carry). Every caravan, transport pod,
    /// shuttle and trade-gift mass check funnels through CollectionsMassCalculator.Capacity, which calls this
    /// method once per pawn; on-map encumbrance (MassUtility.IsOverEncumbered/FreeSpace), the gear tab, the
    /// caravan dialog's per-pawn "+X kg" (TransferableOneWayWidget.DrawMass) and vanilla's pickup/pack/caravan
    /// loading logic call it too — each exactly once. So this one method carries two Parametric transforms:
    ///
    ///   1. Load Support   ×LS                      (setting "apply to mass capacity")
    ///   2. Overload       ×(2 − policy)            (Parametric.Overload; skipped for a comfortable-capacity query)
    ///
    ///   base/modded capacity × LS (once)            = comfortable capacity
    ///   comfortable capacity × routine multiplier   = the value everyone else sees (routine / exposed capacity)
    ///
    /// EXACTLY ONCE PER PAWN PER LOGICAL CALCULATION (both transforms)
    ///   Another mod can compute a pawn's mass capacity from INSIDE this method by calling MassUtility.Capacity for
    ///   the same pawn again (confirmed in game: Vanilla Expanded Framework's transpiler returns its mass stat, whose
    ///   worker reads the patched method). The prefix/finalizer keep a tiny per-thread stack of the pawns whose
    ///   Capacity is being computed, with one flag PER TRANSFORM: a transform is applied in a call only if it was not
    ///   already applied by a nested call for the same pawn, and applying it marks every enclosing same-pawn call.
    ///   The flags are set whenever a transform is applied, whatever its factor (×1 included), so the guard never
    ///   depends on Load Support or the policy being different from 1. Nested calls for OTHER pawns are independent.
    ///   Cost: a few array writes per call, no allocation (per-thread arrays are created once per thread).
    /// </summary>
    [HarmonyPatch(typeof(MassUtility), nameof(MassUtility.Capacity))]
    [HarmonyPatch(new Type[] { typeof(Pawn), typeof(StringBuilder) })]
    public static class Patch_MassUtility_Capacity
    {
        /// <summary>
        /// Diagnostic switch: false restores the pre-0.1.1 behaviour (every call applies both transforms). Only for
        /// reproducing double scaling in tests/traces; never changed by the mod itself.
        /// </summary>
        public static bool NestingGuardEnabled = true;

        private const int MaxTrackedDepth = 32;
        [ThreadStatic] private static Pawn[] framePawn;
        [ThreadStatic] private static bool[] frameLoadSupportDone;
        [ThreadStatic] private static bool[] frameOverloadDone;
        [ThreadStatic] private static int depth;

        /// <summary>Current MassUtility.Capacity nesting depth on this thread (diagnostics).</summary>
        public static int Depth { get { return depth; } }

        /// <summary>How many enclosing MassUtility.Capacity calls on this thread are for the same pawn (diagnostics).</summary>
        public static int SamePawnDepth(Pawn p, int frame)
        {
            int n = 0;
            if (framePawn == null) return 0;
            for (int i = 0; i < frame && i < MaxTrackedDepth; i++) if (framePawn[i] == p) n++;
            return n;
        }

        // __state = frame index + 1 (0 = our prefix did not run)
        [HarmonyPriority(Priority.First)]
        public static void Prefix(Pawn p, out int __state)
        {
            if (framePawn == null)
            {
                framePawn = new Pawn[MaxTrackedDepth];
                frameLoadSupportDone = new bool[MaxTrackedDepth];
                frameOverloadDone = new bool[MaxTrackedDepth];
            }
            int d = depth;
            if (d < MaxTrackedDepth) { framePawn[d] = p; frameLoadSupportDone[d] = false; frameOverloadDone[d] = false; }
            depth = d + 1;
            __state = d + 1;
        }

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Pawn p, StringBuilder explanation, ref float __result, int __state)
        {
            int frame = __state - 1;
            float incoming = __result;

            // ---- 1. Load Support ----
            float lsFactor;
            MassCapacityDecision decision = DecideLoadSupport(p, frame, __result, out lsFactor);
            if (decision == MassCapacityDecision.Scaled || decision == MassCapacityDecision.Neutral)
            {
                MarkEnclosing(frameLoadSupportDone, p, frame);
                if (decision == MassCapacityDecision.Scaled)
                {
                    __result *= lsFactor;
                    if (explanation != null)
                    {
                        // Vanilla wrote "  - Name: +35 kg" on the current line; extend that line.
                        explanation.Append(" ");
                        explanation.Append("LoadSupport_MassExplanation".Translate(
                            lsFactor.ToStringByStyle(ToStringStyle.FloatTwo, ToStringNumberSense.Factor),
                            __result.ToStringMass()).Resolve());
                    }
                }
            }

            // ---- 2. Overload (routine capacity) ----
            float olFactor;
            OverloadDecision ol = DecideOverload(p, frame, __result, out olFactor);
            if (ol == OverloadDecision.Applied || ol == OverloadDecision.Neutral)
            {
                MarkEnclosing(frameOverloadDone, p, frame);
                if (ol == OverloadDecision.Applied)
                {
                    __result *= olFactor;
                    if (explanation != null)
                    {
                        explanation.Append(" ");
                        explanation.Append("Overload_MassExplanation".Translate(
                            Parametric.Overload.OverloadFormula.PolicyLabel(Parametric.Overload.OverloadFormula.SanitizePolicy(2f - olFactor)),
                            olFactor.ToStringByStyle(ToStringStyle.FloatTwo, ToStringNumberSense.Factor),
                            __result.ToStringMass()).Resolve());
                    }
                }
            }

            if (Parametric.Debug.MassCapacityTrace.Armed)
                Parametric.Debug.MassCapacityTrace.Record(p, explanation != null, frame, incoming, lsFactor, decision, olFactor, ol, __result);
        }

        public static void Finalizer(int __state)
        {
            if (__state > 0)
            {
                int frame = __state - 1;
                if (frame < MaxTrackedDepth && framePawn != null) framePawn[frame] = null; // never hold a pawn alive
                depth = frame;
            }
        }

        /// <summary>This call and every enclosing call for the same pawn now contain the transform.</summary>
        private static void MarkEnclosing(bool[] flags, Pawn p, int frame)
        {
            if (framePawn == null || frame < 0) return;
            if (frame < MaxTrackedDepth) flags[frame] = true;
            for (int i = Math.Min(frame, MaxTrackedDepth) - 1; i >= 0; i--)
                if (framePawn[i] == p) flags[i] = true;
        }

        private static bool DoneInside(bool[] flags, int frame)
        {
            return NestingGuardEnabled && flags != null && frame >= 0 && frame < MaxTrackedDepth && flags[frame];
        }

        private static MassCapacityDecision DecideLoadSupport(Pawn p, int frame, float result, out float factor)
        {
            factor = 1f;
            if (StatPart_LoadSupport.Bypass) return MassCapacityDecision.Bypassed;
            if (!(result > 0f)) return MassCapacityDecision.CannotCarry; // babies, non-pack animals...: leave untouched
            ParametricSettings s = ParametricMod.Settings;
            if (s == null || !s.applyToMassCapacity || !s.LoadSupportAppliesTo(p)) return MassCapacityDecision.DisabledBySettings;
            if (DoneInside(frameLoadSupportDone, frame)) return MassCapacityDecision.AlreadyScaledInside;

            factor = LoadSupportCache.Get(p);
            if (!(factor > 0f) || float.IsInfinity(factor) || Math.Abs(factor - 1f) < 0.0001f) { factor = 1f; return MassCapacityDecision.Neutral; }
            return MassCapacityDecision.Scaled;
        }

        private static OverloadDecision DecideOverload(Pawn p, int frame, float result, out float factor)
        {
            factor = 1f;
            if (StatPart_LoadSupport.Bypass) return OverloadDecision.Bypassed; // "Parametric off" measurement
            if (!(result > 0f)) return OverloadDecision.CannotCarry;
            if (!Parametric.Overload.OverloadUtility.Active) return OverloadDecision.Disabled;
            if (p != null && p == Parametric.Overload.OverloadUtility.ComfortableQueryPawn) return OverloadDecision.ComfortableQuery;
            if (DoneInside(frameOverloadDone, frame)) return OverloadDecision.AlreadyAppliedInside;

            factor = Parametric.Overload.OverloadFormula.RoutineMultiplier(Parametric.Overload.OverloadUtility.PolicyFor(p));
            if (Math.Abs(factor - 1f) < 0.0001f) { factor = 1f; return OverloadDecision.Neutral; }
            return OverloadDecision.Applied;
        }
    }

    public enum MassCapacityDecision
    {
        Scaled,
        AlreadyScaledInside,
        Neutral,
        CannotCarry,
        DisabledBySettings,
        Bypassed
    }

    public enum OverloadDecision
    {
        Applied,
        AlreadyAppliedInside,
        Neutral,
        ComfortableQuery,
        CannotCarry,
        Disabled,
        Bypassed
    }

    /// <summary>
    /// PATCH 2 — HediffSet.DirtyCache()   [Postfix]
    ///
    /// Vanilla calls this whenever the hediff set changes (hediff added/removed, part lost, prosthetic installed,
    /// part restored, stage change via Notify_HediffChanged…). We only flip a bool on our cache entry for that pawn —
    /// no allocation, no calculation. The time-based expiry in LoadSupportCache covers anything this misses.
    /// Overload: drops the per-tick comfortable-capacity cache entry and queues one (coalesced) excess-cargo
    /// reconciliation if the pawn carries droppable cargo on a map. Nothing is dropped from inside DirtyCache.
    /// </summary>
    [HarmonyPatch(typeof(HediffSet), nameof(HediffSet.DirtyCache))]
    public static class Patch_HediffSet_DirtyCache
    {
        public static void Postfix(HediffSet __instance)
        {
            if (__instance == null) return;
            LoadSupportCache.MarkDirty(__instance.pawn);
            Parametric.Overload.OverloadUtility.MarkDirty(__instance.pawn);
            Parametric.Overload.OverloadGameComponent.Notify_CapacityMayHaveDropped(__instance.pawn);
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
