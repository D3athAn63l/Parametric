// Integration harness: runs the REAL Parametric.dll against the REAL RimWorld 1.6 Assembly-CSharp and Harmony 2.4.1
// under Mono, outside Unity.
//
// What is real:  every RimWorld method on the tested paths — PawnCapacityUtility limb/part efficiency, the vanilla
//                Manipulation capacity worker, PawnCapacityUtility.CalculateCapacityLevel (capMods), the
//                PawnCapacitiesHandler cache, the COMPLETE StatWorker pipeline (GetValueUnfinalized: base, offsets,
//                statFactors, capacityFactors via PawnCapacityFactor.GetFactor + Lerp(weight); FinalizeValue: parts in
//                list order incl. vanilla StatPart_BodySize, clamp), MassUtility.Capacity, HediffSet.DirtyCache,
//                and the mod's real Harmony patches.
// What is synthetic: body layouts (hand-built approximations of the vanilla Human / quadruped BodyDefs — the Core
//                XML is not loaded), the CarryingCapacity StatDef is built in code with the vanilla configuration
//                (base 75, Manipulation capacity factor weight 1, StatPart_BodySize), vanilla part efficiencies are
//                typed in, and a handful of Unity-only calls are stubbed (logging, Find.Scenario, Prefs.DevMode,
//                ModsConfig, Consciousness worker → controllable value, meditation cache).
//
// Build/run: see Tests/run-tests.sh
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Parametric;
using Parametric.Debug;
using Parametric.LoadSupport;
using RimWorld;
using Verse;
using OC = System.Reflection.Emit.OpCodes;

// Another mod's CarryingCapacity StatPart: val = val × factor + add.
class TestStatPart : StatPart
{
    public float factor = 1f, add = 0f;
    public override void TransformValue(StatRequest req, ref float val) { val = val * factor + add; }
    public override string ExplanationPart(StatRequest req) { return null; }
}

static class IntegrationTests
{
    static int failures, checks;
    static readonly List<string> logLines = new List<string>();
    static float TestConsciousness = 1f;

    // ---------------- Unity-free stubs ----------------
    static bool LogPrefix(object __0) { logLines.Add(Convert.ToString(__0)); return false; }
    static bool FalseGetter(ref bool __result) { __result = false; return false; }
    static bool NullScenario(ref Scenario __result) { __result = null; return false; }
    static bool Skip() { return false; }
    static IEnumerable<CodeInstruction> NoRenderTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        int skip = 0;
        foreach (CodeInstruction ci in instructions)
        {
            var mi = ci.operand as MethodInfo;
            CodeInstruction outI = ci;
            if (skip > 0) { skip--; outI = new CodeInstruction(OC.Nop); }
            else if (mi != null && mi.DeclaringType == typeof(Pawn) && mi.Name == "get_Drawer")
            { outI = new CodeInstruction(OC.Pop); skip = 3; } // drops .renderer.WoundOverlays.ClearCache()
            else if (mi != null && mi.DeclaringType == typeof(PortraitsCache))
                outI = new CodeInstruction(OC.Pop);
            else if (mi != null && mi.DeclaringType == typeof(GlobalTextureAtlasManager))
            {
                var pop = new CodeInstruction(OC.Pop); pop.labels.AddRange(ci.labels); pop.blocks.AddRange(ci.blocks);
                yield return pop;
                outI = new CodeInstruction(OC.Ldc_I4_0);
                yield return outI;
                continue;
            }
            if (!ReferenceEquals(outI, ci)) { outI.labels.AddRange(ci.labels); outI.blocks.AddRange(ci.blocks); }
            yield return outI;
        }
    }
    static bool ConsciousnessStub(ref float __result) { __result = TestConsciousness; return false; }
    static bool OneStub(ref float __result) { __result = 1f; return false; }
    static bool TranslateStub(string key, ref TaggedString __result) { __result = key; return false; } // no LanguageWorker outside the game
    static bool ResolveStub(TaggedString taggedStr, ref string __result) { __result = taggedStr.RawText; return false; } // no colour tables either

    static IEnumerable<CodeInstruction> NoDlcTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction ci in instructions)
        {
            var mi = ci.operand as MethodInfo;
            if ((ci.opcode == System.Reflection.Emit.OpCodes.Call) && mi != null && mi.DeclaringType == typeof(ModsConfig)
                && mi.ReturnType == typeof(bool) && mi.GetParameters().Length == 0)
            {
                var repl = new CodeInstruction(System.Reflection.Emit.OpCodes.Ldc_I4_0);
                repl.labels.AddRange(ci.labels); repl.blocks.AddRange(ci.blocks);
                yield return repl;
            }
            else yield return ci;
        }
    }

    static HarmonyMethod Stub(string name) { return new HarmonyMethod(typeof(IntegrationTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)); }

    static void InstallStubs(Harmony h)
    {
        foreach (MethodInfo m in typeof(Log).GetMethods(BindingFlags.Public | BindingFlags.Static))
            if ((m.Name == "Message" || m.Name == "Warning" || m.Name == "Error" || m.Name == "ErrorOnce" || m.Name == "WarningOnce")
                && m.GetParameters().Length >= 1)
                h.Patch(m, Stub("LogPrefix"));
        // ModsConfig's static constructor reads the mod list from disk (Unity paths). Patching its getters would run
        // that constructor, so instead the few pipeline methods that ask "is DLC X active?" get those calls replaced
        // by 'false' (a pure test-environment adaptation: no DLC content exists here anyway).
        var noDlc = new HarmonyMethod(typeof(IntegrationTests).GetMethod("NoDlcTranspiler", BindingFlags.Static | BindingFlags.NonPublic));
        foreach (MethodBase m in new MethodBase[] {
                     AccessTools.Method(typeof(StatWorker), "GetValueUnfinalized"),
                     AccessTools.Method(typeof(StatWorker), "FinalizeValue"),
                     AccessTools.Method(typeof(PawnCapacityUtility), "CalculateCapacityLevel") })
            h.Patch(m, transpiler: noDlc);
        h.Patch(AccessTools.PropertyGetter(typeof(Prefs), "DevMode"), Stub("FalseGetter"));
        h.Patch(AccessTools.PropertyGetter(typeof(Find), "Scenario"), Stub("NullScenario"));
        h.Patch(AccessTools.Method(typeof(MeditationFocusTypeAvailabilityCache), "ClearFor"), Stub("Skip"));
        // HediffSet.DirtyCache() also dirties rendering caches (wound overlays, portraits, texture atlas). Those types
        // have Unity static constructors, so the three render calls are removed from DirtyCache's IL; everything
        // else in DirtyCache (missing-part cache, capacities dirty, Parametric's postfix) runs for real.
        h.Patch(AccessTools.Method(typeof(HediffSet), "DirtyCache"),
                transpiler: new HarmonyMethod(typeof(IntegrationTests).GetMethod("NoRenderTranspiler", BindingFlags.Static | BindingFlags.NonPublic)));
        h.Patch(AccessTools.Method(typeof(SummaryHealthHandler), "Notify_HealthChanged"), Stub("Skip"));
        h.Patch(AccessTools.Method(typeof(PawnCapacityWorker_Consciousness), "CalculateCapacityLevel"), Stub("ConsciousnessStub"));
        h.Patch(AccessTools.Method(typeof(TranslatorFormattedStringExtensions), "Translate", new[] { typeof(string), typeof(NamedArgument), typeof(NamedArgument) }), Stub("TranslateStub"));
        h.Patch(AccessTools.Method(typeof(ColoredText), "Resolve", new[] { typeof(TaggedString) }), Stub("ResolveStub"));
        h.Patch(AccessTools.Method(typeof(PawnCapacityWorker_Breathing), "CalculateCapacityLevel"), Stub("OneStub"));
        h.Patch(AccessTools.Method(typeof(PawnCapacityWorker_BloodPumping), "CalculateCapacityLevel"), Stub("OneStub"));
    }

    // ---------------- Def/record builders ----------------
    static T New<T>() { return (T)FormatterServices.GetUninitializedObject(typeof(T)); }
    static BodyPartTagDef Tag(string name) { var t = new BodyPartTagDef(); t.defName = name; return t; }

    static BodyPartDef PartDef(string name, int hp, params BodyPartTagDef[] tags)
    {
        var d = new BodyPartDef();
        d.defName = name; d.label = name.ToLowerInvariant(); d.hitPoints = hp;
        d.tags = new List<BodyPartTagDef>(tags);
        return d;
    }

    static BodyPartRecord Rec(BodyPartDef def, float coverage, params BodyPartRecord[] children)
    {
        var r = new BodyPartRecord();
        r.def = def; r.coverage = coverage;
        r.parts = new List<BodyPartRecord>(children);
        return r;
    }

    static BodyDef MakeBody(string name, BodyPartRecord root)
    {
        var b = new BodyDef();
        b.defName = name; b.corePart = root;
        b.ResolveReferences();
        return b;
    }

    static BodyDef HumanBody()
    {
        var torso = PartDef("Torso", 40);
        var spine = PartDef("Spine", 25, BodyPartTagDefOf.Spine);
        var pelvis = PartDef("Pelvis", 25, BodyPartTagDefOf.Pelvis);
        var shoulder = PartDef("Shoulder", 30, BodyPartTagDefOf.ManipulationLimbCore);
        var arm = PartDef("Arm", 30, BodyPartTagDefOf.ManipulationLimbSegment);
        var hand = PartDef("Hand", 20, BodyPartTagDefOf.ManipulationLimbSegment);
        var finger = PartDef("Finger", 8, BodyPartTagDefOf.ManipulationLimbDigit);
        var leg = PartDef("Leg", 30, BodyPartTagDefOf.MovingLimbCore);
        var foot = PartDef("Foot", 25, BodyPartTagDefOf.MovingLimbSegment);
        var toe = PartDef("Toe", 8, BodyPartTagDefOf.MovingLimbDigit);

        Func<BodyPartRecord> armChain = () => Rec(shoulder, 0.12f,
            Rec(arm, 0.77f, Rec(hand, 0.14f, Rec(finger, 0.08f), Rec(finger, 0.08f), Rec(finger, 0.08f), Rec(finger, 0.08f), Rec(finger, 0.08f))));
        Func<BodyPartRecord> legChain = () => Rec(leg, 0.14f,
            Rec(foot, 0.10f, Rec(toe, 0.06f), Rec(toe, 0.06f), Rec(toe, 0.06f), Rec(toe, 0.06f), Rec(toe, 0.06f)));

        var root = Rec(torso, 1f, Rec(spine, 0.025f), Rec(pelvis, 0.025f), armChain(), armChain(), legChain(), legChain());
        return MakeBody("TestHuman", root);
    }

    static BodyDef QuadrupedBody()
    {
        var body = PartDef("Body", 40);
        var spine = PartDef("Spine", 25, BodyPartTagDefOf.Spine);
        var pelvis = PartDef("Pelvis", 25, BodyPartTagDefOf.Pelvis);
        var leg = PartDef("Leg", 30, BodyPartTagDefOf.MovingLimbCore);
        var paw = PartDef("Paw", 15, BodyPartTagDefOf.MovingLimbSegment);
        Func<BodyPartRecord> legChain = () => Rec(leg, 0.07f, Rec(paw, 0.1f));
        return MakeBody("TestQuadruped", Rec(body, 1f, Rec(spine, 0.03f), Rec(pelvis, 0.03f), legChain(), legChain(), legChain(), legChain()));
    }

    static BodyDef BlobBody() { return MakeBody("TestBlob", Rec(PartDef("Blob", 50), 1f)); }

    static BodyDef TentacleBody()
    {
        var core = PartDef("Mantle", 50);
        var tentacle = PartDef("Tentacle", 20, BodyPartTagDefOf.ManipulationLimbCore);
        return MakeBody("TestTentacled", Rec(core, 1f, Rec(tentacle, 0.1f), Rec(tentacle, 0.1f), Rec(tentacle, 0.1f)));
    }

    static LifeStageDef adultStage;
    static int nextId = 1000;

    static Pawn MakePawn(string name, BodyDef body, Intelligence intelligence, bool packAnimal = false, float bodySize = 1f)
    {
        var race = new RaceProperties();
        race.body = body; race.intelligence = intelligence;
        race.baseBodySize = bodySize; race.baseHealthScale = 1f; race.packAnimal = packAnimal;
        var lsa = new LifeStageAge(); lsa.def = adultStage; lsa.minAge = 0f;
        race.lifeStageAges = new List<LifeStageAge> { lsa };

        var def = New<ThingDef>(); // ThingDef ctor touches graphics (Unity); fields are set explicitly
        def.defName = name + "_Race"; def.label = name; def.race = race; def.category = ThingCategory.Pawn;
        def.thingClass = typeof(Pawn);
        def.statBases = new List<StatModifier>();

        Pawn p = New<Pawn>();
        p.def = def;
        p.thingIDNumber = nextId++;
        var age = New<Pawn_AgeTracker>();
        typeof(Pawn_AgeTracker).GetField("pawn", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(age, p);
        age.lockedLifeStageIndex = 0;
        typeof(Pawn_AgeTracker).GetField("cachedLifeStageIndex", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(age, 0);
        p.ageTracker = age;
        p.health = new Pawn_HealthTracker(p);
        return p;
    }

    static Pawn Human(string name, float bodySize = 1f) { return MakePawn(name, HumanBody(), Intelligence.Humanlike, false, bodySize); }

    // ---------------- Hediffs (inserted directly, then the real HediffSet.DirtyCache() is called) ----------------
    static List<BodyPartRecord> Parts(Pawn p, BodyPartTagDef tag) { return p.RaceProps.body.GetPartsWithTag(tag); }

    static T AddHediff<T>(Pawn p, HediffDef def, BodyPartRecord part, float severity = 1f, bool notify = true) where T : Hediff
    {
        T h = New<T>();
        h.def = def; h.pawn = p;
        typeof(Hediff).GetField("part", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(h, part);
        typeof(Hediff).GetField("severityInt", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(h, severity);
        p.health.hediffSet.hediffs.Add(h);
        if (notify) p.health.hediffSet.DirtyCache(); // REAL vanilla method → our postfix marks the cache dirty
        return h;
    }

    static HediffDef MakeDef(string name, Type cls) { var d = new HediffDef(); d.defName = name; d.hediffClass = cls; d.maxSeverity = 99999f; return d; }
    static readonly HediffDef missingDef = MakeDef("MissingBodyPart", typeof(Hediff_MissingPart));
    static HediffDef Prosthetic(string name, float eff)
    {
        var d = MakeDef(name, typeof(Hediff_AddedPart));
        d.addedPartProps = new AddedBodyPartProps(); d.addedPartProps.partEfficiency = eff; d.addedPartProps.solid = true;
        return d;
    }

    static void RemovePart(Pawn p, BodyPartRecord part) { AddHediff<Hediff_MissingPart>(p, missingDef, part); }
    static void Install(Pawn p, BodyPartRecord part, HediffDef def) { AddHediff<Hediff_AddedPart>(p, def, part); }
    static void InstallAll(Pawn p, BodyPartTagDef tag, HediffDef def) { foreach (var x in Parts(p, tag)) Install(p, x, def); }
    static void Injure(Pawn p, BodyPartRecord part, float damage) { AddHediff<Hediff_Injury>(p, MakeDef("Cut", typeof(Hediff_Injury)), part, damage); }

    static void WholeBodyCapMod(Pawn p, PawnCapacityDef cap, float offset, float postFactor = 1f)
    {
        var d = MakeDef("TestCapMod_" + cap.defName, typeof(Hediff));
        var stage = new HediffStage();
        var mod = new PawnCapacityModifier(); mod.capacity = cap; mod.offset = offset; mod.postFactor = postFactor;
        stage.capMods = new List<PawnCapacityModifier> { mod };
        d.stages = new List<HediffStage> { stage };
        AddHediff<Hediff>(p, d, null);
    }
    static void ManipSetMax(Pawn p, float setMax)
    {
        var d = MakeDef("TestManipSetMax", typeof(Hediff));
        var stage = new HediffStage();
        var mod = new PawnCapacityModifier(); mod.capacity = PawnCapacityDefOf.Manipulation; mod.setMax = setMax;
        stage.capMods = new List<PawnCapacityModifier> { mod };
        d.stages = new List<HediffStage> { stage };
        AddHediff<Hediff>(p, d, null);
    }
    // Stands in for another mod's Harmony postfix on the vanilla Manipulation worker (1 = result-preserving).
    static float ModdedWorkerScale = 1f;
    static void ModdedManipulationWorker(ref float __result) { __result *= ModdedWorkerScale; }
    // ---------------- TEST-ONLY stand-ins for other mods' mass-capacity hooks (never shipped) ----------------
    const string ThirdPartyId = "thirdparty.masstest";
    static bool inFakeStat, inCross;
    static Pawn CrossPawnOwner, CrossPawnPartner;
    static bool ThrowNext;
    static readonly Dictionary<Pawn, float> StoredMass = new Dictionary<Pawn, float>();
    // A modded "mass carry capacity" stat whose worker reads the PATCHED MassUtility.Capacity and applies a strength factor.
    static float FakeMassStat(Pawn p)
    {
        bool old = inFakeStat; inFakeStat = true;
        try { return MassUtility.Capacity(p, null) * 1.04f; } finally { inFakeStat = old; }
    }
    static bool ThirdPartyStatPrefix(Pawn p, ref float __result) { if (inFakeStat) return true; __result = FakeMassStat(p); return false; }
    static void ThirdPartyStatPostfix(Pawn p, ref float __result) { if (inFakeStat) return; __result = FakeMassStat(p); }
    static void ThirdPartyFactorPostfix(ref float __result) { __result *= 1.04f; }
    static void ThirdPartyCrossPawnPostfix(Pawn p, ref float __result)
    {
        if (inCross || p != CrossPawnOwner) return;
        inCross = true;
        try { __result += MassUtility.Capacity(CrossPawnPartner, null); } finally { inCross = false; }
    }
    static void ThirdPartyThrowingPrefix() { if (ThrowNext) { ThrowNext = false; throw new InvalidOperationException("third-party test exception"); } }
    static bool ThirdPartyCachingPrefix(Pawn p, ref float __result) { float v; if (StoredMass.TryGetValue(p, out v)) { __result = v; return false; } return true; }
    static void ThirdPartyCachingPostfix(Pawn p, float __result) { StoredMass[p] = __result; }

    static void PartOffset(Pawn p, BodyPartRecord part, float offset)
    {
        var d = MakeDef("TestPartBuff", typeof(Hediff));
        var stage = new HediffStage(); stage.partEfficiencyOffset = offset;
        d.stages = new List<HediffStage> { stage };
        AddHediff<Hediff>(p, d, part);
    }
    static void CarryStatModifier(Pawn p, float offset, float factor)
    {
        var d = MakeDef("TestCarryBuff", typeof(Hediff));
        var stage = new HediffStage();
        if (offset != 0f) { var m = new StatModifier(); m.stat = carryStat; m.value = offset; stage.statOffsets = new List<StatModifier> { m }; }
        if (factor != 1f) { var m = new StatModifier(); m.stat = carryStat; m.value = factor; stage.statFactors = new List<StatModifier> { m }; }
        d.stages = new List<HediffStage> { stage };
        AddHediff<Hediff>(p, d, null);
    }

    // ---------------- Reporting ----------------
    static void Check(string what, bool ok)
    {
        checks++;
        if (!ok) failures++;
        Console.WriteLine((ok ? "  ok:   " : "  FAIL: ") + what);
    }
    static bool Near(float a, float b, float tol = 0.02f) { return Math.Abs(a - b) <= tol * Math.Max(1f, Math.Abs(b)); }

    static LoadSupportResult Report(string label, Pawn p)
    {
        LoadSupportResult r = LoadSupportCache.GetResult(p, true);
        float mass = MassUtility.Capacity(p, null);
        Console.WriteLine(string.Format("{0,-44} L {1,5:0.00}->{2,6:0.00}  C {3,5:0.00}->{4,6:0.00}  U {5,5:0.00}->{6,6:0.00}  bneck {7,4:0.00}  LS {8,6:0.000}  mass {9,6:0.0}",
            label, r.LowerEfficiency, r.LowerStrength, r.CoreEfficiency, r.CoreStrength, r.UpperEfficiency, r.UpperStrength,
            r.BottleneckFactor, r.LoadSupport, mass));
        return r;
    }

    // ---------------- The vanilla-configured CarryingCapacity StatDef ----------------
    static StatDef carryStat;
    static PawnCapacityFactor manipFactor;

    static StatDef BuildCarryingCapacity()
    {
        var s = new StatDef();
        s.defName = "CarryingCapacity"; s.label = "carrying capacity";
        s.defaultBaseValue = 75f; s.minValue = 0f; s.toStringStyle = ToStringStyle.Integer;
        s.workerClass = typeof(StatWorker);
        manipFactor = new PawnCapacityFactor(); manipFactor.capacity = PawnCapacityDefOf.Manipulation; manipFactor.weight = 1f;
        s.capacityFactors = new List<PawnCapacityFactor> { manipFactor };
        var bodySize = new StatPart_BodySize(); bodySize.parentStat = s;
        s.parts = new List<StatPart> { bodySize };
        s.immutable = false;
        return s;
    }

    static float Carry(Pawn p) { return p.GetStatValue(carryStat, true, -1); }
    static float CarryVanilla(Pawn p)
    {
        StatPart_LoadSupport.Bypass = true;
        try { return p.GetStatValue(carryStat, true, -1); } finally { StatPart_LoadSupport.Bypass = false; }
    }

    static void Main()
    {
        var harness = new Harmony("parametric.tests");
        InstallStubs(harness);

        BodyPartTagDefOf.MovingLimbCore = Tag("MovingLimbCore");
        BodyPartTagDefOf.MovingLimbSegment = Tag("MovingLimbSegment");
        BodyPartTagDefOf.MovingLimbDigit = Tag("MovingLimbDigit");
        BodyPartTagDefOf.ManipulationLimbCore = Tag("ManipulationLimbCore");
        BodyPartTagDefOf.ManipulationLimbSegment = Tag("ManipulationLimbSegment");
        BodyPartTagDefOf.ManipulationLimbDigit = Tag("ManipulationLimbDigit");
        BodyPartTagDefOf.Spine = Tag("Spine");
        BodyPartTagDefOf.Pelvis = Tag("Pelvis");

        // Capacity defs registered in the real DefDatabase so the real PawnCapacitiesHandler (DefMap) works.
        Func<string, Type, PawnCapacityDef> cap = (n, w) =>
        {
            var cd = new PawnCapacityDef(); cd.defName = n; cd.workerClass = w; cd.minValue = 0f;
            DefDatabase<PawnCapacityDef>.Add(cd);
            return cd;
        };
        PawnCapacityDefOf.Consciousness = cap("Consciousness", typeof(PawnCapacityWorker_Consciousness));
        PawnCapacityDefOf.Moving = cap("Moving", typeof(PawnCapacityWorker_Moving));
        PawnCapacityDefOf.Manipulation = cap("Manipulation", typeof(PawnCapacityWorker_Manipulation));
        PawnCapacityDefOf.Manipulation.zeroIfCannotBeAwake = true;
        PawnCapacityDefOf.Breathing = cap("Breathing", typeof(PawnCapacityWorker_Breathing));       // needed by the Moving worker
        PawnCapacityDefOf.BloodPumping = cap("BloodPumping", typeof(PawnCapacityWorker_BloodPumping));
        List<PawnCapacityDef> capDefs = DefDatabase<PawnCapacityDef>.AllDefsListForReading;
        for (int i = 0; i < capDefs.Count; i++) capDefs[i].index = (ushort)i;

        adultStage = new LifeStageDef(); adultStage.defName = "Adult"; adultStage.bodySizeFactor = 1f; adultStage.healthScaleFactor = 1f;
        adultStage.developmentalStage = DevelopmentalStage.Adult;

        ParametricMod.Settings = new ParametricSettings();

        // Minimal running game: StatWorker.GetValue reads Find.TickManager.TicksGame.
        var game = New<Game>(); var tm = New<TickManager>();
        typeof(Game).GetField("tickManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(game, tm);
        Current.Game = game;
        FieldInfo ticks = typeof(TickManager).GetField("ticksGameInt", BindingFlags.Instance | BindingFlags.NonPublic);
        ticks.SetValue(tm, 100);

        // ================= Harmony =================
        Console.WriteLine("=== Harmony: applying Parametric's patches to real 1.6 methods ===");
        new Harmony(ParametricMod.HarmonyId).PatchAll(typeof(ParametricMod).Assembly);
        var patched = new List<string>();
        foreach (MethodBase m in Harmony.GetAllPatchedMethods())
        {
            Patches info = Harmony.GetPatchInfo(m);
            if (info.Owners.Contains(ParametricMod.HarmonyId))
                patched.Add(m.DeclaringType.FullName + "." + m.Name + "(" + string.Join(", ", Array.ConvertAll(m.GetParameters(), x => x.ParameterType.Name)) + ")");
        }
        patched.Sort();
        foreach (string s in patched) Console.WriteLine("  patched: " + s);
        Check("Harmony ID is aRed.Parametric", ParametricMod.HarmonyId == "aRed.Parametric");
        Check("exactly 3 methods patched by Parametric", patched.Count == 3);
        Check("MassUtility.Capacity(Pawn, StringBuilder) patched", patched.Exists(s => s.StartsWith("RimWorld.MassUtility.Capacity(Pawn, StringBuilder")));
        Check("HediffSet.DirtyCache() patched", patched.Exists(s => s.StartsWith("Verse.HediffSet.DirtyCache(")));
        Check("Pawn.GetInspectString() patched", patched.Exists(s => s.StartsWith("Verse.Pawn.GetInspectString(")));
        Check("no leftover patches owned by the old ID aRed.LoadSupport", !Array.Exists(new List<MethodBase>(Harmony.GetAllPatchedMethods()).ToArray(), m => Harmony.GetPatchInfo(m).Owners.Contains("aRed.LoadSupport")));

        // ================= StatPart injection & ordering =================
        Console.WriteLine("\n=== StatPart injection & ordering ===");
        carryStat = BuildCarryingCapacity();
        StatDefOf.CarryingCapacity = carryStat;
        StatPart_LoadSupport.InjectInto(carryStat);
        StatPart_LoadSupport.InjectInto(carryStat); // idempotent
        Check("idempotent: exactly one StatPart_LoadSupport", carryStat.parts.FindAll(x => x is StatPart_LoadSupport).Count == 1);
        Check("appended AFTER existing parts (vanilla StatPart_BodySize stays first, untouched)", carryStat.parts.Count == 2 && carryStat.parts[0] is StatPart_BodySize && carryStat.parts[1] is StatPart_LoadSupport);
        Check("parentStat set", carryStat.parts[1].parentStat == carryStat);
        var imm = new StatDef(); imm.defName = "ImmutableTest"; imm.workerClass = typeof(StatWorker); imm.immutable = true;
        StatPart_LoadSupport.InjectInto(imm);
        Check("immutable flag cleared on injection (otherwise StatWorker caches forever)", !imm.immutable && imm.parts.Count == 1);

        // ================= Body scenarios (Load Support value) =================
        Console.WriteLine("\n=== Body scenarios: real vanilla PawnCapacityUtility on synthetic bodies ===");
        var bionicLeg = Prosthetic("BionicLeg", 1.25f); var bionicArm = Prosthetic("BionicArm", 1.25f); var bionicSpine = Prosthetic("BionicSpine", 1.25f);
        var archoLeg = Prosthetic("ArchotechLeg", 1.5f); var archoArm = Prosthetic("ArchotechArm", 1.5f);
        var prostheticLeg = Prosthetic("SimpleProstheticLeg", 0.85f); var pegLeg = Prosthetic("PegLeg", 0.6f);
        var mod3Leg = Prosthetic("ModLeg300", 3f); var mod3Arm = Prosthetic("ModArm300", 3f); var mod3Spine = Prosthetic("ModSpine300", 3f); var mod3Pelvis = Prosthetic("ModPelvis300", 3f);
        var mod5Leg = Prosthetic("ModLeg500", 5f); var mod5Arm = Prosthetic("ModArm500", 5f); var mod5Spine = Prosthetic("ModSpine500", 5f); var mod5Pelvis = Prosthetic("ModPelvis500", 5f);

        var R = new Dictionary<string, LoadSupportResult>();
        Pawn p;
        p = Human("Healthy"); R["healthy"] = Report("healthy human", p);
        p = Human("OneLeg"); RemovePart(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0]); R["oneleg"] = Report("missing one leg", p);
        p = Human("NoLegs"); foreach (var l in Parts(p, BodyPartTagDefOf.MovingLimbCore)) RemovePart(p, l); R["nolegs"] = Report("missing both legs", p);
        p = Human("Peg"); Install(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0], pegLeg); R["peg"] = Report("peg leg (0.6)", p);
        p = Human("Prost"); Install(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0], prostheticLeg); R["prost"] = Report("simple prosthetic leg (0.85)", p);
        p = Human("Bionic1"); Install(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0], bionicLeg); R["bionic1"] = Report("one bionic leg", p);
        p = Human("Bionic2"); InstallAll(p, BodyPartTagDefOf.MovingLimbCore, bionicLeg); R["bionic2"] = Report("two bionic legs", p);
        p = Human("FullBionic"); InstallAll(p, BodyPartTagDefOf.MovingLimbCore, bionicLeg); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, bionicArm); Install(p, Parts(p, BodyPartTagDefOf.Spine)[0], bionicSpine); R["fullbionic"] = Report("full vanilla bionics", p);
        p = Human("Archo"); InstallAll(p, BodyPartTagDefOf.MovingLimbCore, archoLeg); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, archoArm); Install(p, Parts(p, BodyPartTagDefOf.Spine)[0], bionicSpine); R["archo"] = Report("archotech legs+arms, bionic spine", p);
        p = Human("ModLegs"); InstallAll(p, BodyPartTagDefOf.MovingLimbCore, mod3Leg); R["modlegs"] = Report("modded 300% legs, normal core", p);
        p = Human("ModLegsCore"); InstallAll(p, BodyPartTagDefOf.MovingLimbCore, mod3Leg); Install(p, Parts(p, BodyPartTagDefOf.Spine)[0], mod3Spine); Install(p, Parts(p, BodyPartTagDefOf.Pelvis)[0], mod3Pelvis); R["modlegscore"] = Report("modded 300% legs + spine + pelvis", p);
        p = Human("Injured"); Injure(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0], 15f); Injure(p, p.RaceProps.body.corePart, 12f); R["injured"] = Report("leg 50% HP, torso 70% HP", p);
        p = Human("MissingFoot"); RemovePart(p, Parts(p, BodyPartTagDefOf.MovingLimbSegment)[0]); R["foot"] = Report("missing one foot", p);
        p = Human("Exo"); WholeBodyCapMod(p, PawnCapacityDefOf.Moving, 1.0f); R["exo"] = Report("capMod Moving +100%", p);
        p = Human("ManipMod"); WholeBodyCapMod(p, PawnCapacityDefOf.Manipulation, 1.0f); R["manipmod"] = Report("capMod Manipulation +100% (not in LS)", p);
        p = Human("SpineBuff"); PartOffset(p, Parts(p, BodyPartTagDefOf.Spine)[0], 0.5f); R["spinebuff"] = Report("partEfficiencyOffset +0.5 on spine", p);
        p = MakePawn("Muffalo", QuadrupedBody(), Intelligence.Animal, true); R["animal"] = Report("healthy pack animal (no arms)", p);
        p = MakePawn("Muffalo3", QuadrupedBody(), Intelligence.Animal, true); RemovePart(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0]); R["animal3"] = Report("pack animal missing one leg", p);
        p = MakePawn("Blob", BlobBody(), Intelligence.Humanlike); R["blob"] = Report("no recognised anatomy", p);
        p = MakePawn("Squid", TentacleBody(), Intelligence.Humanlike); R["squid"] = Report("manipulators only", p);
        p = MakePawn("SquidHurt", TentacleBody(), Intelligence.Humanlike); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0]); R["squidhurt"] = Report("manipulators only, one lost", p);

        Console.WriteLine("\n=== Body assertions ===");
        Func<string, float> LS = k => R[k].LoadSupport;
        Check("healthy human = 1.000", Math.Abs(LS("healthy") - 1f) < 1e-5f);
        Check("healthy pack animal = 1.000", Math.Abs(LS("animal") - 1f) < 1e-5f);
        Check("strange anatomy = 1.000", Math.Abs(LS("blob") - 1f) < 1e-5f);
        Check("tentacle anatomy healthy = 1.000", Math.Abs(LS("squid") - 1f) < 1e-5f);
        Check("vanilla limb math: one leg missing -> lower 0.5", Math.Abs(R["oneleg"].LowerEfficiency - 0.5f) < 1e-4f);
        Check("vanilla limb math: one bionic leg -> 1.125", Math.Abs(R["bionic1"].LowerEfficiency - 1.125f) < 1e-4f);
        Check("missing both legs: valid, heavily reduced", LS("nolegs") > 0f && LS("nolegs") <= 0.25f);
        Check("peg < prosthetic < healthy < bionic1 < bionic2 < full bionic < archotech",
            LS("peg") < LS("prost") && LS("prost") < 1f && 1f < LS("bionic1") && LS("bionic1") < LS("bionic2")
            && LS("bionic2") < LS("fullbionic") && LS("fullbionic") < LS("archo"));
        Check("bottleneck: 300% legs on normal core in 1.8..2.5", LS("modlegs") > 1.8f && LS("modlegs") < 2.5f);
        Check("legs + core > 4x legs alone", LS("modlegscore") > 4f * LS("modlegs"));
        Check("injuries reduce", LS("injured") < 1f && LS("injured") > 0.5f);
        Check("missing foot disables that limb like vanilla (limb = leg x foot)", Math.Abs(R["foot"].LowerEfficiency - 0.5f) < 1e-4f);
        Check("Moving capMod +100%: dampened to ~1.41", Math.Abs(R["exo"].LowerEfficiency - 1.4142f) < 0.01f);
        Check("Manipulation capMod NOT read into Load Support (vanilla factor keeps it, counted once)", Math.Abs(R["manipmod"].UpperEfficiency - 1f) < 1e-5f && Math.Abs(LS("manipmod") - 1f) < 1e-5f);
        Check("partEfficiencyOffset on spine raises core", R["spinebuff"].CoreEfficiency > 1.1f && LS("spinebuff") > 1f);
        Check("animal missing a leg: reduced", LS("animal3") < 1f && LS("animal3") > 0.6f);
        Check("tentacle anatomy with lost limb: reduced but valid", LS("squidhurt") < 1f && LS("squidhurt") > 0.3f);

        // ================= FULL CarryingCapacity pipeline =================
        // Rule under test (v0.1.1): manipulation limbs X <= 100% keep vanilla's Manipulation factor untouched (disability
        // stays; Load Support multiplies on top). X > 100%: only the above-normal limb bonus is removed, using the
        // Manipulation level the pawn would have with 100% limbs (Y1); every systemic influence stays.
        //   expected = expectedBase x LS, where expectedBase is what vanilla gives with the limb bonus (if any) removed.
        Console.WriteLine("\n=== FULL CarryingCapacity pipeline: pawn.GetStatValue(CarryingCapacity) through the real StatWorker ===");
        var P = new Dictionary<string, float[]>();
        const int X_ = 0, Y_ = 1, Y1_ = 2, VAN = 3, LSV = 4, COMP = 5, OLD = 6, NOW = 7, EXP = 8, EXACT = 9;
        Action header = () => Console.WriteLine(string.Format("  {0,-46} {1,5} {2,5} {3,5} {4,8} {5,7} {6,6} {7,8} {8,8} {9,8}",
            "case", "limbs", "Manip", "@100%", "vanilla", "LS", "comp", "PR#1", "NOW", "expected"));
        Func<string, Pawn, float, float[]> Pipe = (label, pawn, expectedBase) =>
        {
            LoadSupportResult r = LoadSupportCache.GetResult(pawn, true);
            float vanilla = CarryVanilla(pawn);                   // real pipeline, Load Support bypassed
            float now = Carry(pawn);                              // real pipeline, Parametric active
            ManipulationCompensation.Info info;
            float cmp = ManipulationCompensation.Compute(carryStat, pawn, r, out info);
            float y = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation);
            float x = r.HasUpper ? r.UpperEfficiency : 1f;
            // What the first PR #1 revision produced: every limb deviation compensated via Y / X (0 limbs: vanilla 0).
            // (vanilla factor configuration: weight 1, so the old multiplier was simply normalizedLevel / Y)
            float oldNorm = Math.Abs(y - GenMath.RoundedHundredth(x)) < 1e-4f ? 1f : y / x;
            float oldRule = (x > 1e-4f && y > 1e-4f ? vanilla * oldNorm / y : vanilla) * r.LoadSupport;
            float expected = expectedBase * r.LoadSupport;
            Console.WriteLine(string.Format("  {0,-46} {1,5:0.00} {2,5:0.00} {3,5:0.00} {4,8:0.0} {5,7:0.000} {6,6:0.000} {7,8:0.0} {8,8:0.0} {9,8:0.0}{10}",
                label, x, y, info.Applied ? info.NormalizedLevel : y, vanilla, r.LoadSupport, cmp, oldRule, now, expected,
                info.Applied ? (info.ExactNeutral ? "" : "  [ratio fallback]") : ""));
            var row = new[] { x, y, info.NormalizedLevel, vanilla, r.LoadSupport, cmp, oldRule, now, expected, info.ExactNeutral ? 1f : 0f };
            P[label] = row;
            return row;
        };
        Func<string, float[]> Row = k => P[k];
        Func<string, bool> Exact = k => Near(Row(k)[NOW], Row(k)[EXP], 0.002f);
        Func<float, HediffDef> ArmPart = eff => Prosthetic("TestArm" + (eff * 100f).ToString("0"), eff);
        Func<string, float, Pawn> ArmsAt = (name, eff) =>
        {
            Pawn a = Human(name);
            if (Math.Abs(eff - 1f) > 1e-6f) InstallAll(a, BodyPartTagDefOf.ManipulationLimbCore, ArmPart(eff));
            return a;
        };
        TestConsciousness = 1f;

        Console.WriteLine("\n--- Core cases ---"); header();
        p = Human("C01"); Pipe("01 healthy human", p, 75f);
        p = Human("C02"); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0]); Pipe("02 missing one arm", p, 75f * 0.5f);
        p = Human("C03"); foreach (var a in Parts(p, BodyPartTagDefOf.ManipulationLimbCore)) Injure(p, a, 18f); Pipe("03 both arms compromised (shoulders 40% HP)", p, 75f * 0.33f);
        p = Human("C03b"); foreach (var a in Parts(p, BodyPartTagDefOf.ManipulationLimbCore)) RemovePart(p, a); Pipe("03b both arms missing", p, 0f);
        p = Human("C04"); Install(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0], bionicArm); Pipe("04 one bionic arm", p, 75f);
        p = Human("C05"); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, bionicArm); Pipe("05 two bionic arms", p, 75f);
        p = Human("C06"); InstallAll(p, BodyPartTagDefOf.MovingLimbCore, bionicLeg); Pipe("06 two bionic legs only", p, 75f);
        p = Human("C07"); InstallAll(p, BodyPartTagDefOf.MovingLimbCore, bionicLeg); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, bionicArm); Install(p, Parts(p, BodyPartTagDefOf.Spine)[0], bionicSpine); Pipe("07 full vanilla bionic", p, 75f);
        p = Human("C08"); InstallAll(p, BodyPartTagDefOf.MovingLimbCore, archoLeg); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, archoArm); Pipe("08 archotech arms + legs", p, 75f);
        p = Human("C09"); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, mod3Arm); Pipe("09 modded 300% arms only", p, 75f);
        p = Human("C10"); InstallAll(p, BodyPartTagDefOf.MovingLimbCore, mod3Leg); Pipe("10 modded 300% legs only", p, 75f);
        Pawn full3 = Human("C11"); InstallAll(full3, BodyPartTagDefOf.MovingLimbCore, mod3Leg); InstallAll(full3, BodyPartTagDefOf.ManipulationLimbCore, mod3Arm);
        Install(full3, Parts(full3, BodyPartTagDefOf.Spine)[0], mod3Spine); Install(full3, Parts(full3, BodyPartTagDefOf.Pelvis)[0], mod3Pelvis); PartOffset(full3, full3.RaceProps.body.corePart, 2f);
        Pipe("11 modded 300% full body", full3, 75f);
        p = Human("C12"); InstallAll(p, BodyPartTagDefOf.MovingLimbCore, mod5Leg); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, mod5Arm);
        Install(p, Parts(p, BodyPartTagDefOf.Spine)[0], mod5Spine); Install(p, Parts(p, BodyPartTagDefOf.Pelvis)[0], mod5Pelvis); PartOffset(p, p.RaceProps.body.corePart, 4f);
        Pipe("12 modded 500% full body", p, 75f);
        p = Human("C14"); CarryStatModifier(p, 0f, 2f); Pipe("14 direct CarryingCapacity factor x2", p, 150f);
        p = Human("C14b"); CarryStatModifier(p, 0f, 2f); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, bionicArm); Pipe("14b factor x2 + bionic arms", p, 150f);
        p = Human("C14c"); CarryStatModifier(p, 0f, 2f); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, mod3Arm); Pipe("14c factor x2 + 300% arms", p, 150f);
        p = Human("C15"); CarryStatModifier(p, 50f, 1f); Pipe("15 direct CarryingCapacity offset +50", p, 125f);
        p = Human("C15b"); CarryStatModifier(p, 50f, 1f); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0]); Pipe("15b offset +50 + missing arm", p, 125f * 0.5f);
        p = Human("C15c"); CarryStatModifier(p, 50f, 1f); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, mod3Arm); Pipe("15c offset +50 + 300% arms", p, 125f);
        p = Human("C17", 1.5f); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, bionicArm); Pipe("17 body size 1.5 + bionic arms", p, 75f * 1.5f);
        p = Human("C17b", 1.5f); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0]); Pipe("17b body size 1.5 + missing arm", p, 75f * 1.5f * 0.5f);

        Console.WriteLine("\n--- Weak-limb series (both arms replaced by parts of the given efficiency; 100% = natural arms) ---"); header();
        float[] weak = { 1f, 0.85f, 0.6f, 0.5f, 0.25f, 0.1f, 0.01f, 0f };
        var weakKeys = new List<string>();
        foreach (float e in weak)
        {
            string k = "W arms " + (e * 100f).ToString("0") + "%";
            p = ArmsAt("W" + (e * 1000f).ToString("0"), e);
            Pipe(k, p, 75f * GenMath.RoundedHundredth(e)); // vanilla Manipulation = RoundedHundredth(limbs) at consciousness 100%
            weakKeys.Add(k);
        }

        Console.WriteLine("\n--- Superhuman series (both arms) ---"); header();
        float[] strong = { 1f, 1.25f, 1.5f, 3f, 5f };
        var strongKeys = new List<string>();
        foreach (float e in strong)
        {
            string k = "S arms " + (e * 100f).ToString("0") + "%";
            p = ArmsAt("S" + (e * 100f).ToString("0"), e);
            Pipe(k, p, 75f);
            strongKeys.Add(k);
        }

        Console.WriteLine("\n--- Prosthetics ---"); header();
        var simpleArm = Prosthetic("SimpleProstheticArm", 0.85f);
        p = Human("PR1"); Install(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0], simpleArm); Pipe("P one simple prosthetic arm (85%)", p, 75f * GenMath.RoundedHundredth(0.925f));
        p = Human("PR2"); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, simpleArm); Pipe("P two simple prosthetic arms (85%)", p, 75f * 0.85f);
        p = Human("PR3"); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, Prosthetic("PoorModArm", 0.6f)); Pipe("P poor modded prosthetic arms (60%)", p, 75f * 0.6f);
        p = Human("PR4"); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, bionicArm); Pipe("P bionic arms (125%)", p, 75f);
        p = Human("PR5"); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, archoArm); Pipe("P archotech arms (150%)", p, 75f);
        p = Human("PR6"); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, mod3Arm); Pipe("P modded arms (300%)", p, 75f);
        p = Human("PR7"); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, mod5Arm); Pipe("P modded arms (500%)", p, 75f);
        p = Human("PR8"); Install(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0], archoArm); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[1]); Pipe("P archotech arm + missing arm (75%)", p, 75f * 0.75f);

        Console.WriteLine("\n--- Manipulation capMods (whole-body hediffs): systemic part must survive limb normalisation ---"); header();
        var capKeys = new List<KeyValuePair<string, float>>();
        foreach (float off in new[] { 0.5f, -0.25f })
            foreach (float e in off > 0f ? new[] { 1f, 1.25f, 3f, 5f } : new[] { 1f, 1.25f, 3f })
            {
                string k = "M arms " + (e * 100f).ToString("0") + "% + Manip " + (off > 0 ? "+" : "") + (off * 100f).ToString("0") + "%";
                p = ArmsAt("M" + (e * 100f).ToString("0") + "_" + (off * 100f).ToString("0"), e);
                WholeBodyCapMod(p, PawnCapacityDefOf.Manipulation, off);
                Pipe(k, p, 75f * (1f + off));
                capKeys.Add(new KeyValuePair<string, float>(k, off));
            }
        p = Human("M_arm_neg"); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0]); WholeBodyCapMod(p, PawnCapacityDefOf.Manipulation, -0.25f);
        Pipe("M missing arm + Manip -25% (weak: vanilla kept)", p, 75f * 0.25f);
        p = ArmsAt("M_post", 3f); WholeBodyCapMod(p, PawnCapacityDefOf.Manipulation, 0f, 0.8f); Pipe("M arms 300% + Manip postFactor x0.8", p, 75f * 0.8f);
        p = ArmsAt("M_both", 3f); WholeBodyCapMod(p, PawnCapacityDefOf.Manipulation, 0.5f, 0.8f); Pipe("M arms 300% + Manip +50% & x0.8", p, 75f * 1.5f * 0.8f);
        p = ArmsAt("M_max", 3f); ManipSetMax(p, 1.2f); Pipe("M arms 300% + Manip setMax 120%", p, 75f);
        p = ArmsAt("M_max2", 3f); ManipSetMax(p, 0.7f); Pipe("M arms 300% + Manip setMax 70%", p, 75f * 0.7f);

        Console.WriteLine("\n--- Consciousness ---"); header();
        TestConsciousness = 0.5f;
        p = Human("K1"); Pipe("K consciousness 50%, healthy arms", p, 75f * 0.5f);
        p = ArmsAt("K2", 1.25f); Pipe("K consciousness 50% + bionic arms", p, 75f * 0.5f);
        p = ArmsAt("K3", 3f); Pipe("K consciousness 50% + 300% arms", p, 75f * 0.5f);
        p = Human("K4"); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0]); Pipe("K consciousness 50% + missing arm", p, 75f * 0.25f);
        p = ArmsAt("K5", 3f); WholeBodyCapMod(p, PawnCapacityDefOf.Manipulation, 0.5f); Pipe("K consciousness 50% + 300% arms + Manip +50%", p, 75f * 1.0f);
        TestConsciousness = 1f;

        Console.WriteLine("\n=== Pipeline assertions ===");
        foreach (var kv in P)
            Check("pipeline result == expected (tol 0.2%): " + kv.Key + "  (" + kv.Value[NOW].ToString("0.0") + " vs " + kv.Value[EXP].ToString("0.0") + ")", Exact(kv.Key));
        Check("01 healthy = exactly 75", Math.Abs(Row("01 healthy human")[NOW] - 75f) < 0.01f);
        Check("02 missing one arm: vanilla x0.5 retained, LS 0.95 on top -> 35.6 kg (was 71.3)", Near(Row("02 missing one arm")[NOW], 35.6f, 0.005f) && Math.Abs(Row("02 missing one arm")[COMP] - 1f) < 1e-6f);
        Check("03 both arms ~33%: 75 x 0.33 x 0.925 ~ 22.9 kg (was 69.4)", Near(Row("03 both arms compromised (shoulders 40% HP)")[NOW], 22.9f, 0.005f));
        Check("03b both arms missing: 0", Row("03b both arms missing")[NOW] < 0.001f);
        Check("05 two bionic arms: vanilla's x1.25 removed (= 75 x LS)", Near(Row("05 two bionic arms")[NOW], 75f * Row("05 two bionic arms")[LSV], 0.002f) && Row("05 two bionic arms")[COMP] < 0.81f);
        Check("06 bionic legs only: compensation exactly 1", Math.Abs(Row("06 two bionic legs only")[COMP] - 1f) < 1e-6f);
        Check("11 THE BUG stays fixed: 300% full body = 75 x 15.59 ~ 1169 kg (not ~3508)", Near(Row("11 modded 300% full body")[NOW], 1169.1f, 0.01f));
        Check("12 500% full body = 75 x 55.9 ~ 4193 kg (not ~20963)", Near(Row("12 modded 500% full body")[NOW], 75f * 55.9f, 0.01f));
        Check("14/15 direct stat factor x2 and offset +50 preserved", Near(Row("14 direct CarryingCapacity factor x2")[NOW], 150f, 0.005f) && Near(Row("15 direct CarryingCapacity offset +50")[NOW], 125f, 0.005f));

        bool weakComp = true, weakMono = true;
        for (int i = 0; i < weakKeys.Count; i++)
        {
            weakComp &= Math.Abs(Row(weakKeys[i])[COMP] - 1f) < 1e-6f;
            if (i > 0) weakMono &= Row(weakKeys[i])[NOW] <= Row(weakKeys[i - 1])[NOW] + 1e-4f;
        }
        Check("weak series: compensation exactly 1 for every limb level <= 100%", weakComp);
        Check("weak series: carry is monotonic (100% >= 85% >= ... >= 0%)", weakMono);
        Check("weak series: 1% arms carry < 1 kg (no cliff: 1% ~ 70 kg / 0% = 0 is gone)", Row("W arms 1%")[NOW] < 1f && Row("W arms 0%")[NOW] < 0.001f);
        Check("weak series: 50% arms = 75 x 0.5 x 0.95 ~ 35.6 kg", Near(Row("W arms 50%")[NOW], 35.6f, 0.005f));
        Check("weak series: every step is at or below the vanilla value (disability never compensated away)", weakKeys.TrueForAll(k => Row(k)[NOW] <= Row(k)[VAN] + 1e-3f));

        bool strongMono = true, strongExact = true;
        for (int i = 1; i < strongKeys.Count; i++)
        {
            strongMono &= Row(strongKeys[i])[NOW] > Row(strongKeys[i - 1])[NOW];
            strongExact &= Row(strongKeys[i])[EXACT] == 1f && Row(strongKeys[i])[COMP] < 1f;
        }
        Check("superhuman series: monotonic increasing 100% < 125% < 150% < 300% < 500%", strongMono);
        Check("superhuman series: compensation active and exact (neutral-limb level) for every level > 100%", strongExact);
        Check("superhuman series: 100% arms -> compensation exactly 1", Math.Abs(Row("S arms 100%")[COMP] - 1f) < 1e-6f);
        Check("superhuman series: no double count (300% arms = 75 x LS, far below vanilla x LS)", Row("S arms 300%")[NOW] < 0.4f * Row("S arms 300%")[VAN] * Row("S arms 300%")[LSV]);

        Check("prosthetics < 100% keep the vanilla penalty (compensation 1): one/two simple, poor 60%, archotech+missing",
            new[] { "P one simple prosthetic arm (85%)", "P two simple prosthetic arms (85%)", "P poor modded prosthetic arms (60%)", "P archotech arm + missing arm (75%)" }
                .All(k => Math.Abs(Row(k)[COMP] - 1f) < 1e-6f && Near(Row(k)[NOW], Row(k)[VAN] * Row(k)[LSV], 0.001f)));
        Check("prosthetics > 100% activate the compensation: bionic, archotech, 300%, 500%",
            new[] { "P bionic arms (125%)", "P archotech arms (150%)", "P modded arms (300%)", "P modded arms (500%)" }.All(k => Row(k)[COMP] < 1f && Row(k)[EXACT] == 1f));

        foreach (float off in new[] { 0.5f, -0.25f })
        {
            var ks = capKeys.FindAll(kv => kv.Value == off).ConvertAll(kv => kv.Key);
            bool same = ks.TrueForAll(k => Near(Row(k)[NOW] / Row(k)[LSV], 75f * (1f + off), 0.002f));
            Check("additive Manip " + (off * 100f).ToString("+0;-0") + "%: systemic x" + (1f + off).ToString("0.00") + " identical at every limb level (" + string.Join(", ", ks.ConvertAll(k => (Row(k)[NOW] / Row(k)[LSV]).ToString("0.0")).ToArray()) + " kg before LS)", same);
        }
        Check("additive: bionic arms + Manip +50% = 75 x 1.5 x LS ~ 117 kg (PR#1 first revision gave ~109)", Near(Row("M arms 125% + Manip +50%")[NOW], 117.1f, 0.005f));
        Check("consciousness 50% + bionic arms: neutral-limb level is 0.50, not 1.0 (x0.5 kept once)", Math.Abs(Row("K consciousness 50% + bionic arms")[Y1_] - 0.5f) < 1e-4f && Near(Row("K consciousness 50% + bionic arms")[NOW], 37.5f * Row("K consciousness 50% + bionic arms")[LSV], 0.002f));
        Check("consciousness 50% + 300% arms: 75 x 0.5 x LS", Exact("K consciousness 50% + 300% arms"));

        // ---- Custom / patched Manipulation worker (another mod) ----
        Console.WriteLine("\n=== Patched Manipulation worker (another mod's Harmony postfix) ===");
        MethodInfo manipWorker = AccessTools.Method(typeof(PawnCapacityWorker_Manipulation), "CalculateCapacityLevel");
        MethodInfo moddedPostfix = typeof(IntegrationTests).GetMethod("ModdedManipulationWorker", BindingFlags.Static | BindingFlags.NonPublic);
        harness.Patch(manipWorker, postfix: new HarmonyMethod(moddedPostfix));
        header();
        ModdedWorkerScale = 1f;
        p = ArmsAt("H1", 1.25f); Pipe("H result-preserving patch + bionic arms", p, 75f);
        p = ArmsAt("H1b", 3f); WholeBodyCapMod(p, PawnCapacityDefOf.Manipulation, 0.5f); Pipe("H result-preserving patch + 300% arms + Manip +50%", p, 75f * 1.5f);
        ModdedWorkerScale = 1.2f;
        p = ArmsAt("H2", 3f); Pipe("H worker x1.2 + 300% arms (fallback Y/X)", p, 75f * 1.2f);
        p = Human("H3"); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0]); Pipe("H worker x1.2 + missing arm (weak: vanilla kept)", p, 75f * 0.6f);
        p = ArmsAt("H4", 0.6f); Pipe("H worker x1.2 + 60% arms (weak: vanilla kept)", p, 75f * GenMath.RoundedHundredth(0.72f));
        ModdedWorkerScale = 1f;
        harness.Unpatch(manipWorker, moddedPostfix);
        Check("result-preserving patch on the vanilla worker: exact path still used", Row("H result-preserving patch + bionic arms")[EXACT] == 1f && Row("H result-preserving patch + 300% arms + Manip +50%")[EXACT] == 1f && Exact("H result-preserving patch + 300% arms + Manip +50%"));
        Check("math-changing worker: detected, ratio fallback used, result = 75 x (Y/X) x LS", Row("H worker x1.2 + 300% arms (fallback Y/X)")[EXACT] == 0f && Exact("H worker x1.2 + 300% arms (fallback Y/X)"));
        Check("math-changing worker: fallback never compensates weak limbs", Math.Abs(Row("H worker x1.2 + missing arm (weak: vanilla kept)")[COMP] - 1f) < 1e-6f && Math.Abs(Row("H worker x1.2 + 60% arms (weak: vanilla kept)")[COMP] - 1f) < 1e-6f
              && Exact("H worker x1.2 + missing arm (weak: vanilla kept)") && Exact("H worker x1.2 + 60% arms (weak: vanilla kept)"));

        // ---- Different Manipulation factor configurations (compensation follows the runtime StatDef) ----
        Console.WriteLine("\n=== Runtime factor configurations ===");
        Pawn arms125 = Human("CfgBionicArms"); InstallAll(arms125, BodyPartTagDefOf.ManipulationLimbCore, bionicArm);
        Pawn oneArm = Human("CfgOneArm"); RemovePart(oneArm, Parts(oneArm, BodyPartTagDefOf.ManipulationLimbCore)[0]);
        Func<Pawn, float> expectLS = x => 75f * LoadSupportCache.GetResult(x, true).LoadSupport;
        Func<Pawn, float> expectVanillaLS = x => CarryVanilla(x) * LoadSupportCache.GetResult(x, true).LoadSupport;
        Action<string> cfgRow = label =>
        {
            float a = Carry(arms125), av = CarryVanilla(arms125), b = Carry(oneArm), bv = CarryVanilla(oneArm);
            Console.WriteLine(string.Format("  {0,-44} bionic arms: vanilla {1,6:0.0} -> {2,6:0.0} (exp {3,6:0.0})   one arm: vanilla {4,6:0.0} -> {5,6:0.0} (exp {6,6:0.0})",
                label, av, a, expectLS(arms125), bv, b, expectVanillaLS(oneArm)));
        };
        Func<bool> cfgOk = () => Near(Carry(arms125), expectLS(arms125), 0.002f) && Near(Carry(oneArm), expectVanillaLS(oneArm), 0.002f);
        cfgRow("weight 1 (vanilla)");
        manipFactor.weight = 0.5f; cfgRow("weight 0.5");
        Check("weight 0.5: superhuman share removed exactly; weak arm keeps vanilla's partial factor", cfgOk());
        manipFactor.weight = 1f; manipFactor.max = 1f; cfgRow("max 1.0 (bionic bonus capped by vanilla)");
        Check("max 1.0: capped factor handled exactly", cfgOk());
        manipFactor.max = 9999f; manipFactor.allowedDefect = 0.2f; cfgRow("allowedDefect 0.2");
        Check("allowedDefect 0.2: InverseLerp defect curve handled exactly", cfgOk());
        manipFactor.allowedDefect = 0f; manipFactor.useReciprocal = true; cfgRow("useReciprocal (odd, but must stay exact)");
        Check("useReciprocal: handled exactly", cfgOk());
        manipFactor.useReciprocal = false;
        carryStat.capacityFactors = new List<PawnCapacityFactor>(); cfgRow("no Manipulation factor at all");
        Check("no Manipulation factor: compensation is exactly 1 and result = 75 x LS",
            Math.Abs(ManipulationCompensation.Compute(carryStat, arms125, LoadSupportCache.GetResult(arms125)) - 1f) < 1e-6f && Near(Carry(arms125), expectLS(arms125), 0.002f));
        var movingFactor = new PawnCapacityFactor(); movingFactor.capacity = PawnCapacityDefOf.Moving; movingFactor.weight = 1f;
        carryStat.capacityFactors = new List<PawnCapacityFactor> { movingFactor, manipFactor };
        Pawn legs125 = Human("CfgBionicLegs"); InstallAll(legs125, BodyPartTagDefOf.MovingLimbCore, bionicLeg);
        float movingLevel = legs125.health.capacities.GetLevel(PawnCapacityDefOf.Moving);
        Console.WriteLine("  extra (non-Manipulation) Moving factor added by 'another mod': bionic legs Moving level " + movingLevel.ToString("0.00")
                          + ", carry " + Carry(legs125).ToString("0.0") + " (exp " + (movingLevel * expectLS(legs125)).ToString("0.0") + ")");
        Check("a non-Manipulation capacity factor from another mod is preserved (not compensated)", Near(Carry(legs125), movingLevel * expectLS(legs125), 0.005f));
        carryStat.capacityFactors = new List<PawnCapacityFactor> { manipFactor };

        // ---- Other mods' StatParts running BEFORE Parametric's ----
        Console.WriteLine("\n=== Other mods' StatParts inserted BEFORE StatPart_LoadSupport ===");
        var spPawns = new List<KeyValuePair<string, Pawn>>();
        spPawns.Add(new KeyValuePair<string, Pawn>("healthy", Human("SP1")));
        p = Human("SP2"); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0]); spPawns.Add(new KeyValuePair<string, Pawn>("missing one arm", p));
        spPawns.Add(new KeyValuePair<string, Pawn>("bionic arms", ArmsAt("SP3", 1.25f)));
        spPawns.Add(new KeyValuePair<string, Pawn>("300% arms", ArmsAt("SP4", 3f)));
        spPawns.Add(new KeyValuePair<string, Pawn>("300% full body", full3));
        var baseline = new Dictionary<string, float>();
        foreach (var kv in spPawns) baseline[kv.Key] = Carry(kv.Value);
        int ourIndex = carryStat.parts.FindIndex(x => x is StatPart_LoadSupport);

        var mulPart = new TestStatPart { parentStat = carryStat, factor = 2f };
        carryStat.parts.Insert(ourIndex, mulPart);
        bool mulOk = true;
        foreach (var kv in spPawns)
        {
            float v = Carry(kv.Value);
            Console.WriteLine(string.Format("  x2 part    {0,-18} {1,8:0.0} kg  (without part {2,8:0.0}, exp x2 {3,8:0.0})", kv.Key, v, baseline[kv.Key], 2f * baseline[kv.Key]));
            mulOk &= Near(v, 2f * baseline[kv.Key], 0.001f);
        }
        carryStat.parts.Remove(mulPart);
        Check("multiplicative StatPart before Parametric: exactly x2 for every pawn (multiplication commutes)", mulOk);

        var addPart = new TestStatPart { parentStat = carryStat, add = 100f };
        carryStat.parts.Insert(ourIndex, addPart);
        bool addWeakOk = true;
        float addErr300 = 0f, addErrFull = 0f;
        foreach (var kv in spPawns)
        {
            LoadSupportResult r = LoadSupportCache.GetResult(kv.Value, true);
            float cmp = ManipulationCompensation.Compute(carryStat, kv.Value, r);
            float v = Carry(kv.Value);
            float vanillaComposed = CarryVanilla(kv.Value) - 100f;               // vanilla CarryingCapacity before the +100 part
            float ideal = (vanillaComposed * cmp + 100f) * r.LoadSupport;         // +100 kg compensated neither way, then scaled by LS
            float predicted = (vanillaComposed + 100f) * cmp * r.LoadSupport;     // late multiplicative correction
            Console.WriteLine(string.Format("  +100 part  {0,-18} {1,8:0.0} kg  comp {2,5:0.000}  LS {3,6:0.000}  | ideal {4,8:0.0}  predicted {5,8:0.0}  | +100 kg counts as {6,6:0.0} kg before LS",
                kv.Key, v, cmp, r.LoadSupport, ideal, predicted, 100f * cmp));
            if (cmp == 1f) addWeakOk &= Near(v, ideal, 0.001f);
            if (kv.Key == "300% arms") addErr300 = v - ideal;
            if (kv.Key == "300% full body") addErrFull = v - ideal;
            Check("additive StatPart (" + kv.Key + "): result = (vanilla + 100) x comp x LS, as documented", Near(v, predicted, 0.001f));
        }
        carryStat.parts.Remove(addPart);
        Console.WriteLine("  => difference vs ideal: 300% arms " + addErr300.ToString("0.0") + " kg, 300% full body " + addErrFull.ToString("0.0") + " kg");
        Check("additive StatPart: pawns with limbs <= 100% are unaffected by the limitation (exact)", addWeakOk);

        // ---- MassUtility is untouched by Manipulation compensation ----
        Console.WriteLine("\n=== MassUtility has no Manipulation factor: never compensated ===");
        foreach (var kv in spPawns)
        {
            float m = MassUtility.Capacity(kv.Value, null), ls = LoadSupportCache.Get(kv.Value);
            Console.WriteLine(string.Format("  {0,-18} mass {1,7:0.0} kg = 35 x LS {2,6:0.000}", kv.Key, m, ls));
            Check("mass = 35 x LS (no compensation): " + kv.Key, Math.Abs(m - 35f * ls) < 1e-3f * Math.Max(1f, m));
        }

        // ================= MassUtility.Capacity =================
        Console.WriteLine("\n=== MassUtility.Capacity (caravan / inventory) through the real patched method ===");
        p = Human("MassA"); InstallAll(p, BodyPartTagDefOf.MovingLimbCore, bionicLeg); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, bionicArm);
        float massOn = MassUtility.Capacity(p, null);
        ParametricMod.Settings.applyToMassCapacity = false; float massOff = MassUtility.Capacity(p, null); ParametricMod.Settings.applyToMassCapacity = true;
        Console.WriteLine("  bionic legs+arms: vanilla " + massOff.ToString("0.00") + " kg -> " + massOn.ToString("0.00") + " kg (LS " + LoadSupportCache.Get(p).ToString("0.000") + ")");
        Check("vanilla MassUtility base = 35 kg at body size 1", Math.Abs(massOff - 35f) < 1e-3f);
        Check("mass = BodySize x 35 x LS, with NO Manipulation compensation", Math.Abs(massOn - 35f * LoadSupportCache.Get(p)) < 1e-3f);
        Pawn big = Human("MassBig", 1.5f);
        Check("body size 1.5 healthy: 52.5 kg", Math.Abs(MassUtility.Capacity(big, null) - 52.5f) < 1e-3f);
        ParametricMod.Settings.loadSupportEnabled = false;
        Check("module switch off -> mass vanilla", Math.Abs(MassUtility.Capacity(p, null) - 35f) < 1e-3f);
        Check("module switch off -> CarryingCapacity pipeline vanilla (incl. vanilla Manipulation x1.25)", Math.Abs(Carry(p) - CarryVanilla(p)) < 1e-3f && Math.Abs(Carry(p) - 93.75f) < 0.01f);
        ParametricMod.Settings.loadSupportEnabled = true;
        ParametricMod.Settings.includeNonHumanlike = false;
        Pawn beast = MakePawn("MuffaloNoLeg", QuadrupedBody(), Intelligence.Animal, true); RemovePart(beast, Parts(beast, BodyPartTagDefOf.MovingLimbCore)[0]);
        Check("non-humanlike toggle off -> animal untouched", Math.Abs(MassUtility.Capacity(beast, null) - 35f) < 1e-3f);
        ParametricMod.Settings.includeNonHumanlike = true;
        Check("non-humanlike toggle on -> animal reduced", MassUtility.Capacity(beast, null) < 35f);
        Check("animal (no arms): CarryingCapacity compensation not applied", Math.Abs(ManipulationCompensation.Compute(carryStat, beast, LoadSupportCache.GetResult(beast)) - 1f) < 1e-6f);

        // ================= Caravan / mass-capacity chain: Load Support exactly once per pawn =================
        // Vanilla 1.6: CollectionsMassCalculator.Capacity sums MassUtility.Capacity(pawn) once per pawn; the caravan dialog's
        // per-pawn "+X kg" (TransferableOneWayWidget.DrawMass) is MassUtility.Capacity(pawn, null) - gear - inventory.
        // The patterns below are TEST-ONLY stand-ins for other mods' hooks (owner "thirdparty.masstest").
        Console.WriteLine("\n=== Caravan / mass-capacity chain: Load Support exactly once per pawn ===");
        var third = new Harmony(ThirdPartyId);
        MethodInfo massCap = AccessTools.Method(typeof(MassUtility), nameof(MassUtility.Capacity), new[] { typeof(Pawn), typeof(System.Text.StringBuilder) });
        Pawn duster = Human("Duster"); duster.stackCount = 1; duster.Name = new NameSingle("Duster");
        InstallAll(duster, BodyPartTagDefOf.MovingLimbCore, mod3Leg); InstallAll(duster, BodyPartTagDefOf.ManipulationLimbCore, mod3Arm);
        Install(duster, Parts(duster, BodyPartTagDefOf.Spine)[0], mod3Spine); Install(duster, Parts(duster, BodyPartTagDefOf.Pelvis)[0], mod3Pelvis); PartOffset(duster, duster.RaceProps.body.corePart, 2f);
        Pawn mate = Human("Mate"); mate.stackCount = 1; mate.Name = new NameSingle("Mate"); InstallAll(mate, BodyPartTagDefOf.MovingLimbCore, bionicLeg);
        float L = LoadSupportCache.GetResult(duster, true).LoadSupport, Lm = LoadSupportCache.GetResult(mate, true).LoadSupport;
        const float B = 35f, STR = 1.04f; // vanilla base and the "other mod's" strength factor
        Func<Pawn, float> caravanCap = x => CollectionsMassCalculator.Capacity(new List<ThingCount> { new ThingCount(x, 1) }, null);
        Func<Pawn, float> caravanCapExpl = x => CollectionsMassCalculator.Capacity(new List<ThingCount> { new ThingCount(x, 1) }, new System.Text.StringBuilder());
        Func<Pawn, float> infoCard = x => FakeMassStat(x);                    // the other mod's "mass carry capacity" stat
        Func<Pawn, float> dialogPlus = x => MassUtility.Capacity(x, null) - MassUtility.GearMass(x); // DrawMass (no inventory here)
        Action<string, float> massRow = (label, expected) =>
            Console.WriteLine(string.Format("  {0,-62} Capacity {1,9:0.0}  caravan {2,9:0.0}  caravan+expl {3,9:0.0}  +X {4,9:0.0}  expected {5,9:0.0}",
                label, MassUtility.Capacity(duster, null), caravanCap(duster), caravanCapExpl(duster), dialogPlus(duster), expected));
        Func<float, bool> allEqual = expected => Near(MassUtility.Capacity(duster, null), expected, 0.001f) && Near(caravanCap(duster), expected, 0.001f)
                                                 && Near(caravanCapExpl(duster), expected, 0.001f) && Near(dialogPlus(duster), expected, 0.001f);
        Console.WriteLine("  Duster stand-in: 300% body, Load Support " + L.ToString("0.000") + "; other mod's strength factor x" + STR);

        Check("Parametric registers exactly one prefix, one postfix and one finalizer on MassUtility.Capacity",
            Harmony.GetPatchInfo(massCap).Prefixes.Count(x => x.owner == ParametricMod.HarmonyId) == 1
            && Harmony.GetPatchInfo(massCap).Postfixes.Count(x => x.owner == ParametricMod.HarmonyId) == 1
            && Harmony.GetPatchInfo(massCap).Finalizers.Count(x => x.owner == ParametricMod.HarmonyId) == 1);

        massRow("vanilla chain only", B * L);
        Check("vanilla chain: Capacity = caravan = caravan with explanation = dialog +X = 35 x LS", allEqual(B * L));

        // Pattern 0: a plain multiplicative postfix (e.g. a strength stat applied to mass capacity) - composes, one LS.
        third.Patch(massCap, postfix: new HarmonyMethod(typeof(IntegrationTests).GetMethod("ThirdPartyFactorPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
        massRow("P0 plain x1.04 postfix", B * STR * L);
        Check("P0 plain multiplicative postfix: 35 x 1.04 x LS everywhere", allEqual(B * STR * L));
        third.UnpatchAll(ThirdPartyId);

        // Pattern 1: a prefix that replaces mass capacity with a stat whose worker reads the (patched) method again,
        // guarded against its own recursion. This is the re-entrant shape that doubles Load Support.
        third.Patch(massCap, prefix: new HarmonyMethod(typeof(IntegrationTests).GetMethod("ThirdPartyStatPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
        Patch_MassUtility_Capacity.NestingGuardEnabled = false;
        float p1Old = MassUtility.Capacity(duster, null), p1OldCaravan = caravanCap(duster), p1OldCard = infoCard(duster);
        Console.WriteLine(string.Format("  {0,-62} Capacity {1,9:0.0}  caravan {2,9:0.0}  info card {3,9:0.0}   <- reproduces the report (ratio x{4:0.00} = LS)",
            "P1 stat-backed prefix, nesting guard OFF (pre-fix)", p1Old, p1OldCaravan, p1OldCard, p1Old / p1OldCard));
        Check("P1 reproduced without the guard: info card 35 x 1.04 x LS, caravan 35 x 1.04 x LS x LS",
            Near(p1OldCard, B * STR * L, 0.001f) && Near(p1OldCaravan, B * STR * L * L, 0.001f) && Near(p1OldCaravan / p1OldCard, L, 0.001f));
        Patch_MassUtility_Capacity.NestingGuardEnabled = true;
        massRow("P1 stat-backed prefix (guard ON)", B * STR * L);
        Check("P1 stat-backed prefix: Load Support exactly once everywhere (35 x 1.04 x LS), info card agrees", allEqual(B * STR * L) && Near(infoCard(duster), B * STR * L, 0.001f));

        // Trace of the same call: proves depth 2 inner scaled, depth 1 outer skipped.
        var traced = new List<string>();
        int logMark = logLines.Count;
        MassCapacityTrace.Sink = traced.Add;
        MassCapacityTrace.Arm(duster);
        MassUtility.Capacity(duster, null);
        MassCapacityTrace.Disarm("test");
        MassCapacityTrace.Sink = null;
        string traceText = string.Join("\n", traced.ToArray());
        Console.WriteLine("  --- trace excerpt (guard ON) ---");
        foreach (string line in traceText.Split('\n'))
            if (line.StartsWith("Depth") || line.StartsWith("Decision") || line.StartsWith("Incoming") || line.StartsWith("Outgoing") || line.Contains("[MassTrace] #") || line.Contains("WARNING"))
                Console.WriteLine("    " + line);
        Check("trace: patch report lists both owners on MassUtility.Capacity", traced.Count > 0 && traced[0].Contains(ParametricMod.HarmonyId) && traced[0].Contains(ThirdPartyId) && traced[0].Contains("Finalizers"));
        Check("trace: inner call (depth 2, same pawn nested) Scaled, outer call (depth 1) AlreadyScaledInside",
            traceText.Contains("Depth: 2 (same-pawn enclosing calls: 1)") && traceText.Contains("Decision: Scaled") && traceText.Contains("Decision: AlreadyScaledInside"));
        Check("trace: outer call flagged 'incoming already appears to contain Load Support'", traceText.Contains("WARNING: the incoming value already appears to contain Load Support"));
        Check("trace: call stack names the third-party patch", traceText.Contains("ThirdPartyStatPrefix"));
        Check("trace: disarms itself; nothing traced afterwards", !MassCapacityTrace.Armed);
        third.UnpatchAll(ThirdPartyId);

        // Pattern 2/3: same stat-backed replacement done in a POSTFIX, before and after Parametric's postfix.
        third.Patch(massCap, postfix: new HarmonyMethod(typeof(IntegrationTests).GetMethod("ThirdPartyStatPostfix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.High });
        massRow("P2 stat-backed postfix, runs BEFORE Parametric", B * STR * L);
        Check("P2 stat-backed postfix before ours: Load Support exactly once", allEqual(B * STR * L));
        third.UnpatchAll(ThirdPartyId);
        third.Patch(massCap, postfix: new HarmonyMethod(typeof(IntegrationTests).GetMethod("ThirdPartyStatPostfix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last, after = new[] { ParametricMod.HarmonyId } });
        massRow("P3 stat-backed postfix, runs AFTER Parametric", B * STR * L);
        Check("P3 stat-backed postfix after ours: Load Support exactly once (not lost, not doubled)", allEqual(B * STR * L));
        third.UnpatchAll(ThirdPartyId);

        // Pattern 4: a nested call for ANOTHER pawn is independent and still gets that pawn's Load Support.
        CrossPawnPartner = mate; CrossPawnOwner = duster;
        third.Patch(massCap, postfix: new HarmonyMethod(typeof(IntegrationTests).GetMethod("ThirdPartyCrossPawnPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
        float p4 = MassUtility.Capacity(duster, null), p4exp = (B + B * Lm) * L;
        Console.WriteLine(string.Format("  {0,-62} Capacity {1,9:0.0}  expected (35 + 35 x LSmate) x LS {2,9:0.0}; mate alone {3,7:0.0}", "P4 nested call for a different pawn (mate added)", p4, p4exp, MassUtility.Capacity(mate, null)));
        Check("P4 nested call for a different pawn: that pawn is scaled by its own Load Support (not suppressed)", Near(p4, p4exp, 0.001f) && Near(MassUtility.Capacity(mate, null), B * Lm, 0.001f));
        third.UnpatchAll(ThirdPartyId);
        CrossPawnPartner = CrossPawnOwner = null;

        // Pattern 5: an exception thrown by another patch inside a nested call must not leave the guard stack dirty.
        third.Patch(massCap, prefix: new HarmonyMethod(typeof(IntegrationTests).GetMethod("ThirdPartyThrowingPrefix", BindingFlags.Static | BindingFlags.NonPublic)));
        ThrowNext = true;
        bool threw = false;
        try { MassUtility.Capacity(duster, null); } catch (Exception) { threw = true; }
        third.UnpatchAll(ThirdPartyId);
        Check("P5 exception inside the patched method: propagates, depth returns to 0, next call is 35 x LS", threw && Patch_MassUtility_Capacity.Depth == 0 && Near(MassUtility.Capacity(duster, null), B * L, 0.001f));

        // Pattern 6 (NOT fixable here, documented): a mod that stores an already-scaled result and feeds it back in a
        // LATER, non-nested call. No nesting exists, so the guard cannot see it; the trace flags it instead.
        third.Patch(massCap, prefix: new HarmonyMethod(typeof(IntegrationTests).GetMethod("ThirdPartyCachingPrefix", BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(IntegrationTests).GetMethod("ThirdPartyCachingPostfix", BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last, after = new[] { ParametricMod.HarmonyId } });
        StoredMass.Clear();
        traced.Clear(); MassCapacityTrace.Sink = traced.Add; MassCapacityTrace.Arm(duster);
        float first = MassUtility.Capacity(duster, null);  // 35 x LS, stored by the "other mod"
        float second = MassUtility.Capacity(duster, null); // stored value fed back in -> x LS again
        MassCapacityTrace.Disarm("test"); MassCapacityTrace.Sink = null;
        third.UnpatchAll(ThirdPartyId);
        Console.WriteLine(string.Format("  {0,-62} first {1,9:0.0}  second {2,9:0.0} (x{3:0.00})", "P6 stored-value re-feed (limitation)", first, second, second / first));
        foreach (string line in string.Join("\n", traced.ToArray()).Split('\n')) if (line.Contains("WARNING")) Console.WriteLine("    trace:" + line);
        Check("P6 stored-value re-feed: trace flags the incoming value as Parametric's previous output / already containing LS",
            string.Join("\n", traced.ToArray()).Contains("WARNING: incoming equals Parametric's previous OUTPUT") || string.Join("\n", traced.ToArray()).Contains("WARNING: the incoming value already appears to contain Load Support"));
        logLines.RemoveRange(logMark, logLines.Count - logMark); // trace output is expected here, not an error

        Check("all third-party test patches removed; Parametric's still present", !Harmony.GetPatchInfo(massCap).Owners.Contains(ThirdPartyId) && Harmony.GetPatchInfo(massCap).Owners.Contains(ParametricMod.HarmonyId));
        Check("after all patterns: vanilla chain back to 35 x LS, guard depth 0", allEqual(B * L) && Patch_MassUtility_Capacity.Depth == 0);

        // ================= Cache =================
        Console.WriteLine("\n=== Cache: dirty events, time expiry, settings generation, clock rollback, weak keys ===");
        ticks.SetValue(tm, 10000);
        Check("refresh interval is 1000 ticks", LoadSupportCache.RefreshIntervalTicks == 1000);

        Pawn c = Human("CacheTest");
        float c0 = LoadSupportCache.Get(c);
        foreach (var l in Parts(c, BodyPartTagDefOf.MovingLimbCore)) AddHediff<Hediff_AddedPart>(c, bionicLeg, l, 1f, notify: false); // silent change
        ticks.SetValue(tm, 10999);
        Check("silent change, within interval -> cached value reused", Math.Abs(LoadSupportCache.Get(c) - c0) < 1e-6f);
        ticks.SetValue(tm, 11000);
        float c2 = LoadSupportCache.Get(c);
        Check("after 1000 ticks -> recomputed (picks up silent bionic legs)", c2 > c0 + 0.2f);
        RemovePart(c, Parts(c, BodyPartTagDefOf.ManipulationLimbCore)[0]); // real HediffSet.DirtyCache() → postfix
        float c3 = LoadSupportCache.Get(c);
        Check("REAL HediffSet.DirtyCache() -> immediate recompute within the interval", c3 < c2);
        ticks.SetValue(tm, 500); // loaded an earlier save
        c.health.hediffSet.hediffs.Clear();
        Check("clock rollback (load) -> recompute", Math.Abs(LoadSupportCache.Get(c) - 1f) < 1e-5f);
        foreach (var l in Parts(c, BodyPartTagDefOf.MovingLimbCore)) AddHediff<Hediff_AddedPart>(c, bionicLeg, l, 1f, notify: false);
        float before = LoadSupportCache.Get(c);
        ParametricMod.Settings.superhumanExponent = 1.0f;
        LoadSupportCache.InvalidateAll();
        float after = LoadSupportCache.Get(c);
        Console.WriteLine("  exponent 2.5 -> 1.0 with silent bionic legs: " + before.ToString("0.000") + " -> " + after.ToString("0.000"));
        Check("settings generation (InvalidateAll) -> recompute", Math.Abs(after - before) > 1e-4f);
        ParametricMod.Settings.superhumanExponent = LoadSupportFormula.DefaultExponent;
        LoadSupportCache.InvalidateAll();

        WeakReference wr = MakeAndMeasureThrowaway();
        for (int i = 0; i < 5 && wr.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Check("weak keys: cache does not keep a discarded pawn alive", !wr.IsAlive);

        LoadSupportCache.Get(c);
        long a0 = GC.GetTotalMemory(false);
        float sink = 0; for (int i = 0; i < 100000; i++) sink += LoadSupportCache.Get(c);
        long a1 = GC.GetTotalMemory(false);
        Check("cached lookups allocate nothing measurable (" + (a1 - a0) + " bytes / 100k)", a1 - a0 < 16 * 1024);

        // ================= Benchmarks =================
        Console.WriteLine("\n=== Benchmarks (Mono JIT, outside Unity; in-game numbers will differ) ===");
        Pawn t = Human("Timing"); InstallAll(t, BodyPartTagDefOf.MovingLimbCore, bionicLeg); InstallAll(t, BodyPartTagDefOf.ManipulationLimbCore, bionicArm);
        Bench("full body evaluation (uncached)", 20000, () => LoadSupportCalculator.Calculate(t));
        ticks.SetValue(tm, 20000);
        LoadSupportCache.Get(t);
        Bench("cached lookup", 2000000, () => LoadSupportCache.Get(t));
        LoadSupportResult tr = LoadSupportCache.GetResult(t);
        double comp = Bench("manipulation compensation, superhuman arms (exact path)", 2000000, () => ManipulationCompensation.Compute(carryStat, t, tr));
        Pawn tw = Human("TimingWeak"); RemovePart(tw, Parts(tw, BodyPartTagDefOf.ManipulationLimbCore)[0]);
        LoadSupportResult twr = LoadSupportCache.GetResult(tw);
        Bench("manipulation compensation, limbs <= 100% (early out)", 2000000, () => ManipulationCompensation.Compute(carryStat, tw, twr));
        Pawn tm3 = Human("TimingMods"); InstallAll(tm3, BodyPartTagDefOf.ManipulationLimbCore, mod3Arm);
        WholeBodyCapMod(tm3, PawnCapacityDefOf.Manipulation, 0.5f); WholeBodyCapMod(tm3, PawnCapacityDefOf.Moving, 0.2f); Injure(tm3, tm3.RaceProps.body.corePart, 5f);
        LoadSupportResult tm3r = LoadSupportCache.GetResult(tm3);
        Bench("manipulation compensation, 300% arms + 3 other hediffs", 2000000, () => ManipulationCompensation.Compute(carryStat, tm3, tm3r));
        Bench("whole StatPart.TransformValue (cached LS + compensation)", 1000000, () => { float v = 75f; carryStat.parts[1].TransformValue(StatRequest.For(t), ref v); });
        double pipeOn = Bench("GetStatValue(CarryingCapacity) with Parametric", 200000, () => Carry(t));
        double pipeOff = Bench("GetStatValue(CarryingCapacity) Parametric bypassed", 200000, () => CarryVanilla(t));
        Console.WriteLine("  => Parametric adds ~" + Math.Max(0, pipeOn - pipeOff).ToString("0.000") + " µs per CarryingCapacity evaluation; compensation itself ~" + comp.ToString("0.000") + " µs");
        MethodInfo massCapM = AccessTools.Method(typeof(MassUtility), nameof(MassUtility.Capacity), new[] { typeof(Pawn), typeof(System.Text.StringBuilder) });
        double massOnUs = Bench("MassUtility.Capacity with Parametric (prefix+postfix+finalizer)", 1000000, () => MassUtility.Capacity(t, null));
        Bench("  of which nesting guard alone (Prefix + Finalizer)", 5000000, () => { int st; Patch_MassUtility_Capacity.Prefix(t, out st); Patch_MassUtility_Capacity.Finalizer(st); });
        var parametricHarmony = new Harmony(ParametricMod.HarmonyId);
        parametricHarmony.Unpatch(massCapM, HarmonyPatchType.All, ParametricMod.HarmonyId);
        double massOffUs = Bench("MassUtility.Capacity vanilla (Parametric unpatched)", 1000000, () => MassUtility.Capacity(t, null));
        parametricHarmony.PatchAll(typeof(ParametricMod).Assembly);
        Console.WriteLine("  => Parametric adds ~" + Math.Max(0, massOnUs - massOffUs).ToString("0.000") + " µs per MassUtility.Capacity call");
        long m0 = GC.GetTotalMemory(false);
        for (int i = 0; i < 100000; i++) MassUtility.Capacity(t, null);
        long m1 = GC.GetTotalMemory(false);
        Check("MassUtility.Capacity with Parametric allocates nothing measurable (" + (m1 - m0) + " bytes / 100k)", m1 - m0 < 16 * 1024);
        Check("re-patched after benchmark: exactly one Parametric prefix/postfix/finalizer", Harmony.GetPatchInfo(massCapM).Postfixes.Count(x => x.owner == ParametricMod.HarmonyId) == 1
              && Harmony.GetPatchInfo(massCapM).Prefixes.Count(x => x.owner == ParametricMod.HarmonyId) == 1 && Harmony.GetPatchInfo(massCapM).Finalizers.Count(x => x.owner == ParametricMod.HarmonyId) == 1);

        Console.WriteLine("\n=== Log output captured during tests ===");
        var errors = logLines.FindAll(s => s.IndexOf("Parametric", StringComparison.Ordinal) >= 0 || s.IndexOf("rror", StringComparison.Ordinal) >= 0);
        if (logLines.Count == 0) Console.WriteLine("  (none)");
        foreach (string s in logLines) Console.WriteLine("  " + s.Split('\n')[0]);
        Check("no errors and no Parametric log lines during tests (debug logging off)", errors.Count == 0);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL " + checks + " INTEGRATION CHECKS PASSED" : failures + " of " + checks + " CHECKS FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference MakeAndMeasureThrowaway()
    {
        Pawn tmp = Human("Throwaway");
        LoadSupportCache.Get(tmp);
        return new WeakReference(tmp);
    }

    static double Bench(string label, int n, Action a)
    {
        for (int i = 0; i < Math.Min(n, 1000); i++) a();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < n; i++) a();
        sw.Stop();
        double us = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
        Console.WriteLine(string.Format("  {0,-58} {1,9:0.000} µs", label, us));
        return us;
    }
}
