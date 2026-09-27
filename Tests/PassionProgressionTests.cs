// Part of the existing real-DLL integration harness. See IntegrationTests.cs for Unity stubs.
// SkillRecord.Learn, its passion/XP fields, disabled flags, and native Scribe serialization run for real.
// Generation/spawning/game-init bodies are stubbed (need Unity); their actual patched methods and postfixes run.
using System;
using System.Collections.Generic;
using HarmonyLib;
using Parametric;
using Parametric.PassionProgression;
using RimWorld;
using Verse;

static partial class IntegrationTests
{
    static SkillDef masteryDef;
    static Pawn generatedMaster;

    static SkillRecord MasterySkill(int level, Passion passion = Passion.None)
    {
        Pawn pawn = Human("Mastery" + nextId);
        pawn.skills = New<Pawn_SkillTracker>();
        AccessTools.Field(typeof(Pawn_SkillTracker), "pawn").SetValue(pawn.skills, pawn);
        var skill = new SkillRecord(pawn, masteryDef) { levelInt = level, passion = passion };
        pawn.skills.skills = new List<SkillRecord> { skill };
        // The harness has no live work/quest systems. Supply the results of vanilla disability calculation.
        AccessTools.Field(typeof(SkillRecord), "cachedTotallyDisabled").SetValue(skill, BoolUnknown.False);
        AccessTools.Field(typeof(SkillRecord), "cachedPermanentlyDisabled").SetValue(skill, BoolUnknown.False);
        AccessTools.Field(typeof(SkillRecord), "aptitudeCached").SetValue(skill, (int?)0);
        return skill;
    }

    static bool GeneratedMasterStub(ref Pawn __result) { __result = generatedMaster; return false; }

    static void RunPassionProgressionTests()
    {
        Console.WriteLine("\n=== Skill Passion Progression: real SkillRecord and native save data ===");
        var settings = ParametricMod.Settings;
        Check("passion progression defaults enabled", new ParametricSettings().passionProgressionEnabled);
        settings.passionProgressionEnabled = true;
        masteryDef = new SkillDef { defName = "ParametricTestMastery", label = "mastery" };
        DefDatabase<SkillDef>.Add(masteryDef);

        foreach (int level in new[] { 0, 9, 10, 15, 16, 19, 20 })
        foreach (Passion passion in new[] { Passion.None, Passion.Minor, Passion.Major, (Passion)3, (Passion)255 })
        {
            var skill = MasterySkill(level, passion);
            Passion expected = passion;
            if (passion == Passion.None && level >= 10) expected = Passion.Minor;
            if ((passion == Passion.None || passion == Passion.Minor) && level >= 20) expected = Passion.Major;
            int first = PassionProgressionUtility.NormalizePawn(skill.Pawn);
            int second = PassionProgressionUtility.NormalizePawn(skill.Pawn);
            Check("mastery " + level + "/" + passion + " -> " + expected + ", exactly once",
                skill.passion == expected && first == (expected == passion ? 0 : 1) && second == 0);
        }

        var minor = MasterySkill(9);
        minor.xpSinceLastLevel = minor.XpRequiredForLevelUp - 1f;
        minor.Learn(2f, direct: true, ignoreLearnRate: true);
        Check("real Learn: 9 -> 10 promotes Minor", minor.levelInt == 10 && minor.passion == Passion.Minor);
        Check("native direct learning rate reflects Minor", Math.Abs(minor.LearnRateFactor(true) - 1f) < 1e-6f);
        minor.Learn(-minor.xpSinceLastLevel - 1001f, direct: true);
        Check("real Learn decay: below 10 retains Minor", minor.levelInt == 9 && minor.passion == Passion.Minor);
        minor.levelInt = 1;
        Check("dramatic skill reduction never downgrades Minor", !PassionProgressionUtility.NormalizeSkill(minor) && minor.passion == Passion.Minor);

        foreach (Passion initial in new[] { Passion.None, Passion.Minor })
        {
            var major = MasterySkill(19, initial);
            major.xpSinceLastLevel = major.XpRequiredForLevelUp - 1f;
            major.Learn(2f, direct: true, ignoreLearnRate: true);
            Check("real Learn: 19/" + initial + " -> 20/Major", major.levelInt == 20 && major.passion == Passion.Major);
            major.Learn(-major.xpSinceLastLevel - 1001f, direct: true);
            Check("real Learn decay below 20 retains Major", major.levelInt == 19 && major.passion == Passion.Major);
            major.levelInt = 1;
            Check("Major survives dramatic reduction", !PassionProgressionUtility.NormalizeSkill(major) && major.passion == Passion.Major);
        }

        var saturated = MasterySkill(20);
        saturated.xpSinceMidnight = 5000f;
        PassionProgressionUtility.NormalizeSkill(saturated);
        Check("no learning-limit removal or bonus XP", saturated.LearningSaturatedToday && saturated.xpSinceMidnight == 5000f && saturated.xpSinceLastLevel == 0f);

        var aptitude = MasterySkill(9);
        AccessTools.Field(typeof(SkillRecord), "aptitudeCached").SetValue(aptitude, (int?)11);
        Check("aptitude-adjusted 20 is not earned mastery", aptitude.Level == 20 && !PassionProgressionUtility.NormalizeSkill(aptitude) && aptitude.passion == Passion.None);
        aptitude.levelInt = 20;
        AccessTools.Field(typeof(SkillRecord), "aptitudeCached").SetValue(aptitude, (int?)(-8));
        Check("negative aptitude does not erase earned mastery", aptitude.Level == 12 && PassionProgressionUtility.NormalizeSkill(aptitude) && aptitude.passion == Passion.Major);

        var disabled = MasterySkill(20);
        AccessTools.Field(typeof(SkillRecord), "cachedTotallyDisabled").SetValue(disabled, BoolUnknown.True);
        Check("totally disabled skill is a safe no-op", !PassionProgressionUtility.NormalizeSkill(disabled) && disabled.passion == Passion.None);
        AccessTools.Field(typeof(SkillRecord), "cachedTotallyDisabled").SetValue(disabled, BoolUnknown.False);
        AccessTools.Field(typeof(SkillRecord), "cachedPermanentlyDisabled").SetValue(disabled, BoolUnknown.True);
        Check("permanently disabled skill is a safe no-op", !PassionProgressionUtility.NormalizeSkill(disabled) && disabled.passion == Passion.None);
        Check("null pawn, missing tracker and orphan skill safely skipped",
            PassionProgressionUtility.NormalizePawn(null) == 0 && PassionProgressionUtility.NormalizePawn(Human("NoSkills")) == 0
            && !PassionProgressionUtility.NormalizeSkill(null) && !PassionProgressionUtility.NormalizeSkill(new SkillRecord { levelInt = 20, def = masteryDef }));
        var missingDef = MasterySkill(20); missingDef.def = null;
        Check("missing SkillDef safely skipped", !PassionProgressionUtility.NormalizeSkill(missingDef));
        var empty = MasterySkill(20); empty.Pawn.skills.skills = null;
        Check("missing skill list safely skipped", PassionProgressionUtility.NormalizePawn(empty.Pawn) == 0 && !PassionProgressionUtility.NormalizeSkill(empty));
        var nullEntry = MasterySkill(20); nullEntry.Pawn.skills.skills.Insert(0, null);
        Check("null skill entry does not abort the pawn", PassionProgressionUtility.NormalizePawn(nullEntry.Pawn) == 1);
        var nonHuman = MasterySkill(20); nonHuman.Pawn.def.race.intelligence = Intelligence.Animal;
        Check("normal skill tracker is supported without a race whitelist", PassionProgressionUtility.NormalizeSkill(nonHuman));

        settings.passionProgressionEnabled = false;
        var off = MasterySkill(19); off.xpSinceLastLevel = off.XpRequiredForLevelUp - 1f;
        off.Learn(2f, direct: true, ignoreLearnRate: true);
        Check("disabled setting: real learning and normalization do not upgrade", off.levelInt == 20 && off.passion == Passion.None
            && PassionProgressionUtility.NormalizePawn(off.Pawn) == 0);
        ParametricMod.Settings = null;
        Check("missing settings safely skipped", !PassionProgressionUtility.NormalizeSkill(off));
        ParametricMod.Settings = settings;
        settings.passionProgressionEnabled = true;

        var lifecycle = new Harmony("parametric.tests.passion.lifecycle");
        try
        {
            lifecycle.Patch(AccessTools.Method(typeof(PawnGenerator), "GeneratePawn", new[] { typeof(PawnGenerationRequest) }), Stub("GeneratedMasterStub"));
            lifecycle.Patch(AccessTools.Method(typeof(Pawn), "SpawnSetup"), Stub("Skip"));
            lifecycle.Patch(AccessTools.Method(typeof(Game), "FinalizeInit"), Stub("Skip"));
            // Stub only the leaf providers; the real vanilla map/caravan/transporter aggregator still runs.
            lifecycle.Patch(AccessTools.PropertyGetter(typeof(PawnsFinder), "AllMaps"), Stub("MapsWorldPopulation"));
            lifecycle.Patch(AccessTools.PropertyGetter(typeof(PawnsFinder), "AllCaravansAndTravellingTransporters_AliveOrDead"), Stub("CaravanPopulation"));

            var born = MasterySkill(20); generatedMaster = born.Pawn;
            Pawn result = PawnGenerator.GeneratePawn(default(PawnGenerationRequest));
            Check("patched generation normalizes high skills after generation", result == born.Pawn && born.passion == Passion.Major);
            generatedMaster = null;
            Check("failed generation result safely skipped", PawnGenerator.GeneratePawn(default(PawnGenerationRequest)) == null);
            var arriving = MasterySkill(15);
            arriving.Pawn.SpawnSetup(null, false);
            Check("patched spawn normalizes a returning world pawn", arriving.passion == Passion.Minor);
            var loading = MasterySkill(20);
            loading.Pawn.SpawnSetup(null, true);
            Check("load respawn defers until initialization completes", loading.passion == Passion.None);

            var map = MasterySkill(15); var caravan = MasterySkill(20);
            var alreadyMinor = MasterySkill(20, Passion.Minor); var alreadyMajor = MasterySkill(20, Passion.Major);
            var dormant = MasterySkill(20);
            StubMapsWorld.Clear(); StubMapsWorld.Add(map.Pawn); StubMapsWorld.Add(loading.Pawn); StubMapsWorld.Add(alreadyMajor.Pawn);
            StubCaravans.Clear(); StubCaravans.Add(caravan.Pawn); StubCaravans.Add(alreadyMinor.Pawn);
            ProgramState previousState = Current.ProgramState;
            Current.ProgramState = ProgramState.Playing;
            try
            {
                Current.Game.FinalizeInit();
                Check("patched game init backfills map, caravan, transporter populations", map.passion == Passion.Minor && loading.passion == Passion.Major
                    && caravan.passion == Passion.Major && alreadyMinor.passion == Passion.Major && alreadyMajor.passion == Passion.Major);
                Check("backfill leaves dormant world population alone", dormant.passion == Passion.None);
                Current.Game.FinalizeInit();
                Check("repeated initialization is idempotent", PassionProgressionUtility.NormalizePawn(map.Pawn) == 0 && PassionProgressionUtility.NormalizePawn(caravan.Pawn) == 0);
                settings.passionProgressionEnabled = false;
                map.passion = Passion.None;
                Current.Game.FinalizeInit();
                map.Pawn.SpawnSetup(null, false);
                generatedMaster = map.Pawn;
                PawnGenerator.GeneratePawn(default(PawnGenerationRequest));
                Check("disabled setting gates every lifecycle hook", map.passion == Passion.None);
                settings.passionProgressionEnabled = true;
                PassionProgressionUtility.BackfillRelevantPawns(); // same entry point as WriteSettings
                Check("enabling settings can backfill immediately", map.passion == Passion.Minor);
            }
            finally { Current.ProgramState = previousState; }
        }
        finally { lifecycle.UnpatchAll(lifecycle.Id); generatedMaster = null; StubMapsWorld.Clear(); StubCaravans.Clear(); }

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "parametric_passion_test.xml");
        var saved = MasterySkill(20); PassionProgressionUtility.NormalizeSkill(saved); saved.levelInt = 8;
        Scribe.saver.InitSaving(path, "skill"); saved.ExposeData(); Scribe.saver.FinalizeSaving();
        var restored = MasterySkill(0);
        Scribe.loader.InitLoading(path); restored.ExposeData(); Scribe.loader.FinalizeLoading();
        Check("native SkillRecord save round-trip retains Major below milestone", restored.levelInt == 8 && restored.passion == Passion.Major);
        settings.passionProgressionEnabled = false;
        Check("disabling feature does not undo saved passion", !PassionProgressionUtility.NormalizeSkill(restored) && restored.passion == Passion.Major);
        Scribe.saver.InitSaving(path, "settings"); settings.ExposeData(); Scribe.saver.FinalizeSaving();
        var restoredSettings = new ParametricSettings();
        Scribe.loader.InitLoading(path); restoredSettings.ExposeData(); Scribe.loader.FinalizeLoading();
        Check("settings toggle persists through native Scribe", !restoredSettings.passionProgressionEnabled);
        restoredSettings.ResetToDefaults();
        Check("reset defaults restores feature enabled", restoredSettings.passionProgressionEnabled);
        // An old settings file lacking the key should use the enabled default.
        System.IO.File.WriteAllText(path, "<settings />");
        restoredSettings.passionProgressionEnabled = false;
        Scribe.loader.InitLoading(path); restoredSettings.ExposeData(); Scribe.loader.FinalizeLoading();
        Check("pre-feature settings migrate to enabled", restoredSettings.passionProgressionEnabled);
        settings.passionProgressionEnabled = true;
        System.IO.File.Delete(path);
    }
}
