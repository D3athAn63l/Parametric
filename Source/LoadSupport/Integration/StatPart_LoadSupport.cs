using System;
using RimWorld;
using Verse;

namespace LoadSupport
{
    /// <summary>
    /// Multiplies the pawn's CarryingCapacity stat by its LoadSupport.
    ///
    /// Why a StatPart and not a Harmony patch:
    ///   RimWorld 1.6 computes CarryingCapacity through the normal StatWorker pipeline
    ///   (base → offsets → factors (genes, capacities, body size…) → StatParts → postProcess → clamp).
    ///   Pawn_CarryTracker.MaxStackSpaceEver reads it with GetStatValue(CarryingCapacity) / VolumePerUnit.
    ///   Appending ourselves as the LAST StatPart means we multiply whatever every other source already produced
    ///   (compatibility through composition), the multiplier shows up in the stat's info-card explanation for free,
    ///   and no vanilla method needs patching.
    ///
    /// Added from code at startup (not via XML) so it works whether or not the StatDef already has a &lt;parts&gt; list,
    /// and so the only thing referencing it is the in-memory Def — never a save file.
    /// </summary>
    public class StatPart_LoadSupport : StatPart
    {
        /// <summary>Very high so that if anything re-sorts parts by priority we still run last.</summary>
        public const float RunLastPriority = 100000f;

        /// <summary>Set while debug code asks for the "without LoadSupport" value. Main-thread only.</summary>
        [ThreadStatic] public static bool Bypass;

        public override void TransformValue(StatRequest req, ref float val)
        {
            if (Bypass) return;
            Pawn pawn = req.Thing as Pawn;
            if (pawn == null) return;
            LoadSupportSettings s = LoadSupportMod.Settings;
            if (s == null || !s.AppliesTo(pawn)) return;

            float factor = LoadSupportCache.Get(pawn);
            if (factor > 0f && !float.IsNaN(factor) && !float.IsInfinity(factor))
                val *= factor;
        }

        public override string ExplanationPart(StatRequest req)
        {
            if (Bypass) return null;
            Pawn pawn = req.Thing as Pawn;
            if (pawn == null) return null;
            LoadSupportSettings s = LoadSupportMod.Settings;
            if (s == null || !s.AppliesTo(pawn)) return null;

            LoadSupportResult r = LoadSupportCache.GetResult(pawn);
            return "LoadSupport_StatExplanation".Translate(
                       r.LoadSupport.ToStringByStyle(ToStringStyle.FloatTwo, ToStringNumberSense.Factor))
                   + "\n" + "LoadSupport_StatExplanationDetail".Translate(
                       Fmt(r.HasLower, r.LowerEfficiency), Fmt(r.HasCore, r.CoreEfficiency), Fmt(r.HasUpper, r.UpperEfficiency));
        }

        private static string Fmt(bool present, float efficiency)
        {
            return present ? efficiency.ToStringPercent() : "-";
        }

        /// <summary>Idempotent. Appends the part to the end of the stat's parts list.</summary>
        public static void InjectInto(StatDef stat)
        {
            if (stat == null)
            {
                Log.Error("[LoadSupport] CarryingCapacity StatDef not found; carrying capacity will not be modified.");
                return;
            }

            if (stat.parts == null) stat.parts = new System.Collections.Generic.List<StatPart>();
            for (int i = 0; i < stat.parts.Count; i++)
                if (stat.parts[i] is StatPart_LoadSupport) return;

            var part = new StatPart_LoadSupport();
            part.parentStat = stat;
            part.priority = RunLastPriority;
            stat.parts.Add(part);

            // A stat with no parts/factors may have been flagged immutable at startup, which would make the
            // StatWorker cache the first value per Thing forever. Adding a part makes it mutable; enforce that.
            if (stat.immutable)
            {
                stat.immutable = false;
                stat.Worker.SetCacheability(false); // drops the immutable cache, keeps the temporary one if cacheable
            }
        }
    }
}
