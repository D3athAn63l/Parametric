using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Parametric.Overload
{
    /// <summary>
    /// MoveSpeed × reactive overload factor. Multiplies whatever MoveSpeed was composed so far (armor, injuries, genes,
    /// other mods all stay); never replaces it. The factor is min(mass channel, hand channel), each derived from the
    /// pawn's CURRENT load against that channel's comfortable capacity on every evaluation: vanilla Pawn.TicksPerMove reads MoveSpeed uncached
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

        /// <summary>Explains the channel that sets the factor (the worse of mass and hand carry).</summary>
        public override string ExplanationPart(StatRequest req)
        {
            if (!OverloadUtility.Active) return null;
            Pawn pawn = req.Thing as Pawn;
            if (pawn == null) return null;
            OverloadUtility.State st = OverloadUtility.Evaluate(pawn);
            if (!(st.Factor < 1f)) return null;
            bool hand = st.Limiting == OverloadUtility.Channel.Hand;
            float ratio = hand ? OverloadFormula.LoadRatio(st.HandLoad, st.ComfortableHand) : OverloadFormula.LoadRatio(st.Mass, st.ComfortableMass);
            return (hand ? "Overload_MoveSpeedExplanationHand" : "Overload_MoveSpeedExplanationMass").Translate(
                ratio.ToStringPercent(), st.Factor.ToStringByStyle(ToStringStyle.FloatTwo, ToStringNumberSense.Factor));
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

    /// <summary>
    /// CarryingCapacity × (2 − policy): Overload's hand-carry channel in vanilla's own hand-carry system.
    ///
    /// Appended after StatPart_LoadSupport (injected first at startup), so the value it receives is the comfortable hand
    /// capacity — base, offsets, factors, vanilla Manipulation (with Parametric's compensation), body size, other
    /// mods' earlier parts, Load Support — and it multiplies the routine multiplier in exactly once. Everything that
    /// reads CarryingCapacity (JobGiver_Steal's count, Pawn_CarryTracker.MaxStackSpaceEver, haul jobs, other mods)
    /// sees the routine hand capacity with no patch of its own.
    ///
    /// Skipped for: Overload off, policy 100%, trade pawns (PolicyFor = 1), the comfortable-hand query for that pawn
    /// (OverloadUtility.ComfortableHandQueryPawn), and Parametric's own "without Parametric" debug measurement.
    /// </summary>
    public class StatPart_OverloadHandCarry : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            float m = Multiplier(req);
            if (m != 1f) val *= m;
        }

        public override string ExplanationPart(StatRequest req)
        {
            float m = Multiplier(req);
            if (m == 1f) return null;
            Pawn pawn = (Pawn)req.Thing;
            return "Overload_HandCapacityExplanation".Translate(
                OverloadFormula.PolicyLabel(OverloadUtility.PolicyFor(pawn)),
                m.ToStringByStyle(ToStringStyle.FloatTwo, ToStringNumberSense.Factor));
        }

        private static float Multiplier(StatRequest req)
        {
            if (!OverloadUtility.Active || Parametric.LoadSupport.StatPart_LoadSupport.Bypass) return 1f;
            Pawn pawn = req.Thing as Pawn;
            if (pawn == null || pawn == OverloadUtility.ComfortableHandQueryPawn) return 1f;
            float m = OverloadFormula.RoutineMultiplier(OverloadUtility.PolicyFor(pawn));
            return m > 1f ? m : 1f;
        }

        /// <summary>Idempotent; appends after existing parts (so after StatPart_LoadSupport), never reorders them.</summary>
        public static void InjectInto(StatDef stat)
        {
            if (stat == null)
            {
                Log.Error(OverloadLog.Prefix + "CarryingCapacity StatDef not found; Overload will not extend hand carrying.");
                return;
            }
            if (stat.parts == null) stat.parts = new List<StatPart>();
            for (int i = 0; i < stat.parts.Count; i++)
                if (stat.parts[i] is StatPart_OverloadHandCarry) return;
            var part = new StatPart_OverloadHandCarry();
            part.parentStat = stat;
            part.priority = Parametric.LoadSupport.StatPart_LoadSupport.SortLastPriority + 1f;
            stat.parts.Add(part);
            if (stat.immutable)
            {
                stat.immutable = false;
                stat.Worker.SetCacheability(false);
            }
        }
    }
}
