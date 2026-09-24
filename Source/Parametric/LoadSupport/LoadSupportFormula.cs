using System;

namespace Parametric.LoadSupport
{
    /// <summary>
    /// Pure math for turning three body-region efficiencies into a single LoadSupport multiplier.
    /// This file deliberately has NO RimWorld/Unity dependencies so it can be unit-tested outside the game.
    ///
    /// ─────────────────────────────────────────────────────────────────────────────
    ///  FINAL FORMULA (v0.1)
    /// ─────────────────────────────────────────────────────────────────────────────
    ///
    ///  1. Region efficiency e_r  (r ∈ Lower, Core, Upper), 1.0 = healthy baseline for THIS pawn's own body.
    ///
    ///  2. Strength curve  s = f(e):
    ///        e ≤ 0      → 0
    ///        0 < e ≤ 1  → e                (linear degradation for injured / weak bodies, no collapse)
    ///        e > 1      → e ^ k            (superhuman scaling, k = exponent setting, default 2.5)
    ///     Continuous at e = 1 (both branches give 1.0).
    ///
    ///  3. Structural chain (legs → core): weighted HARMONIC mean
    ///        S = (wL + wC) / (wL / sL + wC / sC)
    ///     Load has to pass through every link of the chain, so the weakest link dominates.
    ///     With a normal core (sC = 1) S can never exceed (wL + wC) / wC ≈ 2.43, no matter how strong the legs are.
    ///     That asymptote IS the structural bottleneck; no tiers or hard caps are needed.
    ///
    ///  4. Upper body (arms/hands) lifts the load, but whatever it lifts still has to be held up by the chain.
    ///     Its effective strength is the (equal-weight) harmonic mean of its own strength and the chain's:
    ///        U' = 2 · sU · S / (sU + S)
    ///     then the chain and the arms are blended by weight:
    ///        LoadSupport = (wS · S + wU · U') / (wS + wU),   wS = wL + wC
    ///     So super-arms on a normal body help only a little, and super-legs on a normal core are capped,
    ///     while a body enhanced everywhere gets the full e^k scaling.
    ///
    ///  5. Guard rails: NaN → 1.0, clamp to [MinLoadSupport, MaxLoadSupport]. The maximum is only there to stay finite.
    ///
    ///  Weights: wL = 0.50, wC = 0.35, wU = 0.15. A region that does not exist in the pawn's anatomy
    ///  (e.g. no manipulation limbs on an animal) is removed and the remaining weights renormalise,
    ///  so a healthy pawn of ANY anatomy evaluates to exactly 1.0.
    ///
    ///  Debug-only derived values:
    ///    WeightedSupport  = naive arithmetic mean Σ w·s (what the load would be with NO bottleneck)
    ///    BottleneckFactor = LoadSupport / WeightedSupport  (1.0 = no structural penalty)
    /// </summary>
    public static class LoadSupportFormula
    {
        public const float WeightLower = 0.50f;
        public const float WeightCore = 0.35f;
        public const float WeightUpper = 0.15f;

        public const float DefaultExponent = 2.5f;
        public const float MinExponent = 1.0f;
        public const float MaxExponent = 4.0f;

        /// <summary>Floor for a completely wrecked body. Keeps capacity valid (never 0 / negative).</summary>
        public const float MinLoadSupport = 0.05f;

        /// <summary>Not a gameplay cap: only prevents float overflow from absurd modded efficiencies.</summary>
        public const float MaxLoadSupport = 1000000f;

        /// <summary>Strengths below this are treated as this value inside the harmonic mean to avoid division by zero.</summary>
        private const float HarmonicEpsilon = 0.0001f;

        public static float ClampExponent(float exponent)
        {
            if (float.IsNaN(exponent) || float.IsInfinity(exponent)) return DefaultExponent;
            if (exponent < MinExponent) return MinExponent;
            if (exponent > MaxExponent) return MaxExponent;
            return exponent;
        }

        /// <summary>Efficiency → strength. Linear below 1.0, power curve above 1.0.</summary>
        public static float EfficiencyToStrength(float efficiency, float exponent)
        {
            if (!(efficiency > 0f)) return 0f; // catches NaN, negative and zero
            if (float.IsPositiveInfinity(efficiency)) return MaxLoadSupport;
            if (efficiency <= 1f) return efficiency;
            double s = Math.Pow(efficiency, ClampExponent(exponent));
            if (double.IsNaN(s)) return 1f;
            if (s > MaxLoadSupport) return MaxLoadSupport;
            return (float)s;
        }

        /// <summary>
        /// Combine region efficiencies. Pass hasX = false for regions the anatomy does not have at all.
        /// </summary>
        public static LoadSupportResult Compute(
            bool hasLower, float lowerEfficiency,
            bool hasCore, float coreEfficiency,
            bool hasUpper, float upperEfficiency,
            float exponent)
        {
            var r = new LoadSupportResult();
            r.HasLower = hasLower;
            r.HasCore = hasCore;
            r.HasUpper = hasUpper;
            r.LowerEfficiency = hasLower ? Sanitize(lowerEfficiency, ref r.Fallback) : 1f;
            r.CoreEfficiency = hasCore ? Sanitize(coreEfficiency, ref r.Fallback) : 1f;
            r.UpperEfficiency = hasUpper ? Sanitize(upperEfficiency, ref r.Fallback) : 1f;

            r.LowerStrength = EfficiencyToStrength(r.LowerEfficiency, exponent);
            r.CoreStrength = EfficiencyToStrength(r.CoreEfficiency, exponent);
            r.UpperStrength = EfficiencyToStrength(r.UpperEfficiency, exponent);

            // --- Naive weighted support (debug reference only) ---
            float wSum = 0f, weighted = 0f;
            if (hasLower) { wSum += WeightLower; weighted += WeightLower * r.LowerStrength; }
            if (hasCore) { wSum += WeightCore; weighted += WeightCore * r.CoreStrength; }
            if (hasUpper) { wSum += WeightUpper; weighted += WeightUpper * r.UpperStrength; }

            if (wSum <= 0f)
            {
                // No recognisable region at all: neutral.
                r.WeightedSupport = 1f;
                r.StructuralSupport = 1f;
                r.UpperEffective = 1f;
                r.LoadSupport = 1f;
                r.BottleneckFactor = 1f;
                r.Fallback = true;
                return r;
            }
            r.WeightedSupport = weighted / wSum;

            // --- Structural chain: weighted harmonic mean of Lower and Core ---
            float chainW = 0f, chainInv = 0f;
            if (hasLower) { chainW += WeightLower; chainInv += WeightLower / Math.Max(r.LowerStrength, HarmonicEpsilon); }
            if (hasCore) { chainW += WeightCore; chainInv += WeightCore / Math.Max(r.CoreStrength, HarmonicEpsilon); }

            float final;
            if (chainW > 0f)
            {
                float structural = chainW / chainInv;
                // Anything below epsilon is structurally "gone"; do not let the epsilon leak a phantom contribution.
                if ((hasLower && r.LowerStrength <= HarmonicEpsilon) || (hasCore && r.CoreStrength <= HarmonicEpsilon))
                    structural = 0f;
                r.StructuralSupport = structural;

                float upperW = 0f, upperEff = 0f;
                if (hasUpper)
                {
                    upperW = WeightUpper;
                    float sum = r.UpperStrength + structural;
                    upperEff = sum > 0f ? 2f * r.UpperStrength * structural / sum : 0f;
                }
                r.UpperEffective = upperEff;
                final = (chainW * structural + upperW * upperEff) / (chainW + upperW);
            }
            else
            {
                // Only an upper region exists (very unusual anatomy).
                r.StructuralSupport = 1f;
                r.UpperEffective = r.UpperStrength;
                final = r.UpperStrength;
            }

            if (float.IsNaN(final)) { final = 1f; r.Fallback = true; }
            if (final < MinLoadSupport) final = MinLoadSupport;
            if (final > MaxLoadSupport) final = MaxLoadSupport;

            r.LoadSupport = final;
            r.BottleneckFactor = r.WeightedSupport > 0.0001f ? final / r.WeightedSupport : 1f;
            return r;
        }

        /// <summary>
        /// Negative → 0 (a region can legitimately be destroyed). NaN / ±∞ can only come from a malformed
        /// modded hediff, so that region is treated as neutral (1.0) instead of wildly punishing or rewarding the pawn.
        /// </summary>
        private static float Sanitize(float v, ref bool fallback)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) { fallback = true; return 1f; }
            if (v < 0f) return 0f;
            return v;
        }
    }

    /// <summary>Plain result record. Only primitives: nothing here can reference a Def or a mod.</summary>
    public struct LoadSupportResult
    {
        public bool HasLower, HasCore, HasUpper;
        public float LowerEfficiency, CoreEfficiency, UpperEfficiency;
        public float LowerStrength, CoreStrength, UpperStrength;
        public float WeightedSupport;
        public float StructuralSupport;
        /// <summary>Upper-body strength after coupling to the structural chain.</summary>
        public float UpperEffective;
        public float BottleneckFactor;
        public float LoadSupport;
        /// <summary>True if the value is a neutral fallback rather than a real measurement.</summary>
        public bool Fallback;

        public static LoadSupportResult Neutral
        {
            get
            {
                var r = new LoadSupportResult();
                r.LowerEfficiency = r.CoreEfficiency = r.UpperEfficiency = 1f;
                r.LowerStrength = r.CoreStrength = r.UpperStrength = 1f;
                r.WeightedSupport = r.StructuralSupport = r.UpperEffective = r.BottleneckFactor = r.LoadSupport = 1f;
                r.Fallback = true;
                return r;
            }
        }
    }
}
