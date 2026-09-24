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
    /// method once per pawn; on-map encumbrance (MassUtility.IsOverEncumbered/FreeSpace), the gear tab and the
    /// caravan dialog's per-pawn "+X kg" (TransferableOneWayWidget.DrawMass) call it too — each exactly once.
    /// A single multiplicative postfix therefore covers caravans with no second subsystem, and it composes with
    /// any other mod's postfix/prefix on the same method (we multiply whatever result we are handed).
    /// MassUtility.Capacity has NO Manipulation factor, so no ManipulationCompensation is applied here.
    ///
    /// EXACTLY ONCE PER PAWN PER LOGICAL CALCULATION
    ///   Another mod can compute a pawn's mass capacity from INSIDE this method by calling MassUtility.Capacity for
    ///   the same pawn again (e.g. a prefix/postfix that turns mass capacity into a stat whose worker reads the
    ///   patched method, guarded against its own recursion). The inner call already carries Load Support, so
    ///   multiplying the outer result again gives base × LS × LS. The prefix/finalizer keep a tiny per-thread stack
    ///   of the pawns whose Capacity is being computed; a call is scaled only if no nested call FOR THE SAME PAWN
    ///   was already scaled inside it. Nested calls for OTHER pawns are independent and scaled normally.
    ///   Cost: a few array writes per call, no allocation (the per-thread arrays are created once per thread).
    /// </summary>
    [HarmonyPatch(typeof(MassUtility), nameof(MassUtility.Capacity))]
    [HarmonyPatch(new Type[] { typeof(Pawn), typeof(StringBuilder) })]
    public static class Patch_MassUtility_Capacity
    {
        /// <summary>
        /// Diagnostic switch: false restores the pre-0.1.1 behaviour (every call scales). Only for reproducing
        /// double scaling in tests/traces; never changed by the mod itself.
        /// </summary>
        public static bool NestingGuardEnabled = true;

        private const int MaxTrackedDepth = 32;
        [ThreadStatic] private static Pawn[] framePawn;
        [ThreadStatic] private static bool[] frameScaled;
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
            if (framePawn == null) { framePawn = new Pawn[MaxTrackedDepth]; frameScaled = new bool[MaxTrackedDepth]; }
            int d = depth;
            if (d < MaxTrackedDepth) { framePawn[d] = p; frameScaled[d] = false; }
            depth = d + 1;
            __state = d + 1;
        }

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Pawn p, StringBuilder explanation, ref float __result, int __state)
        {
            int frame = __state - 1;
            float incoming = __result;
            float factor = 1f;
            MassCapacityDecision decision = Decide(p, frame, __result, out factor);

            if (decision == MassCapacityDecision.Scaled)
            {
                __result *= factor;
                // Tell enclosing calls for the same pawn that Load Support is already inside their value.
                if (framePawn != null)
                    for (int i = Math.Min(frame, MaxTrackedDepth) - 1; i >= 0; i--)
                        if (framePawn[i] == p) frameScaled[i] = true;

                if (explanation != null)
                {
                    // Vanilla wrote "  - Name: +35 kg" on the current line; extend that line.
                    explanation.Append(" ");
                    explanation.Append("LoadSupport_MassExplanation".Translate(
                        factor.ToStringByStyle(ToStringStyle.FloatTwo, ToStringNumberSense.Factor),
                        __result.ToStringMass()).Resolve());
                }
            }

            if (Parametric.Debug.MassCapacityTrace.Armed)
                Parametric.Debug.MassCapacityTrace.Record(p, explanation != null, frame, incoming, factor, decision, __result);
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

        private static MassCapacityDecision Decide(Pawn p, int frame, float result, out float factor)
        {
            factor = 1f;
            if (StatPart_LoadSupport.Bypass) return MassCapacityDecision.Bypassed;
            if (!(result > 0f)) return MassCapacityDecision.CannotCarry; // babies, non-pack animals...: leave untouched
            ParametricSettings s = ParametricMod.Settings;
            if (s == null || !s.applyToMassCapacity || !s.LoadSupportAppliesTo(p)) return MassCapacityDecision.DisabledBySettings;
            if (NestingGuardEnabled && frame >= 0 && frame < MaxTrackedDepth && frameScaled != null && frameScaled[frame])
                return MassCapacityDecision.AlreadyScaledInside;

            factor = LoadSupportCache.Get(p);
            if (!(factor > 0f) || float.IsInfinity(factor) || Math.Abs(factor - 1f) < 0.0001f) { factor = 1f; return MassCapacityDecision.Neutral; }
            return MassCapacityDecision.Scaled;
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
