using HarmonyLib;
using RimWorld;
using Verse;

namespace Parametric.PassionProgression
{
    // Learn writes levelInt directly, bypassing the Level setter. Do not alter XP, saturation or decay.
    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.Learn), new[] { typeof(float), typeof(bool), typeof(bool) })]
    public static class Patch_SkillRecord_Learn
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(SkillRecord __instance, float xp)
        {
            // Negative XP is decay, not mastery. NormalizeSkill immediately exits for low/already-qualified skills.
            if (xp > 0f) PassionProgressionUtility.NormalizeSkill(__instance);
        }
    }

    // The common request overload completes both new generation and redressing existing world pawns.
    // It runs after vanilla assigns passions, avoiding mid-generation changes to the passion budget.
    [HarmonyPatch(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new[] { typeof(PawnGenerationRequest) })]
    public static class Patch_PawnGenerator_GeneratePawn
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Pawn __result)
        {
            PassionProgressionUtility.NormalizePawn(__result);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup), new[] { typeof(Map), typeof(bool) })]
    public static class Patch_Pawn_SpawnSetup
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Pawn __instance, bool respawningAfterLoad)
        {
            // Save loading is normalized once at Game.FinalizeInit, after references and trackers are ready.
            if (!respawningAfterLoad) PassionProgressionUtility.NormalizePawn(__instance);
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    public static class Patch_Game_FinalizeInit
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix()
        {
            PassionProgressionUtility.BackfillRelevantPawns();
        }
    }
}
