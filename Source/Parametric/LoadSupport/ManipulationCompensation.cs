using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Parametric.LoadSupport
{
    /// <summary>
    /// Removes the SUPERHUMAN manipulation-limb share of vanilla's Manipulation capacity factor on CarryingCapacity
    /// (Load Support represents that enhancement itself) and keeps everything else vanilla applies.
    ///
    /// WHY
    ///   RimWorld 1.6 StatWorker.GetValueUnfinalized applies each entry of stat.capacityFactors as
    ///       val = Mathf.Lerp(val, val * cf.GetFactor(capacities.GetLevel(cf.capacity)), cf.weight)
    ///   i.e. val *= Lerp(1, GetFactor(level), weight), after base, offsets and statFactors (and before StatParts).
    ///   Vanilla CarryingCapacity has a Manipulation factor (weight 1). Load Support's upper region is built from the
    ///   same manipulation limbs, so without this correction a 300% arm set would be applied as
    ///   ×3 (vanilla) × 15.59 (Load Support) instead of ×15.59.
    ///
    /// RULE (enhancement-only)
    ///   Two different questions: Manipulation = can the pawn grip and control the hauled item; Load Support = can the
    ///   body structurally bear the weight. Below baseline BOTH matter, so the vanilla penalty is never touched:
    ///       X = upper-limb efficiency (exactly the limb term of vanilla's Manipulation worker)
    ///       X ≤ 1 (missing / injured / weak / sub-100% prosthetic manipulators) → multiplier 1, vanilla penalty stays
    ///       X > 1 (superhuman manipulators)                                   → vanilla's above-normal limb bonus is
    ///                                                                            replaced by Load Support's scaling
    ///
    /// WHAT (X > 1)
    ///   Y  = the pawn's real Manipulation level (the same cached value vanilla just used)
    ///   Y1 = the Manipulation level the pawn would have with exactly 100% manipulation limbs and every other influence
    ///        unchanged (consciousness, hediff and gene capMods, postFactors, setMax, minValue, rounding) —
    ///        see <see cref="CalculateManipulationWithNeutralLimbs"/>.
    ///   For every capacity factor on the runtime StatDef whose capacity is Manipulation:
    ///       vanilla    = Lerp(1, cf.GetFactor(Y),  cf.weight)
    ///       normalized = Lerp(1, cf.GetFactor(Y1), cf.weight)
    ///       multiplier = Π normalized / vanilla
    ///   cf.GetFactor is the real vanilla method, so allowedDefect, max and useReciprocal are all respected, and a mod
    ///   that changes the factor's configuration (or patches GetFactor) is followed automatically.
    ///
    /// LEFT EXACTLY AS VANILLA COMPUTED IT (multiplier 1)
    ///   • limbs ≤ 100%, NaN or ∞                                    (see RULE)
    ///   • no Manipulation factor on the stat                         (another mod removed it: nothing to undo)
    ///   • body has no manipulation limbs                             (Load Support has no upper region)
    ///   • vanilla factor ≈ 0 (e.g. pawn cannot be awake)             (0 cannot be divided back)
    ///   • neutral-limb factor ≤ 0 (a systemic penalty so large that only the superhuman limbs keep Manipulation
    ///     above 0): vanilla's value is kept rather than zeroing the stat.
    ///
    /// Cost: X ≤ 1 returns before any capacity lookup. X > 1: one pass over hediffs (and active genes) for Manipulation
    /// capMods, two cached GetLevel lookups, two GetFactor calls. No allocation, no reflection.
    /// </summary>
    public static class ManipulationCompensation
    {
        private const float Epsilon = 0.0001f;

        /// <summary>
        /// Vanilla rounds capacity levels to 0.01. A reproduced level that differs from the real one by no more than
        /// half a rounding step (+ float noise) is the same computation; anything else means a non-standard pipeline.
        /// </summary>
        private const float MatchTolerance = 0.0051f;

        public struct Info
        {
            public bool Applied;
            public bool BelowNormalLimbs;     // X ≤ 1: vanilla Manipulation deliberately left untouched
            public bool ExactNeutral;         // Y1 from the reproduced vanilla pipeline (false: Y / X ratio fallback)
            public float ManipulationLevel;   // Y
            public float LimbEfficiency;      // X
            public float NormalizedLevel;     // Y1: Manipulation with 100% limbs
            public float VanillaFactor;
            public float NormalizedFactor;
            public float Multiplier;
        }

        public static float Compute(StatDef stat, Pawn pawn, LoadSupportResult ls)
        {
            Info info;
            return Compute(stat, pawn, ls, out info);
        }

        public static float Compute(StatDef stat, Pawn pawn, LoadSupportResult ls, out Info info)
        {
            info = new Info();
            info.Multiplier = 1f;

            if (stat == null || pawn == null || !ls.HasUpper) return 1f;
            float x = ls.UpperEfficiency;
            info.LimbEfficiency = x;
            // Enhancement-only: disability, weakness and sub-100% prosthetics keep vanilla's Manipulation penalty.
            // (The negated comparison also sends NaN here; ∞ is not a measurable enhancement either.)
            if (!(x > 1f + Epsilon) || float.IsInfinity(x))
            {
                info.BelowNormalLimbs = !(x > 1f + Epsilon);
                return 1f;
            }

            List<PawnCapacityFactor> factors = stat.capacityFactors;
            if (factors == null || factors.Count == 0) return 1f;
            PawnCapacityDef manip = PawnCapacityDefOf.Manipulation;
            if (manip == null || pawn.health == null || pawn.health.capacities == null) return 1f;

            float multiplier = 1f;
            bool haveLevel = false;
            float y = 0f, y1 = 1f;
            bool exact = false;

            for (int i = 0; i < factors.Count; i++)
            {
                PawnCapacityFactor cf = factors[i];
                if (cf == null || cf.capacity != manip) continue;

                if (!haveLevel)
                {
                    y = pawn.health.capacities.GetLevel(manip);
                    y1 = CalculateManipulationWithNeutralLimbs(pawn, x, y, out exact);
                    haveLevel = true;
                }

                float vanilla = Mathf.Lerp(1f, cf.GetFactor(y), cf.weight);
                if (!(vanilla > Epsilon) || float.IsInfinity(vanilla)) continue;

                float normalized = Mathf.Lerp(1f, cf.GetFactor(y1), cf.weight);
                if (!(normalized > 0f) || float.IsInfinity(normalized)) continue;

                multiplier *= normalized / vanilla;
                info.Applied = true;
                info.VanillaFactor = vanilla;
                info.NormalizedFactor = normalized;
            }

            if (float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier <= 0f) multiplier = 1f;

            info.ManipulationLevel = y;
            info.NormalizedLevel = haveLevel ? y1 : 1f;
            info.ExactNeutral = haveLevel && exact;
            info.Multiplier = multiplier;
            return multiplier;
        }

        /// <summary>
        /// Manipulation level the pawn would have if its manipulation limbs were exactly 100% efficient while every other
        /// Manipulation influence stays identical.
        ///
        /// Vanilla 1.6 (verified against the assembly):
        ///   PawnCapacityWorker_Manipulation:  base = CalculateLimbEfficiency(ManipulationLimb*, 0.8) × GetLevel(Consciousness)
        ///   PawnCapacityUtility.CalculateCapacityLevel:
        ///       zeroIfCannotBeAwake && !CanBeAwake → 0
        ///       if base > 0:  base = min((base + Σ offset) × Π postFactor, min setMax)
        ///                     hediff capMods: postFactor scaled by stage.capacityFactorEffectMultiplier (StatWorker.ScaleFactor),
        ///                                     setMax = EvaluateSetMax (curve/stat aware), initial ceiling 99999
        ///                     active gene capMods (Biotech): offset, postFactor, EvaluateSetMax
        ///       level = RoundedHundredth(max(base, def.minValue))
        ///
        /// The capMod phase is independent of the limbs, so it is read once and applied to two bases:
        ///   predicted = phase(X × consciousness)   — must reproduce the real level Y
        ///   neutral   = phase(1 × consciousness)   — the answer
        /// If <c>predicted</c> matches Y (within vanilla's rounding) the pipeline is the standard one — including when
        /// other mods Harmony-patch the vanilla worker without changing its result — and <c>neutral</c> is exact.
        /// Otherwise (custom/replaced Manipulation worker, a patch that changes the math, a malformed hediff) the helper
        /// falls back to the ratio Y / X, which is exact for purely multiplicative influences. Callers only use this for
        /// X > 1, so the fallback can only ever REMOVE superhuman enhancement, never compensate a disability.
        /// </summary>
        public static float CalculateManipulationWithNeutralLimbs(Pawn pawn, float limbEfficiency, float actualLevel, out bool exact)
        {
            exact = false;
            float x = limbEfficiency, y = actualLevel;
            if (!(x > Epsilon) || float.IsInfinity(x) || float.IsNaN(y)) return 1f;

            PawnCapacityDef manip = PawnCapacityDefOf.Manipulation;
            PawnCapacitiesHandler caps = pawn != null && pawn.health != null ? pawn.health.capacities : null;
            HediffSet set = pawn != null && pawn.health != null ? pawn.health.hediffSet : null;
            if (manip != null && caps != null && set != null)
            {
                try
                {
                    if (manip.zeroIfCannotBeAwake && !caps.CanBeAwake)
                    {
                        exact = y == 0f; // vanilla returns 0 regardless of limbs
                        if (exact) return 0f;
                    }
                    else
                    {
                        PawnCapacityDef consciousnessDef = PawnCapacityDefOf.Consciousness;
                        float consciousness = consciousnessDef != null ? caps.GetLevel(consciousnessDef) : 1f;

                        float offset, postFactor, setMax;
                        ReadCapMods(pawn, set, manip, out offset, out postFactor, out setMax);

                        float predicted = ApplyCapModPhase(x * consciousness, offset, postFactor, setMax, manip.minValue);
                        if (Math.Abs(predicted - y) <= MatchTolerance)
                        {
                            exact = true;
                            return ApplyCapModPhase(1f * consciousness, offset, postFactor, setMax, manip.minValue);
                        }
                    }
                }
                catch
                {
                    // A malformed modded hediff/gene: vanilla survived computing Y, so use the conservative ratio.
                }
            }

            // Ratio fallback. If Y is exactly round(X) the systemic part is neutral: 1 exactly, no rounding noise.
            if (Math.Abs(y - GenMath.RoundedHundredth(x)) < Epsilon) return 1f;
            return y / x;
        }

        /// <summary>The generic capMod phase of PawnCapacityUtility.CalculateCapacityLevel, applied to a worker result.</summary>
        private static float ApplyCapModPhase(float workerResult, float offset, float postFactor, float setMax, float minValue)
        {
            float v = workerResult;
            if (v > 0f)
            {
                v += offset;
                v *= postFactor;
                v = Mathf.Min(v, setMax);
            }
            v = Mathf.Max(v, minValue);
            return GenMath.RoundedHundredth(v);
        }

        /// <summary>Σ offset, Π postFactor and min setMax of every Manipulation capMod, in vanilla's order and semantics.</summary>
        private static void ReadCapMods(Pawn pawn, HediffSet set, PawnCapacityDef capacity, out float offset, out float postFactor, out float setMax)
        {
            offset = 0f; postFactor = 1f; setMax = 99999f;

            List<Hediff> hediffs = set.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff h = hediffs[i];
                List<PawnCapacityModifier> mods = h.CapMods;
                if (mods == null) continue;
                for (int j = 0; j < mods.Count; j++)
                {
                    PawnCapacityModifier m = mods[j];
                    if (m.capacity != capacity) continue;
                    offset += m.offset;
                    float pf = m.postFactor;
                    HediffStage stage = h.CurStage;
                    if (stage != null && stage.capacityFactorEffectMultiplier != null)
                        pf = StatWorker.ScaleFactor(pf, h.pawn.GetStatValue(stage.capacityFactorEffectMultiplier, true, -1));
                    postFactor *= pf;
                    float max = m.EvaluateSetMax(pawn);
                    if (max < setMax) setMax = max;
                }
            }

            // genes first: test/modded pawns without a gene tracker never touch ModsConfig.
            if (pawn.genes != null && ModsConfig.BiotechActive)
            {
                List<Gene> genes = pawn.genes.GenesListForReading;
                for (int i = 0; i < genes.Count; i++)
                {
                    Gene g = genes[i];
                    if (!g.Active) continue;
                    List<PawnCapacityModifier> mods = g.def.capMods;
                    if (mods == null) continue;
                    for (int j = 0; j < mods.Count; j++)
                    {
                        PawnCapacityModifier m = mods[j];
                        if (m.capacity != capacity) continue;
                        offset += m.offset;
                        postFactor *= m.postFactor;
                        float max = m.EvaluateSetMax(pawn);
                        if (max < setMax) setMax = max;
                    }
                }
            }
        }
    }
}
