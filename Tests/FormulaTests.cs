// Standalone test harness for LoadSupportFormula (no RimWorld needed).
// Build: mcs -out:FormulaTests.exe Tests/FormulaTests.cs Source/Parametric/LoadSupport/LoadSupportFormula.cs && mono FormulaTests.exe
using System;
using System.Collections.Generic;
using Parametric.LoadSupport;
using Parametric.Overload;

static class FormulaTests
{
    static int failures;
    const float K = LoadSupportFormula.DefaultExponent;
    const float BaseCarry = 75f;   // vanilla humanlike CarryingCapacity base
    const float BaseMass = 35f;    // vanilla MassUtility capacity per body size

    static LoadSupportResult Human(float lower, float core, float upper, float k = K)
    {
        return LoadSupportFormula.Compute(true, lower, true, core, true, upper, k);
    }

    // Core efficiency as BodyRegionAnalyzer computes it: 0.4 spine + 0.4 pelvis + 0.2 torso.
    static float Core(float spine, float pelvis, float torso) { return 0.4f * spine + 0.4f * pelvis + 0.2f * torso; }

    static void Row(string name, LoadSupportResult r)
    {
        Console.WriteLine(string.Format("{0,-46} L {1,6:0.00}->{2,7:0.00}  C {3,6:0.00}->{4,7:0.00}  U {5,6:0.00}->{6,7:0.00} (eff {12,5:0.00}) | naive {7,7:0.00} bneck {8,5:0.00} | LS {9,7:0.000} | carry {10,7:0.0} kg  mass {11,6:0.0} kg",
            name, r.LowerEfficiency, r.LowerStrength, r.CoreEfficiency, r.CoreStrength, r.UpperEfficiency, r.UpperStrength,
            r.WeightedSupport, r.BottleneckFactor, r.LoadSupport, BaseCarry * r.LoadSupport, BaseMass * r.LoadSupport, r.UpperEffective));
    }

    static void Check(string what, bool ok)
    {
        if (!ok) { failures++; Console.WriteLine("  FAIL: " + what); }
        else Console.WriteLine("  ok:   " + what);
    }

    static bool Valid(LoadSupportResult r)
    {
        return !float.IsNaN(r.LoadSupport) && !float.IsInfinity(r.LoadSupport) && r.LoadSupport > 0f;
    }

    static bool Eq(float a, float b, float tol = 1e-5f) { return Math.Abs(a - b) <= tol; }

    // ---------------- Overload (pure math) ----------------
    static void OverloadTests()
    {
        Console.WriteLine("\n=== Overload: policy -> routine capacity (comfortable 80 kg) ===");
        float[] policies = { 1f, 0.75f, 0.5f, 0.25f, 0.10f };
        float[] multipliers = { 1f, 1.25f, 1.5f, 1.75f, 1.9f };
        float[] kg = { 80f, 100f, 120f, 140f, 152f };
        for (int i = 0; i < policies.Length; i++)
        {
            float m = OverloadFormula.RoutineMultiplier(policies[i]), c = OverloadFormula.RoutineCapacity(80f, policies[i]);
            Console.WriteLine(string.Format("  policy {0,4:0%}  multiplier x{1:0.00}  routine {2,6:0.0} kg", policies[i], m, c));
            Check("policy " + policies[i].ToString("0%") + " -> x" + multipliers[i].ToString("0.00") + " -> " + kg[i] + " kg (TOTAL capacity, not +)", Eq(m, multipliers[i]) && Eq(c, kg[i], 1e-3f));
        }
        Check("presets are exactly 100/75/50/25/10%", OverloadFormula.Presets.Length == 5 && Eq(OverloadFormula.Presets[0], 1f) && Eq(OverloadFormula.Presets[4], 0.10f));

        Console.WriteLine("\n=== Overload: reactive load -> movement factor (comfortable 80 kg) ===");
        float[] mass = { 80f, 88f, 100f, 112f, 120f, 140f, 152f, 160f };
        float[] expected = { 1f, 0.9f, 0.75f, 0.6f, 0.5f, 0.25f, 0.1f, 0f };
        for (int i = 0; i < mass.Length; i++)
        {
            float raw = OverloadFormula.RawFactor(mass[i], 80f), applied = OverloadFormula.ReactiveFactor(mass[i], 80f);
            Console.WriteLine(string.Format("  {0,5:0} / 80 kg = {1,4:0%}  raw x{2:0.00}  applied x{3:0.00}", mass[i], mass[i] / 80f, raw, applied));
            Check(mass[i] + "/80 -> raw x" + expected[i].ToString("0.00"), Eq(raw, expected[i], 1e-5f));
        }
        Check("160/80 (200%): mathematical x0.00, applied = emergency minimum x" + OverloadFormula.EmergencyMinimumFactor,
            Eq(OverloadFormula.RawFactor(160f, 80f), 0f) && Eq(OverloadFormula.ReactiveFactor(160f, 80f), OverloadFormula.EmergencyMinimumFactor));
        Check("below comfortable (40/80) and exactly comfortable -> exactly 1", OverloadFormula.ReactiveFactor(40f, 80f) == 1f && OverloadFormula.ReactiveFactor(80f, 80f) == 1f);
        Check("policy is inverse of the curve: loading exactly to the routine limit reaches the policy factor",
            Array.TrueForAll(policies, pol => Eq(OverloadFormula.RawFactor(OverloadFormula.RoutineCapacity(80f, pol), 80f), pol, 1e-5f)));

        // Monotonicity / continuity
        bool mono = true, cont = true; float prev = 2f;
        for (int i = 0; i <= 4000; i++)
        {
            float w = i * 0.1f; // 0..400 kg against 80
            float f = OverloadFormula.ReactiveFactor(w, 80f);
            if (f > prev + 1e-6f) mono = false;
            if (i > 0 && Math.Abs(f - prev) > 0.0013f) cont = false; // 0.1 kg step on 80 kg = 0.00125
            prev = f;
        }
        Check("monotonic non-increasing in mass (0..400 kg)", mono);
        Check("continuous: no step larger than one 0.1 kg increment (no RPG thresholds)", cont);

        // Invalid inputs
        Check("invalid capacity (0, negative, NaN, inf) -> neutral 1",
            OverloadFormula.ReactiveFactor(50f, 0f) == 1f && OverloadFormula.ReactiveFactor(50f, -10f) == 1f
            && OverloadFormula.ReactiveFactor(50f, float.NaN) == 1f && OverloadFormula.ReactiveFactor(50f, float.PositiveInfinity) == 1f);
        Check("invalid mass (NaN, negative, 0) -> neutral 1", OverloadFormula.ReactiveFactor(float.NaN, 80f) == 1f
            && OverloadFormula.ReactiveFactor(-5f, 80f) == 1f && OverloadFormula.ReactiveFactor(0f, 80f) == 1f);
        Check("infinite mass on valid capacity -> emergency minimum (finite, > 0)", Eq(OverloadFormula.ReactiveFactor(float.PositiveInfinity, 80f), OverloadFormula.EmergencyMinimumFactor));
        Check("policy sanitising: NaN/inf/<=0/>1 -> 1 (no overload); 0.01 -> 0.10 floor",
            OverloadFormula.SanitizePolicy(float.NaN) == 1f && OverloadFormula.SanitizePolicy(float.PositiveInfinity) == 1f
            && OverloadFormula.SanitizePolicy(0f) == 1f && OverloadFormula.SanitizePolicy(-0.5f) == 1f && OverloadFormula.SanitizePolicy(1.7f) == 1f
            && Eq(OverloadFormula.SanitizePolicy(0.01f), 0.10f));
        Check("routine capacity with invalid comfortable never produces NaN/negative",
            OverloadFormula.RoutineCapacity(float.NaN, 0.25f) == 0f && OverloadFormula.RoutineCapacity(-3f, 0.25f) == 0f && OverloadFormula.RoutineCapacity(0f, 0.25f) == 0f);

        // Excess / partial stack maths
        Check("excess: 135 kg at comfortable 60, policy 25% -> 30 kg", Eq(OverloadFormula.ExcessMass(135f, 60f, 0.25f), 30f, 1e-3f));
        Check("excess: already within routine limit -> 0", OverloadFormula.ExcessMass(100f, 80f, 0.25f) == 0f);
        Check("units to drop: 15 kg of 1 kg units from 75 -> 15", OverloadFormula.UnitsToDrop(15f, 1f, 75) == 15);
        Check("units to drop: 15.2 kg of 1 kg units -> 16 (ceil)", OverloadFormula.UnitsToDrop(15.2f, 1f, 75) == 16);
        Check("units to drop: bounded by the stack (100 kg of 1 kg units from 75 -> 75)", OverloadFormula.UnitsToDrop(100f, 1f, 75) == 75);
        Check("units to drop: invalid inputs -> 0", OverloadFormula.UnitsToDrop(0f, 1f, 75) == 0 && OverloadFormula.UnitsToDrop(5f, 0f, 75) == 0
              && OverloadFormula.UnitsToDrop(5f, float.NaN, 75) == 0 && OverloadFormula.UnitsToDrop(5f, 1f, 0) == 0);

        // Fuzz
        var rng = new Random(99); bool fuzz = true;
        for (int i = 0; i < 200000; i++)
        {
            float w = (float)(rng.NextDouble() * 600 - 100), c = (float)(rng.NextDouble() * 300 - 50), pol = (float)(rng.NextDouble() * 2 - 0.5);
            if (rng.Next(50) == 0) w = float.NaN; if (rng.Next(50) == 0) c = float.PositiveInfinity;
            float f = OverloadFormula.ReactiveFactor(w, c), r = OverloadFormula.RoutineCapacity(c, pol), m = OverloadFormula.RoutineMultiplier(pol);
            if (float.IsNaN(f) || f < OverloadFormula.EmergencyMinimumFactor || f > 1f || float.IsNaN(r) || r < 0f || m < 1f || m > 1.9f + 1e-6f) { fuzz = false; break; }
        }
        Check("200k random overload inputs: factor always in [0.05, 1], routine capacity finite and >= 0, multiplier in [1, 1.9]", fuzz);
    }

    static void Main()
    {
        Console.WriteLine("=== Strength curve (k = 2.5) ===");
        foreach (float e in new[] { 0f, 0.25f, 0.5f, 0.85f, 1f, 1.25f, 1.5f, 2f, 3f, 5f, 10f })
            Console.WriteLine(string.Format("  efficiency {0,5:0%} -> strength {1:0.00}", e, LoadSupportFormula.EfficiencyToStrength(e, K)));
        Check("125% ≈ 1.75", Math.Abs(LoadSupportFormula.EfficiencyToStrength(1.25f, K) - 1.747f) < 0.01f);
        Check("150% ≈ 2.76", Math.Abs(LoadSupportFormula.EfficiencyToStrength(1.5f, K) - 2.756f) < 0.01f);
        Check("200% ≈ 5.66", Math.Abs(LoadSupportFormula.EfficiencyToStrength(2f, K) - 5.657f) < 0.01f);
        Check("300% ≈ 15.59", Math.Abs(LoadSupportFormula.EfficiencyToStrength(3f, K) - 15.588f) < 0.02f);
        Check("500% ≈ 55.9", Math.Abs(LoadSupportFormula.EfficiencyToStrength(5f, K) - 55.9f) < 0.05f);
        Check("continuous at 100%", Math.Abs(LoadSupportFormula.EfficiencyToStrength(1.0001f, K) - LoadSupportFormula.EfficiencyToStrength(0.9999f, K)) < 0.001f);
        Console.WriteLine();

        // Vanilla replacement-part efficiencies used below (addedPartProps.partEfficiency, as commonly defined in Core):
        //   peg leg 0.6, simple prosthetic leg 0.85, bionic leg/arm/spine 1.25, archotech leg/arm 1.5.
        // Region values follow PawnCapacityUtility.CalculateLimbEfficiency = average over limbs.
        Console.WriteLine("=== Scenarios (weights L .50 / C .35 / U .15, k = 2.5) ===");
        var R = new Dictionary<string, LoadSupportResult>();
        Action<string, LoadSupportResult> add = (n, r) => { R[n] = r; Row(n, r); };

        add("01 healthy human", Human(1, 1, 1));
        add("02 missing one leg", Human(0.5f, 1, 1));
        add("03 missing both legs", Human(0f, 1, 1));
        add("03b missing both legs and both arms", Human(0f, 1, 0f));
        add("04 one simple prosthetic leg (0.85)", Human((0.85f + 1f) / 2f, 1, 1));
        add("04b one peg leg (0.6)", Human((0.6f + 1f) / 2f, 1, 1));
        add("05 one bionic leg", Human((1.25f + 1f) / 2f, 1, 1));
        add("06 two bionic legs", Human(1.25f, 1, 1));
        add("07 full vanilla bionics (legs,arms,spine)", Human(1.25f, Core(1.25f, 1, 1), 1.25f));
        add("08 archotech legs+arms, bionic spine", Human(1.5f, Core(1.25f, 1, 1), 1.5f));
        add("09 modded legs 300%, rest normal", Human(3f, 1, 1));
        add("10 modded legs 500%, rest normal", Human(5f, 1, 1));
        add("10b modded legs 1000%, rest normal", Human(10f, 1, 1));
        add("11 legs 300% + spine&pelvis 300%", Human(3f, Core(3, 3, 1), 1));
        add("11b legs 300% + core 300% (all)", Human(3f, 3f, 1));
        add("11c full body 300%", Human(3f, 3f, 3f));
        add("11d legs 500% + core 500%", Human(5f, 5f, 1));
        add("11e full body 500%", Human(5f, 5f, 5f));
        add("12 injured: leg 44% eff, torso 70%", Human((0.444f + 1f) / 2f, Core(1, 1, 0.7f), 1));
        add("13 bad spine (30%), legs fine", Human(1, Core(0.3f, 1, 1), 1));
        add("14 powerful legs 300% + weak spine 50%", Human(3f, Core(0.5f, 1, 1), 1));
        add("15 missing one arm", Human(1, 1, 0.5f));
        add("16 super arms 300% only", Human(1, 1, 3f));
        add("16b super core 300% only", Human(1, 3f, 1));
        add("17 animal healthy (no upper)", LoadSupportFormula.Compute(true, 1, true, 1, false, 0, K));
        add("18 animal missing 1 of 4 legs", LoadSupportFormula.Compute(true, 0.75f, true, 1, false, 0, K));
        add("19 legless mech (core only)", LoadSupportFormula.Compute(false, 0, true, 1, false, 0, K));
        add("20 no recognised anatomy", LoadSupportFormula.Compute(false, 0, false, 0, false, 0, K));
        add("21 garbage inputs (NaN,-5,+inf)", Human(float.NaN, -5f, float.PositiveInfinity));
        add("21b one NaN region, rest healthy", Human(float.NaN, 1, 1));
        add("22 legs 300%, exponent 1.0 (linear)", Human(3f, 1, 1, 1f));
        add("23 full body 300%, exponent 1.0", Human(3f, 3f, 3f, 1f));
        add("24 full body 300%, exponent 4.0", Human(3f, 3f, 3f, 4f));
        Console.WriteLine();

        Console.WriteLine("=== Assertions ===");
        Check("healthy = exactly 1.0", Math.Abs(R["01 healthy human"].LoadSupport - 1f) < 1e-6f);
        Check("healthy animal = exactly 1.0", Math.Abs(R["17 animal healthy (no upper)"].LoadSupport - 1f) < 1e-6f);
        Check("healthy legless mech = exactly 1.0", Math.Abs(R["19 legless mech (core only)"].LoadSupport - 1f) < 1e-6f);
        Check("one leg missing: 0.55..0.85", R["02 missing one leg"].LoadSupport > 0.55f && R["02 missing one leg"].LoadSupport < 0.85f);
        Check("super arms only: small gain, far below super legs only", R["16 super arms 300% only"].LoadSupport > 1f && R["16 super arms 300% only"].LoadSupport < 0.7f * R["09 modded legs 300%, rest normal"].LoadSupport);
        Check("super core only: bottlenecked like legs-only", R["16b super core 300% only"].LoadSupport > 1.3f && R["16b super core 300% only"].LoadSupport < 2.5f);
        Check("NaN/inf region -> neutral 1.0 for that region, flagged", Math.Abs(R["21b one NaN region, rest healthy"].LoadSupport - 1f) < 1e-6f && R["21b one NaN region, rest healthy"].Fallback);
        Check("both legs missing: valid, heavily reduced (<0.25)", Valid(R["03 missing both legs"]) && R["03 missing both legs"].LoadSupport < 0.25f);
        Check("everything missing: floor, not zero", Math.Abs(R["03b missing both legs and both arms"].LoadSupport - LoadSupportFormula.MinLoadSupport) < 1e-6f);
        Check("peg < prosthetic < healthy < 1 bionic < 2 bionic",
            R["04b one peg leg (0.6)"].LoadSupport < R["04 one simple prosthetic leg (0.85)"].LoadSupport
            && R["04 one simple prosthetic leg (0.85)"].LoadSupport < 1f
            && 1f < R["05 one bionic leg"].LoadSupport
            && R["05 one bionic leg"].LoadSupport < R["06 two bionic legs"].LoadSupport);
        Check("full vanilla bionics > two bionic legs", R["07 full vanilla bionics (legs,arms,spine)"].LoadSupport > R["06 two bionic legs"].LoadSupport);
        Check("archotech > full bionics", R["08 archotech legs+arms, bionic spine"].LoadSupport > R["07 full vanilla bionics (legs,arms,spine)"].LoadSupport);

        float legsOnly = R["09 modded legs 300%, rest normal"].LoadSupport;
        float legsCore = R["11b legs 300% + core 300% (all)"].LoadSupport;
        float full = R["11c full body 300%"].LoadSupport;
        Check("legs-only 300% improves substantially (> 1.8x)", legsOnly > 1.8f);
        Check("bottleneck visibly matters: legs-only < 30% of naive weighted", legsOnly < 0.3f * R["09 modded legs 300%, rest normal"].WeightedSupport);
        Check("legs+core >> legs-only (> 4x)", legsCore > 4f * legsOnly);
        Check("full body gets full scaling (= 300%^2.5)", Math.Abs(full - LoadSupportFormula.EfficiencyToStrength(3f, K)) < 0.01f);
        Check("normal core caps legs: 1000% legs < 2.6x", R["10b modded legs 1000%, rest normal"].LoadSupport < 2.6f);
        Check("monotonic in legs: 300% < 500% < 1000%", legsOnly < R["10 modded legs 500%, rest normal"].LoadSupport
              && R["10 modded legs 500%, rest normal"].LoadSupport < R["10b modded legs 1000%, rest normal"].LoadSupport);
        Check("weak spine limits strong legs below legs+normal-core", R["14 powerful legs 300% + weak spine 50%"].LoadSupport < legsOnly);
        Check("garbage inputs produce finite positive value", Valid(R["21 garbage inputs (NaN,-5,+inf)"]));
        Check("no anatomy -> neutral 1.0", Math.Abs(R["20 no recognised anatomy"].LoadSupport - 1f) < 1e-6f);
        Check("exponent 1.0 makes full body linear (3.0)", Math.Abs(R["23 full body 300%, exponent 1.0"].LoadSupport - 3f) < 1e-4f);

        // Continuity sweep: no jumps anywhere along a path from wrecked to godlike.
        float prev = -1f; bool mono = true, finite = true; float maxJump = 0f;
        for (int i = 0; i <= 1000; i++)
        {
            float e = i * 0.01f; // 0 .. 10
            float v = Human(e, e, e).LoadSupport;
            if (float.IsNaN(v) || float.IsInfinity(v) || v <= 0) finite = false;
            if (prev >= 0 && v + 1e-6f < prev) mono = false;
            if (prev >= 0 && e > 0.05f && e < 1.5f) maxJump = Math.Max(maxJump, Math.Abs(v - prev));
            prev = v;
        }
        Check("uniform sweep 0..1000% finite", finite);
        Check("uniform sweep monotonic", mono);
        Check("no discontinuity around 100% (max step < 0.05)", maxJump < 0.05f);

        // Randomised fuzz
        var rng = new Random(1234); bool fuzzOk = true;
        for (int i = 0; i < 200000; i++)
        {
            float l = (float)(rng.NextDouble() * 20 - 2), c = (float)(rng.NextDouble() * 20 - 2), u = (float)(rng.NextDouble() * 20 - 2);
            float k = (float)(rng.NextDouble() * 6 - 1);
            var r = LoadSupportFormula.Compute(rng.Next(2) == 0, l, rng.Next(2) == 0, c, rng.Next(2) == 0, u, k);
            if (!Valid(r) || r.LoadSupport < LoadSupportFormula.MinLoadSupport - 1e-6f) { fuzzOk = false; break; }
        }
        Check("200k random inputs (incl. negative, absent regions, bad exponents) always valid", fuzzOk);

        // Perf of the pure math
        var sw = System.Diagnostics.Stopwatch.StartNew();
        float sink = 0; for (int i = 0; i < 1000000; i++) sink += Human(1.1f, 1.05f, 0.9f).LoadSupport;
        sw.Stop();
        Console.WriteLine("  info: formula cost " + (sw.Elapsed.TotalMilliseconds * 1000 / 1e6).ToString("0.000") + " µs/call (sink " + sink.ToString("0") + ")");

        OverloadTests();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL FORMULA TESTS PASSED" : failures + " FAILURE(S)");
        Environment.Exit(failures == 0 ? 0 : 1);
    }
}
