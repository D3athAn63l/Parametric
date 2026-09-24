using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Parametric.LoadSupport
{
    /// <summary>
    /// Reads a pawn's CURRENT body and hediffs and reduces them to three region efficiencies.
    ///
    /// Everything here goes through RimWorld's own generic body APIs (the same ones the vanilla
    /// Moving / Manipulation capacity workers use), so prosthetics, bionics, archotech parts and modded
    /// replacement parts are understood automatically when they use standard mechanisms:
    ///   • Hediff_AddedPart + addedPartProps.partEfficiency   (all vanilla/most modded prosthetics)
    ///   • HediffStage.partEfficiencyOffset                  (part-level buffs/debuffs)
    ///   • missing parts / missing parents / part HP         (injuries, amputations)
    ///   • capMods on Moving from hediffs AND active genes   (non-part lower-body modifiers, dampened)
    ///
    /// Region mapping uses BodyPartTagDefs, never DefNames:
    ///   Lower  = MovingLimbCore / MovingLimbSegment / MovingLimbDigit   (legs, feet, toes or equivalents)
    ///   Upper  = ManipulationLimbCore / ...Segment / ...Digit            (shoulders, arms, hands or equivalents)
    ///   Core   = parts tagged Spine, parts tagged Pelvis, and the body's root (corePart, e.g. torso)
    ///
    /// The UPPER region is deliberately limb-only (no capMods). Everything else that feeds vanilla's Manipulation
    /// capacity (consciousness, capMods, genes, custom workers) is kept by vanilla's own Manipulation factor on
    /// CarryingCapacity — see <see cref="ManipulationCompensation"/>. Reading it here too would count it twice.
    /// </summary>
    public static class BodyRegionAnalyzer
    {
        // Same appendage weights the vanilla capacity workers use for digits (toes / fingers).
        private const float LowerAppendageWeight = 0.4f;
        private const float UpperAppendageWeight = 0.8f;

        // Core sub-weights: spine and pelvis are the actual load path; the root part stands in for
        // general torso integrity. Renormalised over whichever groups exist in this anatomy.
        private const float CoreWeightSpine = 0.4f;
        private const float CoreWeightPelvis = 0.4f;
        private const float CoreWeightRoot = 0.2f;

        /// <summary>
        /// How strongly Moving capMods feed into lower-body efficiency.
        /// 0.5 = square root: a +100% Moving capMod gives ×1.41 lower-body efficiency, not ×2.
        /// Movement is related to, but not the same as, load-bearing.
        /// </summary>
        private const float CapModInfluence = 0.5f;

        public struct Reading
        {
            public bool HasLower, HasCore, HasUpper;
            public float LowerPartEfficiency, CorePartEfficiency, UpperPartEfficiency;
            public float LowerCapModFactor;
            public float LowerEfficiency, CoreEfficiency, UpperEfficiency;
        }

        public static Reading Read(Pawn pawn)
        {
            var r = new Reading();
            r.LowerCapModFactor = 1f;

            BodyDef body = pawn.RaceProps != null ? pawn.RaceProps.body : null;
            HediffSet set = pawn.health != null ? pawn.health.hediffSet : null;
            if (body == null || set == null)
            {
                r.LowerEfficiency = r.CoreEfficiency = r.UpperEfficiency = 1f;
                return r;
            }

            // ---------- Lower body ----------
            if (HasAny(body, BodyPartTagDefOf.MovingLimbCore))
            {
                r.HasLower = true;
                float functional;
                r.LowerPartEfficiency = PawnCapacityUtility.CalculateLimbEfficiency(
                    set, BodyPartTagDefOf.MovingLimbCore, BodyPartTagDefOf.MovingLimbSegment,
                    BodyPartTagDefOf.MovingLimbDigit, LowerAppendageWeight, out functional, null);
            }

            // ---------- Upper body (limb-only, exactly the limb term of vanilla's Manipulation worker) ----------
            if (HasAny(body, BodyPartTagDefOf.ManipulationLimbCore))
            {
                r.HasUpper = true;
                float functional;
                r.UpperPartEfficiency = PawnCapacityUtility.CalculateLimbEfficiency(
                    set, BodyPartTagDefOf.ManipulationLimbCore, BodyPartTagDefOf.ManipulationLimbSegment,
                    BodyPartTagDefOf.ManipulationLimbDigit, UpperAppendageWeight, out functional, null);
            }

            // ---------- Core ----------
            float coreSum = 0f, coreW = 0f;
            float avg;
            if (TryAverageTagEfficiency(set, body, BodyPartTagDefOf.Spine, out avg))
            {
                coreSum += CoreWeightSpine * avg; coreW += CoreWeightSpine;
            }
            if (TryAverageTagEfficiency(set, body, BodyPartTagDefOf.Pelvis, out avg))
            {
                coreSum += CoreWeightPelvis * avg; coreW += CoreWeightPelvis;
            }
            if (body.corePart != null)
            {
                coreSum += CoreWeightRoot * PawnCapacityUtility.CalculatePartEfficiency(set, body.corePart, false, null);
                coreW += CoreWeightRoot;
            }
            if (coreW > 0f)
            {
                r.HasCore = true;
                r.CorePartEfficiency = coreSum / coreW;
            }

            // ---------- Moving capMods (hediffs + active genes), dampened ----------
            float lowerMax;
            ReadMovingCapMods(pawn, set, out r.LowerCapModFactor, out lowerMax);

            r.LowerEfficiency = Math.Min(r.LowerPartEfficiency * r.LowerCapModFactor, lowerMax);
            r.UpperEfficiency = r.UpperPartEfficiency;
            r.CoreEfficiency = r.CorePartEfficiency;
            return r;
        }

        private static bool HasAny(BodyDef body, BodyPartTagDef tag)
        {
            if (tag == null) return false;
            List<BodyPartRecord> parts = body.GetPartsWithTag(tag); // cached per BodyDef by vanilla
            return parts != null && parts.Count > 0;
        }

        /// <summary>Average CalculatePartEfficiency over all parts carrying a tag. False if the anatomy has none.</summary>
        private static bool TryAverageTagEfficiency(HediffSet set, BodyDef body, BodyPartTagDef tag, out float average)
        {
            average = 1f;
            if (tag == null) return false;
            List<BodyPartRecord> parts = body.GetPartsWithTag(tag);
            if (parts == null || parts.Count == 0) return false;
            float sum = 0f;
            for (int i = 0; i < parts.Count; i++)
                sum += PawnCapacityUtility.CalculatePartEfficiency(set, parts[i], false, null);
            average = sum / parts.Count;
            return true;
        }

        /// <summary>
        /// Aggregates capMods on Moving the same way vanilla PawnCapacityUtility.CalculateCapacityLevel does:
        /// hediffs (offset sum, postFactor product scaled by capacityFactorEffectMultiplier, min of defined setMax), then
        /// active Biotech genes (offset, postFactor, setMax). Returns a dampened multiplier and the setMax ceiling.
        /// </summary>
        private static void ReadMovingCapMods(Pawn pawn, HediffSet set, out float factor, out float max)
        {
            float off = 0f, post = 1f;
            max = float.MaxValue;
            PawnCapacityDef moving = PawnCapacityDefOf.Moving;
            if (moving == null) { factor = 1f; return; }

            List<Hediff> hediffs = set.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff h = hediffs[i];
                if (h == null) continue;
                List<PawnCapacityModifier> mods;
                HediffStage stage;
                try { mods = h.CapMods; stage = h.CurStage; }
                catch { continue; } // malformed modded hediff: ignore it rather than break the pawn
                if (mods == null) continue;

                for (int j = 0; j < mods.Count; j++)
                {
                    PawnCapacityModifier m = mods[j];
                    if (m == null || m.capacity != moving) continue;
                    off += m.offset;
                    float pf = m.postFactor;
                    if (stage != null && stage.capacityFactorEffectMultiplier != null)
                        pf = StatWorker.ScaleFactor(pf, pawn.GetStatValue(stage.capacityFactorEffectMultiplier, true, -1));
                    post *= pf;
                    if (m.SetMaxDefined) max = Math.Min(max, m.EvaluateSetMax(pawn)); // undefined setMax = 999 in vanilla: not a real ceiling
                }
            }

            if (pawn.genes != null && ModsConfig.BiotechActive)
            {
                List<Gene> genes = pawn.genes.GenesListForReading;
                for (int i = 0; i < genes.Count; i++)
                {
                    Gene g = genes[i];
                    if (g == null || !g.Active || g.def == null || g.def.capMods == null) continue;
                    List<PawnCapacityModifier> mods = g.def.capMods;
                    for (int j = 0; j < mods.Count; j++)
                    {
                        PawnCapacityModifier m = mods[j];
                        if (m == null || m.capacity != moving) continue;
                        off += m.offset;
                        post *= m.postFactor;
                        if (m.SetMaxDefined) max = Math.Min(max, m.EvaluateSetMax(pawn)); // undefined setMax = 999 in vanilla: not a real ceiling
                    }
                }
            }

            factor = Dampen((1f + off) * post);
            if (float.IsNaN(max) || max < 0f) max = 0f;
        }

        private static float Dampen(float rawMultiplier)
        {
            if (!(rawMultiplier > 0f)) return 0f;
            if (Math.Abs(rawMultiplier - 1f) < 0.0001f) return 1f;
            return (float)Math.Pow(rawMultiplier, CapModInfluence);
        }
    }
}
