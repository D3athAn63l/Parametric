using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace Parametric.LoadSupport
{
    /// <summary>
    /// Removes the part of vanilla's Manipulation capacity factor on CarryingCapacity that Load Support already
    /// measures (manipulation-limb efficiency), and keeps everything else vanilla applies.
    ///
    /// WHY
    ///   RimWorld 1.6 StatWorker.GetValueUnfinalized applies each entry of stat.capacityFactors as
    ///       val = Mathf.Lerp(val, val * cf.GetFactor(capacities.GetLevel(cf.capacity)), cf.weight)
    ///   i.e. val *= Lerp(1, GetFactor(level), weight), after base, offsets and statFactors (and before StatParts).
    ///   Vanilla CarryingCapacity has a Manipulation factor (weight 1). Load Support's upper-body region is built
    ///   from the same manipulation limbs, so without this correction a 300% bionic arm set would be applied as
    ///   ×3 (vanilla) × 15.59 (Load Support) instead of ×15.59.
    ///
    /// WHAT
    ///   Vanilla Manipulation level Y = limbEfficiency X × (systemic part: consciousness, capMods, genes, custom
    ///   worker logic). Load Support represents X. So for every capacity factor on the runtime StatDef whose
    ///   capacity is Manipulation:
    ///       vanilla    = Lerp(1, cf.GetFactor(Y),     cf.weight)   // what vanilla multiplied in
    ///       normalized = Lerp(1, cf.GetFactor(Y / X), cf.weight)   // what vanilla would multiply with perfect (100%) limbs
    ///                                                              // (see NormalizedLevel for the rounding/offset details)
    ///       multiplier = normalized / vanilla
    ///   cf.GetFactor is the real vanilla method, so allowedDefect, max and useReciprocal are all respected, and a
    ///   mod that changes the factor's configuration (or patches GetFactor) is followed automatically.
    ///
    ///   Healthy limbs (X = 1): multiplier = 1, nothing changes.
    ///   Bionic arms, consciousness 100%: the limb bonus is removed from vanilla and supplied by Load Support instead.
    ///   Consciousness 50%, healthy arms: multiplier = 1, vanilla's ×0.5 stays (Load Support does not model it).
    ///
    /// NOT CORRECTABLE (left exactly as vanilla computed it)
    ///   • no Manipulation factor on the stat (e.g. another mod removed it) → nothing to do
    ///   • body has no manipulation limbs (Load Support has no upper region) → nothing was double-counted
    ///   • X ≈ 0 (all manipulators lost) or vanilla factor ≈ 0 → the value vanilla produced is 0 and cannot be
    ///     divided back; the pawn has no working hands anyway, so vanilla's result stands.
    ///
    /// Cost: one short loop over capacityFactors (usually 1 entry), one cached GetLevel lookup, two GetFactor calls.
    /// No allocation. Y is read live (the same cached level vanilla just used in the same stat evaluation).
    /// </summary>
    public static class ManipulationCompensation
    {
        private const float Epsilon = 0.0001f;

        public struct Info
        {
            public bool Applied;
            public float ManipulationLevel;   // Y
            public float LimbEfficiency;      // X
            public float NormalizedLevel;     // Y with perfect limbs (≈ Y / X)
            public float VanillaFactor;
            public float NormalizedFactor;
            public float Multiplier;
        }

        /// <summary>
        /// Manipulation level vanilla would have with 100% limbs. Vanilla's level is X × S (S = consciousness, capMods,
        /// genes, …) rounded to 0.01 (GenMath.RoundedHundredth). If Y is exactly round(X), the systemic part is neutral
        /// and the result is exactly 1 — this removes rounding noise for the common "only limbs changed" case.
        /// Otherwise S is estimated as Y / X (exact for multiplicative effects such as consciousness and postFactors;
        /// an approximation for additive capMod offsets combined with non-100% limbs; within vanilla's 0.01 rounding).
        /// </summary>
        public static float NormalizedLevel(float y, float x)
        {
            if (Mathf.Abs(y - GenMath.RoundedHundredth(x)) < 0.0001f) return 1f;
            return y / x;
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
            List<PawnCapacityFactor> factors = stat.capacityFactors;
            if (factors == null || factors.Count == 0) return 1f;
            PawnCapacityDef manip = PawnCapacityDefOf.Manipulation;
            if (manip == null || pawn.health == null || pawn.health.capacities == null) return 1f;

            float x = ls.UpperEfficiency;
            if (!(x > Epsilon)) return 1f;

            float multiplier = 1f;
            bool haveLevel = false;
            float y = 0f;

            for (int i = 0; i < factors.Count; i++)
            {
                PawnCapacityFactor cf = factors[i];
                if (cf == null || cf.capacity != manip) continue;

                if (!haveLevel)
                {
                    y = pawn.health.capacities.GetLevel(manip);
                    haveLevel = true;
                }

                float vanilla = Mathf.Lerp(1f, cf.GetFactor(y), cf.weight);
                if (!(vanilla > Epsilon) || float.IsInfinity(vanilla)) continue;

                float normalized = Mathf.Lerp(1f, cf.GetFactor(NormalizedLevel(y, x)), cf.weight);
                if (float.IsNaN(normalized) || float.IsInfinity(normalized) || normalized < 0f) continue;

                multiplier *= normalized / vanilla;
                info.Applied = true;
                info.VanillaFactor = vanilla;
                info.NormalizedFactor = normalized;
            }

            if (float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier <= 0f) multiplier = 1f;

            info.ManipulationLevel = y;
            info.NormalizedLevel = haveLevel ? NormalizedLevel(y, x) : 1f;
            info.LimbEfficiency = x;
            info.Multiplier = multiplier;
            return multiplier;
        }
    }
}
