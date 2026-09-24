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
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HarmonyLib;
using Parametric;
using Parametric.LoadSupport;
using RimWorld;
using Verse;
using OC = System.Reflection.Emit.OpCodes;

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
        Console.WriteLine("\n=== FULL CarryingCapacity pipeline: pawn.GetStatValue(CarryingCapacity) through the real StatWorker ===");
        Console.WriteLine(string.Format("  {0,-42} {1,7} {2,9} {3,8} {4,8} {5,9} {6,9} {7,9}", "case", "Manip", "vanilla", "LS", "comp", "OLD bug", "NEW", "expected"));
        var P = new Dictionary<string, float[]>();
        Func<string, Pawn, float, float[]> Pipe = (label, pawn, expectedNonDupBase) =>
        {
            LoadSupportResult r = LoadSupportCache.GetResult(pawn, true);
            float manip = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Manipulation);
            float vanilla = CarryVanilla(pawn);                   // real pipeline, Load Support bypassed
            float now = Carry(pawn);                              // real pipeline, Parametric active
            float cmp = ManipulationCompensation.Compute(carryStat, pawn, r);
            float oldBug = vanilla * r.LoadSupport;               // what v0.1.0 (Load Support) produced
            float expected = expectedNonDupBase * r.LoadSupport;  // non-duplicated base × Load Support
            Console.WriteLine(string.Format("  {0,-42} {1,7:0.00} {2,9:0.0} {3,8:0.000} {4,8:0.000} {5,9:0.0} {6,9:0.0} {7,9:0.0}",
                label, manip, vanilla, r.LoadSupport, cmp, oldBug, now, expected));
            var row = new[] { manip, vanilla, r.LoadSupport, cmp, oldBug, now, expected };
            P[label] = row;
            return row;
        };
        TestConsciousness = 1f;

        p = Human("C01"); Pipe("01 healthy human", p, 75f);
        p = Human("C02"); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0]); Pipe("02 missing one arm", p, 75f);
        p = Human("C03"); foreach (var a in Parts(p, BodyPartTagDefOf.ManipulationLimbCore)) Injure(p, a, 18f); Pipe("03 both arms compromised (shoulders 40% HP)", p, 75f);
        p = Human("C03b"); foreach (var a in Parts(p, BodyPartTagDefOf.ManipulationLimbCore)) RemovePart(p, a); Pipe("03b both arms missing (uncorrectable: 0)", p, 0f);
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
        p = Human("C13"); WholeBodyCapMod(p, PawnCapacityDefOf.Manipulation, 0.5f); Pipe("13 capMod Manipulation +50% (vanilla kept)", p, 75f * 1.5f);
        p = Human("C13b"); WholeBodyCapMod(p, PawnCapacityDefOf.Manipulation, 0.5f); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, bionicArm); Pipe("13b capMod Manip +50% AND bionic arms", p, 75f * 1.5f);
        TestConsciousness = 0.5f;
        p = Human("C13c"); Pipe("13c consciousness 50% (vanilla kept)", p, 75f * 0.5f);
        p = Human("C13d"); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, bionicArm); Pipe("13d consciousness 50% + bionic arms", p, 75f * 0.5f);
        TestConsciousness = 1f;
        p = Human("C14"); CarryStatModifier(p, 0f, 2f); Pipe("14 direct CarryingCapacity factor x2", p, 150f);
        p = Human("C14b"); CarryStatModifier(p, 0f, 2f); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, bionicArm); Pipe("14b factor x2 + bionic arms", p, 150f);
        p = Human("C15"); CarryStatModifier(p, 50f, 1f); Pipe("15 direct CarryingCapacity offset +50", p, 125f);
        p = Human("C15b"); CarryStatModifier(p, 50f, 1f); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0]); Pipe("15b offset +50 + missing arm", p, 125f);
        p = Human("C17", 1.5f); InstallAll(p, BodyPartTagDefOf.ManipulationLimbCore, bionicArm); Pipe("17 body size 1.5 (StatPart_BodySize) + bionic arms", p, 75f * 1.5f);

        Console.WriteLine("\n=== Pipeline assertions ===");
        Func<string, float[]> Row = k => P[k];
        const int LSV = 2, COMP = 3, OLD = 4, NOW = 5, EXP = 6;
        // Vanilla rounds capacity levels to 0.01, so a result can differ from the ideal by up to ~0.005/Manip (relative).
        foreach (var kv in P)
        {
            if (kv.Key.StartsWith("13b")) continue; // additive offset + non-100% limbs: documented approximation, checked below
            float tol = Math.Max(0.002f, kv.Value[0] > 0f ? 0.0051f / kv.Value[0] : 0f);
            Check("pipeline result == non-duplicated base x Load Support (tol " + (tol * 100f).ToString("0.0") + "%): " + kv.Key, Near(kv.Value[NOW], kv.Value[EXP], tol));
        }
        float[] r13b = Row("13b capMod Manip +50% AND bionic arms");
        Check("13b additive capMod + bionic arms: between the multiplicative (x1.4) and additive (x1.5) readings, and below the old double count",
            r13b[NOW] >= 75f * 1.4f * r13b[LSV] - 0.1f && r13b[NOW] <= 75f * 1.5f * r13b[LSV] + 0.1f && r13b[NOW] < r13b[OLD]);
        Check("03 both arms compromised: rounding-exact (compensation undoes vanilla's factor completely)", Near(Row("03 both arms compromised (shoulders 40% HP)")[NOW], Row("03 both arms compromised (shoulders 40% HP)")[EXP], 0.002f));
        Check("01 healthy = exactly 75", Math.Abs(Row("01 healthy human")[NOW] - 75f) < 0.01f);
        Check("02 missing one arm: no double punishment (new > vanilla x LS)", Row("02 missing one arm")[NOW] > Row("02 missing one arm")[OLD] * 1.5f);
        Check("05 two bionic arms: vanilla's x1.25 removed (new = 75 x LS, not 93.75 x LS)", Near(Row("05 two bionic arms")[NOW], 75f * Row("05 two bionic arms")[LSV], 0.002f) && Row("05 two bionic arms")[OLD] > Row("05 two bionic arms")[NOW] * 1.2f);
        Check("06 bionic legs only: no Manipulation change -> compensation exactly 1", Math.Abs(Row("06 two bionic legs only")[COMP] - 1f) < 1e-5f);
        Check("11 THE BUG: 300% full body = 75 x 15.59 ~ 1169 kg (was ~3508)", Near(Row("11 modded 300% full body")[NOW], 1169.1f, 0.01f) && Row("11 modded 300% full body")[OLD] > 3400f);
        Check("12 500% full body = 75 x 55.9 ~ 4193 kg (was ~5x more)", Near(Row("12 modded 500% full body")[NOW], 75f * 55.9f, 0.01f) && Row("12 modded 500% full body")[OLD] > 4.9f * Row("12 modded 500% full body")[NOW]);
        Check("13 whole-body Manipulation capMod: vanilla x1.5 preserved exactly once", Near(Row("13 capMod Manipulation +50% (vanilla kept)")[NOW], 112.5f, 0.005f));
        Check("13c consciousness 50%: vanilla x0.5 preserved (not erased by the correction)", Near(Row("13c consciousness 50% (vanilla kept)")[NOW], 37.5f, 0.005f));
        Check("14 stat factor x2 preserved", Near(Row("14 direct CarryingCapacity factor x2")[NOW], 150f, 0.005f));
        Check("15 stat offset +50 preserved", Near(Row("15 direct CarryingCapacity offset +50")[NOW], 125f, 0.005f));
        Check("03b both arms missing: vanilla 0 stands (uncorrectable, compensation 1)", Row("03b both arms missing (uncorrectable: 0)")[NOW] < 0.001f && Math.Abs(Row("03b both arms missing (uncorrectable: 0)")[COMP] - 1f) < 1e-6f);

        // ---- Different Manipulation factor configurations (compensation follows the runtime StatDef) ----
        Console.WriteLine("\n=== Runtime factor configurations ===");
        Pawn arms125 = Human("CfgBionicArms"); InstallAll(arms125, BodyPartTagDefOf.ManipulationLimbCore, bionicArm);
        Pawn oneArm = Human("CfgOneArm"); RemovePart(oneArm, Parts(oneArm, BodyPartTagDefOf.ManipulationLimbCore)[0]);
        Func<Pawn, float> expectLS = x => 75f * LoadSupportCache.GetResult(x, true).LoadSupport;
        Action<string> cfgRow = label =>
        {
            float a = Carry(arms125), av = CarryVanilla(arms125), b = Carry(oneArm), bv = CarryVanilla(oneArm);
            Console.WriteLine(string.Format("  {0,-44} bionic arms: vanilla {1,6:0.0} -> {2,6:0.0} (exp {3,6:0.0})   one arm: vanilla {4,6:0.0} -> {5,6:0.0} (exp {6,6:0.0})",
                label, av, a, expectLS(arms125), bv, b, expectLS(oneArm)));
        };
        cfgRow("weight 1 (vanilla)");
        manipFactor.weight = 0.5f; cfgRow("weight 0.5");
        Check("weight 0.5: vanilla's partial factor removed exactly", Near(Carry(arms125), expectLS(arms125), 0.002f) && Near(Carry(oneArm), expectLS(oneArm), 0.002f));
        manipFactor.weight = 1f; manipFactor.max = 1f; cfgRow("max 1.0 (bionic bonus capped by vanilla)");
        Check("max 1.0: capped factor handled exactly", Near(Carry(arms125), expectLS(arms125), 0.002f) && Near(Carry(oneArm), expectLS(oneArm), 0.002f));
        manipFactor.max = 9999f; manipFactor.allowedDefect = 0.2f; cfgRow("allowedDefect 0.2");
        Check("allowedDefect 0.2: InverseLerp defect curve handled exactly", Near(Carry(arms125), expectLS(arms125), 0.002f) && Near(Carry(oneArm), expectLS(oneArm), 0.002f));
        manipFactor.allowedDefect = 0f; manipFactor.useReciprocal = true; cfgRow("useReciprocal (odd, but must stay exact)");
        Check("useReciprocal: handled exactly", Near(Carry(arms125), expectLS(arms125), 0.002f) && Near(Carry(oneArm), expectLS(oneArm), 0.002f));
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
        double comp = Bench("manipulation compensation", 2000000, () => ManipulationCompensation.Compute(carryStat, t, tr));
        Bench("whole StatPart.TransformValue (cached LS + compensation)", 1000000, () => { float v = 75f; carryStat.parts[1].TransformValue(StatRequest.For(t), ref v); });
        double pipeOn = Bench("GetStatValue(CarryingCapacity) with Parametric", 200000, () => Carry(t));
        double pipeOff = Bench("GetStatValue(CarryingCapacity) Parametric bypassed", 200000, () => CarryVanilla(t));
        Console.WriteLine("  => Parametric adds ~" + Math.Max(0, pipeOn - pipeOff).ToString("0.000") + " µs per CarryingCapacity evaluation; compensation itself ~" + comp.ToString("0.000") + " µs");

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
