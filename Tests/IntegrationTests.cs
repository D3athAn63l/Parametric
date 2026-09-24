// Integration harness: runs the REAL mod DLL against the REAL RimWorld 1.6 Assembly-CSharp under Mono,
// outside Unity. Builds synthetic bodies/pawns from real Verse classes, applies the mod's real Harmony patches,
// and exercises BodyRegionAnalyzer (which calls vanilla PawnCapacityUtility), the cache, the StatPart and the
// MassUtility.Capacity postfix.
//
// Limitations: body layouts are hand-built approximations of the vanilla Human/quadruped BodyDefs (the Core
// XML is not loaded), and vanilla part efficiencies are typed in. What IS real: every RimWorld method called.
//
// Build/run: see Tests/run-tests.sh
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using HarmonyLib;
using LoadSupport;
using RimWorld;
using Verse;

static class IntegrationTests
{
    static int failures;
    static readonly List<string> logLines = new List<string>();

    // ---------------- Unity-free logging ----------------
    static bool LogPrefix(object __0) { logLines.Add(Convert.ToString(__0)); return false; }

    static void StubLogging(Harmony h)
    {
        var prefix = new HarmonyMethod(typeof(IntegrationTests).GetMethod("LogPrefix", BindingFlags.Static | BindingFlags.NonPublic));
        foreach (MethodInfo m in typeof(Log).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if ((m.Name == "Message" || m.Name == "Warning" || m.Name == "Error" || m.Name == "ErrorOnce" || m.Name == "WarningOnce")
                && m.GetParameters().Length >= 1)
                h.Patch(m, prefix);
        }
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

    static BodyDef BlobBody() // strange anatomy: one part, no tags at all
    {
        return MakeBody("TestBlob", Rec(PartDef("Blob", 50), 1f));
    }

    static BodyDef TentacleBody() // manipulation limbs only, no legs, no spine/pelvis
    {
        var core = PartDef("Mantle", 50);
        var tentacle = PartDef("Tentacle", 20, BodyPartTagDefOf.ManipulationLimbCore);
        return MakeBody("TestTentacled", Rec(core, 1f, Rec(tentacle, 0.1f), Rec(tentacle, 0.1f), Rec(tentacle, 0.1f)));
    }

    static LifeStageDef adultStage;

    static Pawn MakePawn(string name, BodyDef body, Intelligence intelligence, bool packAnimal = false)
    {
        var race = new RaceProperties();
        race.body = body;
        race.intelligence = intelligence;
        race.baseBodySize = 1f;
        race.baseHealthScale = 1f;
        race.packAnimal = packAnimal;
        var lsa = new LifeStageAge(); lsa.def = adultStage; lsa.minAge = 0f;
        race.lifeStageAges = new List<LifeStageAge> { lsa };

        var def = New<ThingDef>(); // ThingDef ctor touches graphics (Unity); fields are set explicitly below
        def.defName = name + "_Race"; def.label = name; def.race = race; def.category = ThingCategory.Pawn;
        def.thingClass = typeof(Pawn);

        Pawn p = New<Pawn>();
        p.def = def;
        p.thingIDNumber = Math.Abs(name.GetHashCode());
        var age = New<Pawn_AgeTracker>();
        typeof(Pawn_AgeTracker).GetField("pawn", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(age, p);
        age.lockedLifeStageIndex = 0;
        typeof(Pawn_AgeTracker).GetField("cachedLifeStageIndex", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(age, 0);
        p.ageTracker = age;
        p.health = new Pawn_HealthTracker(p);
        return p;
    }

    // ---------------- Hediff builders (inserted directly, bypassing AddHediff side effects) ----------------
    static List<BodyPartRecord> Parts(Pawn p, BodyPartTagDef tag) { return p.RaceProps.body.GetPartsWithTag(tag); }

    static T AddHediff<T>(Pawn p, HediffDef def, BodyPartRecord part, float severity = 1f) where T : Hediff
    {
        T h = New<T>();
        h.def = def; h.pawn = p;
        typeof(Hediff).GetField("part", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(h, part);
        typeof(Hediff).GetField("severityInt", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(h, severity);
        p.health.hediffSet.hediffs.Add(h);
        LoadSupportCache.MarkDirty(p);
        return h;
    }

    static HediffDef missingDef = MakeDef("MissingBodyPart", typeof(Hediff_MissingPart));
    static HediffDef MakeDef(string name, Type cls) { var d = new HediffDef(); d.defName = name; d.hediffClass = cls; d.maxSeverity = 99999f; return d; }
    static HediffDef Prosthetic(string name, float eff)
    {
        var d = MakeDef(name, typeof(Hediff_AddedPart));
        d.addedPartProps = new AddedBodyPartProps(); d.addedPartProps.partEfficiency = eff; d.addedPartProps.solid = true;
        return d;
    }

    static void RemovePart(Pawn p, BodyPartRecord part) { AddHediff<Hediff_MissingPart>(p, missingDef, part); }
    static void Install(Pawn p, BodyPartRecord part, HediffDef def) { AddHediff<Hediff_AddedPart>(p, def, part); }
    static void Injure(Pawn p, BodyPartRecord part, float damage)
    {
        var d = MakeDef("Cut", typeof(Hediff_Injury));
        AddHediff<Hediff_Injury>(p, d, part, damage);
    }
    static void WholeBodyCapMod(Pawn p, PawnCapacityDef cap, float offset, float postFactor = 1f)
    {
        var d = MakeDef("TestExoskeleton", typeof(Hediff));
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

    // ---------------- Reporting ----------------
    static void Check(string what, bool ok)
    {
        if (!ok) failures++;
        Console.WriteLine((ok ? "  ok:   " : "  FAIL: ") + what);
    }

    static LoadSupportResult Report(string label, Pawn p)
    {
        LoadSupportResult r = LoadSupportCache.GetResult(p, true);
        float mass = MassUtility.Capacity(p, null);
        float carry = 75f; new StatPart_LoadSupport().TransformValue(StatRequest.For(p), ref carry);
        Console.WriteLine(string.Format("{0,-44} L {1,5:0.00}->{2,6:0.00}  C {3,5:0.00}->{4,6:0.00}  U {5,5:0.00}->{6,6:0.00}  bneck {7,4:0.00}  LS {8,6:0.000}  stat75->{9,6:0.0}  mass {10,5:0.0}",
            label, r.LowerEfficiency, r.LowerStrength, r.CoreEfficiency, r.CoreStrength, r.UpperEfficiency, r.UpperStrength,
            r.BottleneckFactor, r.LoadSupport, carry, mass));
        return r;
    }

    static Pawn Human(string name) { return MakePawn(name, HumanBody(), Intelligence.Humanlike); }

    static void Main()
    {
        var harmony = new Harmony("loadsupport.tests");
        StubLogging(harmony);

        // DefOf fields the code under test reads.
        BodyPartTagDefOf.MovingLimbCore = Tag("MovingLimbCore");
        BodyPartTagDefOf.MovingLimbSegment = Tag("MovingLimbSegment");
        BodyPartTagDefOf.MovingLimbDigit = Tag("MovingLimbDigit");
        BodyPartTagDefOf.ManipulationLimbCore = Tag("ManipulationLimbCore");
        BodyPartTagDefOf.ManipulationLimbSegment = Tag("ManipulationLimbSegment");
        BodyPartTagDefOf.ManipulationLimbDigit = Tag("ManipulationLimbDigit");
        BodyPartTagDefOf.Spine = Tag("Spine");
        BodyPartTagDefOf.Pelvis = Tag("Pelvis");
        PawnCapacityDefOf.Moving = new PawnCapacityDef(); PawnCapacityDefOf.Moving.defName = "Moving";
        PawnCapacityDefOf.Manipulation = new PawnCapacityDef(); PawnCapacityDefOf.Manipulation.defName = "Manipulation";
        adultStage = new LifeStageDef(); adultStage.defName = "Adult"; adultStage.bodySizeFactor = 1f; adultStage.healthScaleFactor = 1f;
        adultStage.developmentalStage = DevelopmentalStage.Adult;

        LoadSupportMod.Settings = new LoadSupportSettings();

        // ---- Apply the mod's REAL Harmony patches ----
        Console.WriteLine("=== Harmony: applying mod patches to real 1.6 methods ===");
        var modHarmony = new Harmony(LoadSupportMod.HarmonyId);
        modHarmony.PatchAll(typeof(LoadSupportMod).Assembly);
        var patched = new List<string>();
        foreach (MethodBase m in Harmony.GetAllPatchedMethods())
        {
            Patches info = Harmony.GetPatchInfo(m);
            if (info.Owners.Contains(LoadSupportMod.HarmonyId))
                patched.Add(m.DeclaringType.FullName + "." + m.Name + "(" + string.Join(", ", Array.ConvertAll(m.GetParameters(), x => x.ParameterType.Name)) + ")");
        }
        patched.Sort();
        foreach (string s in patched) Console.WriteLine("  patched: " + s);
        Check("exactly 3 methods patched", patched.Count == 3);
        Check("MassUtility.Capacity(Pawn, StringBuilder) patched", patched.Exists(s => s.StartsWith("RimWorld.MassUtility.Capacity(Pawn, StringBuilder")));
        Check("HediffSet.DirtyCache() patched", patched.Exists(s => s.StartsWith("Verse.HediffSet.DirtyCache(")));
        Check("Pawn.GetInspectString() patched", patched.Exists(s => s.StartsWith("Verse.Pawn.GetInspectString(")));

        // ---- StatPart injection ----
        Console.WriteLine("\n=== StatPart injection ===");
        var stat = new StatDef(); stat.defName = "CarryingCapacity"; stat.defaultBaseValue = 75f; stat.workerClass = typeof(StatWorker);
        stat.parts = null; stat.immutable = true;
        StatPart_LoadSupport.InjectInto(stat);
        StatPart_LoadSupport.InjectInto(stat); // idempotent
        Check("part list created, exactly one LoadSupport part", stat.parts != null && stat.parts.Count == 1 && stat.parts[0] is StatPart_LoadSupport);
        Check("parentStat set, runs last (priority)", stat.parts[0].parentStat == stat && stat.parts[0].priority >= 100000f);
        Check("immutable flag cleared so the StatWorker does not cache forever", !stat.immutable);

        // ---- Scenarios on synthetic pawns ----
        Console.WriteLine("\n=== Body scenarios (real vanilla PawnCapacityUtility on synthetic bodies) ===");
        var bionicLeg = Prosthetic("BionicLeg", 1.25f);
        var bionicArm = Prosthetic("BionicArm", 1.25f);
        var bionicSpine = Prosthetic("BionicSpine", 1.25f);
        var archoLeg = Prosthetic("ArchotechLeg", 1.5f);
        var archoArm = Prosthetic("ArchotechArm", 1.5f);
        var prostheticLeg = Prosthetic("SimpleProstheticLeg", 0.85f);
        var pegLeg = Prosthetic("PegLeg", 0.6f);
        var moddedLeg = Prosthetic("SomeModdedHyperLeg", 3.0f);
        var moddedSpine = Prosthetic("SomeModdedHyperSpine", 3.0f);
        var moddedPelvis = Prosthetic("SomeModdedHyperPelvis", 3.0f);
        var moddedArm = Prosthetic("SomeModdedHyperArm", 3.0f);

        var R = new Dictionary<string, LoadSupportResult>();
        Pawn p;

        p = Human("Healthy"); R["healthy"] = Report("01 healthy human", p);
        p = Human("OneLeg"); RemovePart(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0]); R["oneleg"] = Report("02 missing one leg", p);
        p = Human("NoLegs"); RemovePart(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0]); RemovePart(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[1]); R["nolegs"] = Report("03 missing both legs", p);
        p = Human("Peg"); Install(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0], pegLeg); R["peg"] = Report("04a peg leg (0.6)", p);
        p = Human("Prost"); Install(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0], prostheticLeg); R["prost"] = Report("04b simple prosthetic leg (0.85)", p);
        p = Human("Bionic1"); Install(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0], bionicLeg); R["bionic1"] = Report("05 one bionic leg", p);
        p = Human("Bionic2"); foreach (var l in Parts(p, BodyPartTagDefOf.MovingLimbCore)) Install(p, l, bionicLeg); R["bionic2"] = Report("06 two bionic legs", p);
        p = Human("FullBionic");
        foreach (var l in Parts(p, BodyPartTagDefOf.MovingLimbCore)) Install(p, l, bionicLeg);
        foreach (var a in Parts(p, BodyPartTagDefOf.ManipulationLimbCore)) Install(p, a, bionicArm);
        Install(p, Parts(p, BodyPartTagDefOf.Spine)[0], bionicSpine);
        R["fullbionic"] = Report("07 full vanilla bionics", p);
        p = Human("Archo");
        foreach (var l in Parts(p, BodyPartTagDefOf.MovingLimbCore)) Install(p, l, archoLeg);
        foreach (var a in Parts(p, BodyPartTagDefOf.ManipulationLimbCore)) Install(p, a, archoArm);
        Install(p, Parts(p, BodyPartTagDefOf.Spine)[0], bionicSpine);
        R["archo"] = Report("08 archotech legs+arms, bionic spine", p);
        p = Human("ModLegs"); foreach (var l in Parts(p, BodyPartTagDefOf.MovingLimbCore)) Install(p, l, moddedLeg); R["modlegs"] = Report("09/10 modded 300% legs, normal core", p);
        p = Human("ModLegsCore");
        foreach (var l in Parts(p, BodyPartTagDefOf.MovingLimbCore)) Install(p, l, moddedLeg);
        Install(p, Parts(p, BodyPartTagDefOf.Spine)[0], moddedSpine); Install(p, Parts(p, BodyPartTagDefOf.Pelvis)[0], moddedPelvis);
        R["modlegscore"] = Report("11 modded 300% legs + spine + pelvis", p);
        p = Human("ModFull");
        foreach (var l in Parts(p, BodyPartTagDefOf.MovingLimbCore)) Install(p, l, moddedLeg);
        foreach (var a in Parts(p, BodyPartTagDefOf.ManipulationLimbCore)) Install(p, a, moddedArm);
        Install(p, Parts(p, BodyPartTagDefOf.Spine)[0], moddedSpine); Install(p, Parts(p, BodyPartTagDefOf.Pelvis)[0], moddedPelvis);
        R["modfull"] = Report("11b modded 300% everything (torso organic)", p);
        p = Human("Injured"); Injure(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0], 15f); Injure(p, p.RaceProps.body.corePart, 12f); R["injured"] = Report("12 leg at 50% HP, torso at 70% HP", p);
        p = Human("MissingFoot"); RemovePart(p, Parts(p, BodyPartTagDefOf.MovingLimbSegment)[0]); R["foot"] = Report("12b missing one foot", p);
        p = Human("Exo"); WholeBodyCapMod(p, PawnCapacityDefOf.Moving, 1.0f); R["exo"] = Report("13 whole-body capMod Moving +100%", p);
        p = Human("Sick"); WholeBodyCapMod(p, PawnCapacityDefOf.Moving, -0.3f); WholeBodyCapMod(p, PawnCapacityDefOf.Manipulation, -0.3f); R["sick"] = Report("13b capMods Moving/Manip -30%", p);
        p = Human("SpineBuff"); PartOffset(p, Parts(p, BodyPartTagDefOf.Spine)[0], 0.5f); R["spinebuff"] = Report("13c partEfficiencyOffset +0.5 on spine", p);
        p = MakePawn("Muffalo", QuadrupedBody(), Intelligence.Animal, true); R["animal"] = Report("14 healthy pack animal (no arms)", p);
        p = MakePawn("Muffalo3", QuadrupedBody(), Intelligence.Animal, true); RemovePart(p, Parts(p, BodyPartTagDefOf.MovingLimbCore)[0]); R["animal3"] = Report("14b pack animal missing one leg", p);
        p = MakePawn("Blob", BlobBody(), Intelligence.Humanlike); R["blob"] = Report("15 no recognised anatomy (1 untagged part)", p);
        p = MakePawn("Squid", TentacleBody(), Intelligence.Humanlike); R["squid"] = Report("15b manipulators only, no legs/spine", p);
        p = MakePawn("SquidHurt", TentacleBody(), Intelligence.Humanlike); RemovePart(p, Parts(p, BodyPartTagDefOf.ManipulationLimbCore)[0]); R["squidhurt"] = Report("15c manipulators only, one lost", p);

        Console.WriteLine("\n=== Assertions ===");
        Func<string, float> LS = k => R[k].LoadSupport;
        Check("healthy human = 1.000", Math.Abs(LS("healthy") - 1f) < 1e-5f);
        Check("healthy pack animal = 1.000", Math.Abs(LS("animal") - 1f) < 1e-5f);
        Check("strange anatomy (blob) = 1.000, no exceptions", Math.Abs(LS("blob") - 1f) < 1e-5f);
        Check("tentacle anatomy healthy = 1.000", Math.Abs(LS("squid") - 1f) < 1e-5f);
        Check("vanilla limb math: one leg missing -> lower efficiency 0.5", Math.Abs(R["oneleg"].LowerEfficiency - 0.5f) < 1e-4f);
        Check("vanilla limb math: bionic leg -> 1.125 avg (children under added part count as 1.0)", Math.Abs(R["bionic1"].LowerEfficiency - 1.125f) < 1e-4f);
        Check("vanilla limb math: two bionic legs -> 1.25", Math.Abs(R["bionic2"].LowerEfficiency - 1.25f) < 1e-4f);
        Check("missing both legs: valid, heavily reduced", LS("nolegs") > 0f && LS("nolegs") <= 0.25f);
        Check("peg < prosthetic < healthy < bionic1 < bionic2 < full bionic < archotech",
            LS("peg") < LS("prost") && LS("prost") < 1f && 1f < LS("bionic1") && LS("bionic1") < LS("bionic2")
            && LS("bionic2") < LS("fullbionic") && LS("fullbionic") < LS("archo"));
        Check("bottleneck: modded legs on normal core < 2.5", LS("modlegs") > 1.8f && LS("modlegs") < 2.5f);
        Check("legs + core much higher than legs alone (> 4x)", LS("modlegscore") > 4f * LS("modlegs"));
        Check("everything enhanced > legs + core", LS("modfull") > LS("modlegscore"));
        Check("injuries reduce", LS("injured") < 1f && LS("injured") > 0.5f);
        Check("missing foot disables that limb exactly like vanilla Moving does (limb = leg x foot)", Math.Abs(R["foot"].LowerEfficiency - 0.5f) < 1e-4f);
        Check("capMod +100% Moving: dampened (lower eff ~1.41, not 2.0)", Math.Abs(R["exo"].LowerEfficiency - 1.4142f) < 0.01f);
        Check("negative capMods reduce", LS("sick") < 1f);
        Check("partEfficiencyOffset on spine raises core", R["spinebuff"].CoreEfficiency > 1.1f && LS("spinebuff") > 1f);
        Check("animal missing a leg: reduced", LS("animal3") < 1f && LS("animal3") > 0.6f);
        Check("tentacle anatomy with lost limb: reduced but valid", LS("squidhurt") < 1f && LS("squidhurt") > 0.3f);

        // ---- MassUtility.Capacity postfix, real method ----
        Console.WriteLine("\n=== MassUtility.Capacity (caravan / inventory) through the real patched method ===");
        p = Human("MassA"); foreach (var l in Parts(p, BodyPartTagDefOf.MovingLimbCore)) Install(p, l, bionicLeg);
        float massOn = MassUtility.Capacity(p, null);
        LoadSupportMod.Settings.applyToMassCapacity = false;
        float massOff = MassUtility.Capacity(p, null);
        LoadSupportMod.Settings.applyToMassCapacity = true;
        Console.WriteLine("  two bionic legs: vanilla " + massOff.ToString("0.00") + " kg -> modded " + massOn.ToString("0.00") + " kg");
        Check("vanilla MassUtility base is 35 kg for body size 1", Math.Abs(massOff - 35f) < 1e-3f);
        Check("postfix multiplies by LoadSupport", Math.Abs(massOn - 35f * LoadSupportCache.Get(p)) < 1e-3f);
        LoadSupportMod.Settings.enabled = false;
        Check("master switch off -> vanilla 35 kg", Math.Abs(MassUtility.Capacity(p, null) - 35f) < 1e-3f);
        float v = 75f; new StatPart_LoadSupport().TransformValue(StatRequest.For(p), ref v);
        Check("master switch off -> stat untouched", Math.Abs(v - 75f) < 1e-4f);
        LoadSupportMod.Settings.enabled = true;
        LoadSupportMod.Settings.includeNonHumanlike = false;
        Pawn beast = MakePawn("MuffaloNoLeg", QuadrupedBody(), Intelligence.Animal, true); RemovePart(beast, Parts(beast, BodyPartTagDefOf.MovingLimbCore)[0]);
        Check("non-humanlike toggle off -> animal untouched", Math.Abs(MassUtility.Capacity(beast, null) - 35f) < 1e-3f);
        LoadSupportMod.Settings.includeNonHumanlike = true;
        Check("non-humanlike toggle on -> animal reduced", MassUtility.Capacity(beast, null) < 35f);
        StatPart_LoadSupport.Bypass = true;
        Check("debug bypass returns vanilla value", Math.Abs(MassUtility.Capacity(p, null) - 35f) < 1e-3f);
        StatPart_LoadSupport.Bypass = false;

        // ---- Cache behaviour with a (minimal) running game clock ----
        Console.WriteLine("\n=== Cache: time expiry, dirty marking, generation ===");
        var game = New<Game>();
        var tm = New<TickManager>();
        typeof(Game).GetField("tickManager", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).SetValue(game, tm);
        Current.Game = game;
        FieldInfo ticks = typeof(TickManager).GetField("ticksGameInt", BindingFlags.Instance | BindingFlags.NonPublic);
        ticks.SetValue(tm, 1000);

        Pawn c = Human("CacheTest");
        float c0 = LoadSupportCache.Get(c);
        // Mutate the body WITHOUT notifying: value must stay cached until expiry.
        foreach (var l in Parts(c, BodyPartTagDefOf.MovingLimbCore))
        {
            T0(c, l, bionicLeg);
        }
        ticks.SetValue(tm, 1100);
        float c1 = LoadSupportCache.Get(c);
        Check("within interval and not dirty -> cached value reused", Math.Abs(c1 - c0) < 1e-6f);
        ticks.SetValue(tm, 1000 + LoadSupportCache.RefreshIntervalTicks);
        float c2 = LoadSupportCache.Get(c);
        Check("after " + LoadSupportCache.RefreshIntervalTicks + " ticks -> recomputed (picks up bionic legs)", c2 > c1 + 0.2f);
        // Dirty via the REAL HediffSet.DirtyCache (our postfix) — only if vanilla DirtyCache runs outside Unity.
        RemovePart(c, Parts(c, BodyPartTagDefOf.ManipulationLimbCore)[0]); // AddHediff helper calls MarkDirty directly
        float c3 = LoadSupportCache.Get(c);
        Check("MarkDirty -> immediate recompute (arm removed)", c3 < c2);
        try
        {
            c.health.hediffSet.hediffs.RemoveAt(c.health.hediffSet.hediffs.Count - 1); // silently restore arm
            c.health.hediffSet.DirtyCache(); // real vanilla method + our postfix
            float c4 = LoadSupportCache.Get(c);
            Check("real HediffSet.DirtyCache() postfix marks entry dirty", Math.Abs(c4 - c2) < 1e-5f);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  skip: vanilla DirtyCache needs more game state outside Unity (" + ex.GetType().Name + "); postfix binding verified above");
        }
        ticks.SetValue(tm, 500); // clock went backwards (loaded an earlier save)
        c.health.hediffSet.hediffs.Clear();
        float c5 = LoadSupportCache.Get(c);
        Check("tick going backwards -> recompute", Math.Abs(c5 - 1f) < 1e-5f);
        LoadSupportMod.Settings.superhumanExponent = 1.0f;
        foreach (var l in Parts(c, BodyPartTagDefOf.MovingLimbCore)) T0(c, l, bionicLeg);
        float before = LoadSupportCache.Get(c);
        LoadSupportCache.InvalidateAll();
        float after = LoadSupportCache.Get(c);
        Console.WriteLine("  exponent 1.0: cached " + before.ToString("0.000") + " -> after invalidate " + after.ToString("0.000"));
        Check("InvalidateAll (settings change) -> recompute", after != before);
        LoadSupportMod.Settings.superhumanExponent = LoadSupportFormula.DefaultExponent;

        // Allocation check on the hot path (cached lookups)
        LoadSupportCache.Get(c);
        long a0 = GC.GetTotalMemory(false);
        float sink = 0; for (int i = 0; i < 100000; i++) sink += LoadSupportCache.Get(c);
        long a1 = GC.GetTotalMemory(false);
        Console.WriteLine("  info: 100k cached lookups allocated ~" + (a1 - a0) + " bytes (sink " + sink.ToString("0") + ")");
        Check("cached lookups allocate (almost) nothing", a1 - a0 < 64 * 1024);

        // Timing: full body evaluation cost
        Current.Game = null;
        Pawn t = Human("Timing"); foreach (var l in Parts(t, BodyPartTagDefOf.MovingLimbCore)) T0(t, l, bionicLeg);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20000; i++) LoadSupportCalculator.Calculate(t);
        sw.Stop();
        Console.WriteLine("  info: full body evaluation " + (sw.Elapsed.TotalMilliseconds * 1000 / 20000).ToString("0.00") + " µs/pawn (Mono JIT, outside Unity)");

        Console.WriteLine("\n=== Log output captured during tests ===");
        if (logLines.Count == 0) Console.WriteLine("  (none)");
        foreach (string s in logLines) Console.WriteLine("  " + s.Split('\n')[0]);
        Check("no errors logged", !logLines.Exists(s => s.IndexOf("rror", StringComparison.Ordinal) >= 0));

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL INTEGRATION TESTS PASSED" : failures + " FAILURE(S)");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    // Insert without notifying the cache (to test time-based expiry).
    static void T0(Pawn p, BodyPartRecord part, HediffDef def)
    {
        Hediff_AddedPart h = New<Hediff_AddedPart>();
        h.def = def; h.pawn = p;
        typeof(Hediff).GetField("part", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(h, part);
        p.health.hediffSet.hediffs.Add(h);
    }
}
