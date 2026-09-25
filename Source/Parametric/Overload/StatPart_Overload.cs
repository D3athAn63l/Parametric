using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Parametric.Overload
{
    /// <summary>
    /// MoveSpeed × reactive overload factor. Multiplies whatever MoveSpeed was composed so far (armor, injuries, genes,
    /// other mods all stay); never replaces it. The factor is derived from the pawn's CURRENT gear + inventory mass
    /// against its comfortable capacity on every evaluation: vanilla Pawn.TicksPerMove reads MoveSpeed uncached
    /// (cacheStaleAfterTicks −1), so a picked-up or dropped stack changes movement at the next step, with no polling.
    /// At or below comfortable load the part contributes nothing and adds no explanation line.
    /// </summary>
    public class StatPart_Overload : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            if (!OverloadUtility.Active) return;
            Pawn pawn = req.Thing as Pawn;
            if (pawn == null) return;
            float f = OverloadUtility.CurrentFactor(pawn);
            if (f < 1f) val *= f;
        }

        public override string ExplanationPart(StatRequest req)
        {
            if (!OverloadUtility.Active) return null;
            Pawn pawn = req.Thing as Pawn;
            if (pawn == null) return null;
            float mass, comfortable;
            float f = OverloadUtility.CurrentFactor(pawn, out mass, out comfortable);
            if (!(f < 1f)) return null;
            return "Overload_MoveSpeedExplanation".Translate(
                OverloadFormula.LoadRatio(mass, comfortable).ToStringPercent(),
                f.ToStringByStyle(ToStringStyle.FloatTwo, ToStringNumberSense.Factor));
        }

        /// <summary>Idempotent; appends after existing parts, never reorders them (same contract as StatPart_LoadSupport).</summary>
        public static void InjectInto(StatDef stat)
        {
            if (stat == null)
            {
                Log.Error(OverloadLog.Prefix + "MoveSpeed StatDef not found; overload will not slow movement.");
                return;
            }
            if (stat.parts == null) stat.parts = new List<StatPart>();
            for (int i = 0; i < stat.parts.Count; i++)
                if (stat.parts[i] is StatPart_Overload) return;
            var part = new StatPart_Overload();
            part.parentStat = stat;
            part.priority = Parametric.LoadSupport.StatPart_LoadSupport.SortLastPriority;
            stat.parts.Add(part);
            if (stat.immutable)
            {
                stat.immutable = false;
                stat.Worker.SetCacheability(false);
            }
        }
    }
}
