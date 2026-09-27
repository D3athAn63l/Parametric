using System;
using RimWorld;
using Verse;

namespace Parametric.PassionProgression
{
    /// <summary>Native passion is the only persistent state. No polling or per-pawn cache.</summary>
    public static class PassionProgressionUtility
    {
        public static bool Active
        {
            get { return ParametricMod.Settings != null && ParametricMod.Settings.passionProgressionEnabled; }
        }

        /// <summary>Only known vanilla values may be promoted; unknown passion models are left alone.</summary>
        public static Passion PassionForMastery(int learnedLevel, Passion current)
        {
            if (current != Passion.None && current != Passion.Minor) return current;
            if (learnedLevel >= 20) return Passion.Major;
            if (learnedLevel >= 10 && current == Passion.None) return Passion.Minor;
            return current;
        }

        public static bool NormalizeSkill(SkillRecord skill)
        {
            if (!Active || skill == null) return false;
            // levelInt is earned skill, not the aptitude-adjusted display level. Cheap exits precede pawn queries.
            Passion target = PassionForMastery(skill.levelInt, skill.passion);
            if (target == skill.passion) return false;
            Pawn pawn = skill.Pawn;
            if (skill.def == null || pawn == null || pawn.def == null || pawn.RaceProps == null
                || pawn.skills == null || pawn.skills.skills == null || pawn.health == null || pawn.Dead) return false;
            try
            {
                if (skill.TotallyDisabled) return false;
                skill.passion = target;
                return true;
            }
            catch (Exception ex)
            {
                // An unusual pawn/skill or replacement disability implementation must not break learning or loading.
                Log.WarningOnce("[Parametric:PassionProgression] Could not normalize a skill: " + ex.Message, 0x50525001);
                return false;
            }
        }

        public static int NormalizePawn(Pawn pawn)
        {
            if (!Active || pawn == null || pawn.skills == null || pawn.skills.skills == null) return 0;
            var skills = pawn.skills.skills;
            int changed = 0;
            for (int i = 0; i < skills.Count; i++)
                if (NormalizeSkill(skills[i])) changed++;
            return changed;
        }

        /// <summary>One pass after game initialization or settings application; never called from a tick.</summary>
        public static void BackfillRelevantPawns()
        {
            if (!Active || Current.Game == null || Current.ProgramState != ProgramState.Playing) return;
            try
            {
                // Includes all factions, map-held pawns (e.g. cryptosleep), caravans and travelling transporters.
                // Dormant world pawns are handled when generated/redressed, spawned, or next learning.
                var pawns = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive;
                for (int i = 0; i < pawns.Count; i++) NormalizePawn(pawns[i]);
                // In-flight Odyssey gravships are not included in that vanilla aggregator.
                if (Find.CurrentGravship != null)
                    foreach (Pawn pawn in Find.CurrentGravship.Pawns) NormalizePawn(pawn);
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[Parametric:PassionProgression] Could not complete passion backfill: " + ex.Message, 0x50525002);
            }
        }
    }
}
