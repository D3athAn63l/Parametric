using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace LoadSupport
{
    /// <summary>
    /// Reads a pawn's CURRENT body and hediffs and reduces them to three region efficiencies.
    ///
    /// Everything here goes through RimWorld's own generic body APIs — the same ones the vanilla
    /// Moving / Manipulation capacity workers use — so prosthetics, bionics, archotech parts and modded
    /// replacement parts are understood automatically, as long as they use standard mechanisms:
    ///   • Hediff_AddedPart + addedPartProps.partEfficiency   (all vanilla/most modded prosthetics)
    ///   • HediffStage.partEfficiencyOffset                  (part-level buffs/debuffs)
    ///   • missing parts / missing parents / part HP         (injuries, amputations)
    ///   • HediffStage capMods on Moving / Manipulation       (non-part body modifiers, dampened)
    ///
    /// Region mapping uses BodyPartTagDefs, never DefNames:
    ///   Lower  = MovingLimbCore / MovingLimbSegment / MovingLimbDigit   (legs, feet, toes or equivalents)
    ///   Upper  = ManipulationLimbCore / ...Segment / ...Digit            (shoulders, arms, hands or equivalents)
    ///   Core   = parts tagged Spine, parts tagged Pelvis, and the body's root (corePart, e.g. torso)
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
        /// How strongly whole-pawn capacity modifiers (capMods) feed into region efficiency.
        /// 0.5 = square root: a +100% Moving capMod gives ×1.41 lower-body efficiency, not ×2.
        /// Movement is related to, but not the same as, load-bearing (design requirement).
        /// </summary>
        private const float CapModInfluence = 0.5f;

        public struct Reading
        {
            public bool HasLower, HasCore, HasUpper;
            public float LowerPartEfficiency, CorePartEfficiency, UpperPartEfficiency;
            public float LowerCapModFactor, UpperCapModFactor;
            public float LowerEfficiency, CoreEfficiency, UpperEfficiency;
        }

        public static Reading Read(Pawn pawn)
        {
            var r = new Reading();
            r.LowerCapModFactor = r.UpperCapModFactor = 1f;

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

            // ---------- Upper body ----------
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

            // ---------- Whole-pawn capacity modifiers (hediff capMods) ----------
            float lowerMax, upperMax;
            ReadCapMods(pawn, set, out r.LowerCapModFactor, out lowerMax, out r.UpperCapModFactor, out upperMax);

            r.LowerEfficiency = Math.Min(r.LowerPartEfficiency * r.LowerCapModFactor, lowerMax);
            r.UpperEfficiency = Math.Min(r.UpperPartEfficiency * r.UpperCapModFactor, upperMax);
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
        /// Aggregates hediff capMods for Moving (→ lower) and Manipulation (→ upper) the same way vanilla does
        /// (sum offsets, multiply postFactors, min of setMax) and returns a dampened multiplier.
        /// </summary>
        private static void ReadCapMods(Pawn pawn, HediffSet set,
            out float lowerFactor, out float lowerMax, out float upperFactor, out float upperMax)
        {
            float lowOff = 0f, lowPost = 1f, upOff = 0f, upPost = 1f;
            lowerMax = upperMax = float.MaxValue;

            PawnCapacityDef moving = PawnCapacityDefOf.Moving;
            PawnCapacityDef manip = PawnCapacityDefOf.Manipulation;
            List<Hediff> hediffs = set.hediffs;

            for (int i = 0; i < hediffs.Count; i++)
            {
                Hediff h = hediffs[i];
                if (h == null) continue;
                List<PawnCapacityModifier> mods;
                try { mods = h.CapMods; }
                catch { continue; } // malformed modded hediff: ignore it rather than break the pawn
                if (mods == null) continue;

                for (int j = 0; j < mods.Count; j++)
                {
                    PawnCapacityModifier m = mods[j];
                    if (m == null) continue;
                    if (m.capacity == moving)
                    {
                        lowOff += m.offset;
                        lowPost *= m.postFactor;
                        if (m.SetMaxDefined) lowerMax = Math.Min(lowerMax, m.EvaluateSetMax(pawn));
                    }
                    else if (m.capacity == manip)
                    {
                        upOff += m.offset;
                        upPost *= m.postFactor;
                        if (m.SetMaxDefined) upperMax = Math.Min(upperMax, m.EvaluateSetMax(pawn));
                    }
                }
            }

            lowerFactor = Dampen((1f + lowOff) * lowPost);
            upperFactor = Dampen((1f + upOff) * upPost);
            if (float.IsNaN(lowerMax) || lowerMax < 0f) lowerMax = 0f;
            if (float.IsNaN(upperMax) || upperMax < 0f) upperMax = 0f;
        }

        private static float Dampen(float rawMultiplier)
        {
            if (!(rawMultiplier > 0f)) return 0f;
            if (Math.Abs(rawMultiplier - 1f) < 0.0001f) return 1f;
            return (float)Math.Pow(rawMultiplier, CapModInfluence);
        }
    }
}
