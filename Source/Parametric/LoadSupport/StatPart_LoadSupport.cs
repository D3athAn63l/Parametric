using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Parametric.LoadSupport
{
    /// <summary>
    /// Turns the pawn's CarryingCapacity into
    ///     (CarryingCapacity as composed so far) × ManipulationCompensation × LoadSupport
    ///
    /// RimWorld 1.6 pipeline (StatWorker):
    ///   GetValueUnfinalized: base → offsets (genes, traits, hediffs, …) → statFactors → capacityFactors (Manipulation)
    ///   FinalizeValue:       stat.parts in LIST ORDER → postProcessCurve → postProcessStatFactors → scenario → round → clamp
    /// Pawn_CarryTracker.MaxStackSpaceEver reads the result as GetStatValue(CarryingCapacity) / VolumePerUnit.
    ///
    /// Every vanilla/modded influence is kept (compatibility through composition) EXCEPT the manipulation-limb share of
    /// vanilla's Manipulation capacity factor, which Load Support measures itself (see ManipulationCompensation).
    ///
    /// ORDERING: StatPart.priority is only used by StatDef.PostLoad, which sorts the XML-defined parts once when defs
    /// load. FinalizeValue then iterates stat.parts in list order. This part is appended at startup (after PostLoad),
    /// so it runs after every part that existed at that point. A mod that appends its own part later would run after
    /// this one; we deliberately do not reorder other mods' parts. The high priority value only matters if something
    /// re-sorts the list.
    ///
    /// Added from code (not XML) so it works whether or not the StatDef has a &lt;parts&gt; list, and so the only
    /// thing referencing it is the in-memory Def, never a save file.
    /// </summary>
    public class StatPart_LoadSupport : StatPart
    {
        /// <summary>Only relevant if something re-sorts parts by priority (vanilla sorts once, at PostLoad, before we are added).</summary>
        public const float SortLastPriority = 100000f;

        /// <summary>Set while debug code asks for the "without Load Support" value. Main-thread only.</summary>
        [ThreadStatic] public static bool Bypass;

        public override void TransformValue(StatRequest req, ref float val)
        {
            if (Bypass) return;
            Pawn pawn = req.Thing as Pawn;
            if (pawn == null) return;
            ParametricSettings s = ParametricMod.Settings;
            if (s == null || !s.LoadSupportAppliesTo(pawn)) return;

            LoadSupportResult r = LoadSupportCache.GetResult(pawn);
            float factor = ManipulationCompensation.Compute(parentStat, pawn, r) * r.LoadSupport;
            if (factor > 0f && !float.IsNaN(factor) && !float.IsInfinity(factor))
                val *= factor;
        }

        public override string ExplanationPart(StatRequest req)
        {
            if (Bypass) return null;
            Pawn pawn = req.Thing as Pawn;
            if (pawn == null) return null;
            ParametricSettings s = ParametricMod.Settings;
            if (s == null || !s.LoadSupportAppliesTo(pawn)) return null;

            LoadSupportResult r = LoadSupportCache.GetResult(pawn);
            ManipulationCompensation.Info comp;
            ManipulationCompensation.Compute(parentStat, pawn, r, out comp);

            string text = "";
            if (comp.Applied && Math.Abs(comp.Multiplier - 1f) > 0.0005f)
                text += "LoadSupport_ManipulationCompensation".Translate(
                            comp.Multiplier.ToStringByStyle(ToStringStyle.FloatTwo, ToStringNumberSense.Factor),
                            comp.LimbEfficiency.ToStringPercent()) + "\n";
            text += "LoadSupport_StatExplanation".Translate(
                        r.LoadSupport.ToStringByStyle(ToStringStyle.FloatTwo, ToStringNumberSense.Factor))
                    + "\n" + "LoadSupport_StatExplanationDetail".Translate(
                        Fmt(r.HasLower, r.LowerEfficiency), Fmt(r.HasCore, r.CoreEfficiency), Fmt(r.HasUpper, r.UpperEfficiency));
            return text;
        }

        private static string Fmt(bool present, float efficiency)
        {
            return present ? efficiency.ToStringPercent() : "-";
        }

        /// <summary>Idempotent. Appends the part to the end of the stat's parts list; never reorders existing parts.</summary>
        public static void InjectInto(StatDef stat)
        {
            if (stat == null)
            {
                Log.Error(LoadSupportLog.Prefix + "CarryingCapacity StatDef not found; carrying capacity will not be modified.");
                return;
            }

            if (stat.parts == null) stat.parts = new List<StatPart>();
            for (int i = 0; i < stat.parts.Count; i++)
                if (stat.parts[i] is StatPart_LoadSupport) return;

            var part = new StatPart_LoadSupport();
            part.parentStat = stat;
            part.priority = SortLastPriority;
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
