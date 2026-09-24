# Parametric

v0.1.1 · RimWorld 1.6 · requires Harmony · package `aRed.Parametric`

## 1. What Parametric is

Parametric is a lightweight background systems mod. Its values are derived at runtime from what a pawn actually *is* right now, never from lists of specific mods or defs. It stores nothing in saves and is safe to add or remove mid-game.

```
Parametric
└── Load Support   (the only module in v0.1)
```

---

## Current module: Load Support

## 2. What Load Support does

Every pawn gets a **Load Support** multiplier (a healthy body is exactly ×1.00). It is worked out from the current state of the pawn's legs, core and arms, and it scales:

- the **`CarryingCapacity`** stat (hand-hauling), after the Manipulation correction in §7
- **`MassUtility.Capacity`** (inventory encumbrance, caravans, transport pods, shuttles), optional and on by default

| Load Support | Meaning |
|---|---|
| 0.05 | Floor for a wrecked body (never 0, never negative) |
| 0.5 | About half normal |
| **1.0** | **Normal healthy body of its own anatomy** (human, animal, mech, modded race) |
| 2.0 | Twice normal |
| 10+ | Heavily and consistently enhanced body |

The guiding rule is not *"which mod or bionic is this?"* but *"what is this pawn physically capable of right now?"*.

## 3. Body-region detection

Regions are found through **BodyPartTagDefs**, the same tags vanilla's capacity workers use. DefNames are never used.

| Region | Parts (by tag) | Efficiency source (vanilla API) |
|---|---|---|
| **Lower body** | `MovingLimbCore` / `…Segment` / `…Digit` | `PawnCapacityUtility.CalculateLimbEfficiency(…, 0.4)`, the vanilla Moving worker's limb term. It is then multiplied by the dampened **Moving capMods** from hediffs *and active genes* (vanilla aggregation, including `capacityFactorEffectMultiplier` and defined `setMax`). |
| **Core** | `Spine`, `Pelvis`, the body's root part (`corePart`) | `CalculatePartEfficiency` per part. Weights spine 0.4 / pelvis 0.4 / root 0.2, renormalised over the parts that exist. |
| **Upper body** | `ManipulationLimbCore` / `…Segment` / `…Digit` | `CalculateLimbEfficiency(…, 0.8)`, the vanilla Manipulation worker's limb term. **Limbs only** (see §7). |

The vanilla functions already understand missing parts and missing parents, `Hediff_AddedPart.partEfficiency` (every vanilla prosthetic, bionic and archotech part, and nearly every modded one), `partEfficiencyOffset`, and part HP. A region whose tags don't exist in the anatomy is dropped and its weight redistributed, so a healthy body of **any** anatomy is exactly 1.0.

Moving capMods are **dampened with a square root** (+100% Moving → ×1.41 lower body). Moving and load-bearing are related, but they are not the same thing.

## 4. Superhuman scaling

```
e ≤ 0      → s = 0
0 < e ≤ 1  → s = e          (linear degradation, no collapse)
e > 1      → s = e ^ k      (k = superhuman exponent, default 2.5, setting 1.0–4.0)
```

| efficiency | 100% | 125% | 150% | 200% | 300% | 500% |
|---|---|---|---|---|---|---|
| strength (k = 2.5) | 1.00 | 1.75 | 2.76 | 5.66 | 15.59 | 55.90 |

## 5. Structural bottleneck

**Legs → core is a chain**, so it uses a weighted harmonic mean (springs in series, where the weakest link dominates):

```
S  = (0.50 + 0.35) / (0.50 / sL + 0.35 / sC)
U' = 2 · sU · S / (sU + S)                       arms lift, but the chain holds what they lift
LoadSupport = 0.85 · S + 0.15 · U'               clamp [0.05, 10^6]; NaN/∞ region → neutral 1.0
```

With a normal core, S can never exceed 0.85 / 0.35 ≈ 2.43, however strong the legs are.

| Body | Load Support |
|---|---|
| Healthy | **1.00** |
| 300% legs, normal core and arms | **2.10** (a naive weighted sum would give 8.29) |
| 300% legs + spine + pelvis | **11.54** |
| 300% everywhere | **15.59** (= 3^2.5) |
| 300% arms only | **1.13** |

## 6. CarryingCapacity integration

The RimWorld 1.6 `StatWorker` pipeline, verified against the 1.6.9676 assemblies:

```
GetValueUnfinalized:  base (75) → offsets (skills, traits, hediffs, genes, precepts…) → statFactors
                      → capacityFactors:  val = Lerp(val, val · cf.GetFactor(capacities.GetLevel(cf.capacity)), cf.weight)
FinalizeValue:        stat.parts IN LIST ORDER (vanilla StatPart_BodySize, …, StatPart_LoadSupport)
                      → postProcessCurve → postProcessStatFactors → scenario → rounding → clamp(min, max)
```

`StatPart_LoadSupport` is appended to `CarryingCapacity.parts` at startup. It is idempotent, never reorders other parts, and clears `immutable` if needed. It applies:

```
Final = (CarryingCapacity as composed by vanilla + other mods) × ManipulationCompensation × LoadSupport
```

**Ordering (corrected).** `StatPart.priority` is used only by `StatDef.PostLoad`, which sorts the XML-defined parts once when defs load. At runtime `FinalizeValue` iterates `parts` in **list order**. Our part runs after every part that existed at startup because it is *appended* after `PostLoad`, not because of its priority. A mod that appends a part later would run after ours; Parametric deliberately does not reorder other mods' parts. The multiplication is order-independent against other multiplicative parts anyway.

## 7. Why Manipulation requires compensation

Vanilla `CarryingCapacity` already has a **Manipulation capacity factor** (weight 1). Load Support's upper region measures the same manipulation limbs. In v0.1.0 this double-counted. For example, 300% arms on a 300% body:

```
old:   75 × 3 (vanilla Manipulation) × 15.59 (Load Support) ≈ 3507 kg
fixed: 75 × 15.59                                            ≈ 1169 kg
```

Vanilla's Manipulation level is **limb efficiency × consciousness × capMods × genes × custom-worker logic**. Load Support represents only the *limb* part, so Parametric removes only that part:

```
Y = pawn's Manipulation level (the same cached value vanilla just used)
X = Load Support's upper-limb efficiency
vanilla    = Lerp(1, cf.GetFactor(Y),     cf.weight)
normalized = Lerp(1, cf.GetFactor(Y / X), cf.weight)      ← vanilla's factor with 100% limbs
ManipulationCompensation = Π over the stat's Manipulation factors of (normalized / vanilla)
```

- It reads `StatDefOf.CarryingCapacity.capacityFactors` **at runtime** and calls the **real** `PawnCapacityFactor.GetFactor`, so `weight`, `max`, `allowedDefect` and `useReciprocal` are respected, and a mod that reconfigures (or patches) the factor is followed automatically. With no Manipulation factor there is no compensation.
- Everything else is kept: BodySize (StatPart_BodySize), stat factors and offsets, genes, traits, other mods' parts, other capacity factors, and the **non-limb share** of Manipulation (consciousness, drugs, capMods, genes). For example, a pawn at 50% consciousness still gets vanilla's ×0.5.
- Vanilla rounds capacity levels to 0.01. When Y is exactly `round(X)` the systemic part is treated as exactly 1, which removes rounding noise.
- Y/X is exact for multiplicative effects. For **additive** capMod offsets combined with non-100% limbs it is an approximation (+50% Manipulation offset with bionic arms: 109 kg vs 117 kg for a purely additive reading).
- **Not correctable:** with every manipulator gone (X ≈ 0), or a vanilla factor of 0, vanilla's result was 0 and can't be divided back, so it stands (no hands, no hand-carrying). Bodies with no manipulation limbs at all (animals) get no compensation, because nothing was double-counted.

`MassUtility.Capacity` has **no** Manipulation factor, so **no compensation** is applied there.

Full-pipeline results from the integration tests (the real `pawn.GetStatValue(CarryingCapacity)`, vanilla configuration):

| Pawn | Manip | Vanilla | Old bug (v0.1.0) | **Now** |
|---|---|---|---|---|
| Healthy | 1.00 | 75.0 | 75.0 | **75.0** |
| Missing one arm | 0.50 | 37.5 | 35.6 | **71.3** |
| Both arms compromised (40% HP) | 0.33 | 24.8 | 22.9 | **69.4** |
| Two bionic arms | 1.25 | 93.8 | 97.6 | **78.1** |
| Two bionic legs only | 1.00 | 75.0 | 98.0 | **98.0** |
| Full vanilla bionic | 1.25 | 93.8 | 143.3 | **114.7** |
| Archotech arms + legs | 1.50 | 112.5 | 187.1 | **124.7** |
| Modded 300% arms only | 3.00 | 225.0 | 254.7 | **84.9** |
| Modded 300% legs only | 1.00 | 75.0 | 157.3 | **157.3** |
| Modded 300% full body | 3.00 | 225.0 | 3507.4 | **1169.1** |
| Modded 500% full body | 5.00 | 375.0 | 20963.1 | **4192.6** |
| capMod Manipulation +50% | 1.50 | 112.5 | 112.5 | **112.5** |
| Consciousness 50% | 0.50 | 37.5 | 37.5 | **37.5** |
| Stat factor ×2 (e.g. a gene) | 1.00 | 150.0 | 150.0 | **150.0** |
| Stat offset +50 | 1.00 | 125.0 | 125.0 | **125.0** |
| Body size 1.5 + bionic arms | 1.25 | 140.6 | 146.4 | **117.1** |

**Design consequence to be aware of:** with vanilla's Manipulation factor replaced, hand-carry is only as sensitive to arms as Load Support's 15% upper weight. A one-armed pawn now hauls about 71 kg, where vanilla gives 37.5 kg; 300% arms alone give 85 kg, where vanilla gives 225 kg. This follows the v0.1 design, but it's a tuning question for later (see *v0.2 candidates*).

## 8. Inventory / caravan mass path

`MassUtility.Capacity(Pawn, StringBuilder)` = `BodySize × 35` (0 if the pawn can never carry). One **postfix** multiplies the result by Load Support (setting, default on). Every caravan, transport pod, shuttle and gift check goes through `CollectionsMassCalculator.Capacity`, which calls this method per pawn, so there is no second subsystem. The caravan mass tab shows `(load support x1.36 = 47.7 kg)` on each pawn's line. A formed caravan caches its total and refreshes it on vanilla's own dirty events.

## 9. Cache architecture

- `ConditionalWeakTable<Pawn, Entry>`: keyed by the pawn object, runtime only, **never serialised**. Entries die with the pawn (verified: the cache does not keep a discarded pawn alive).
- **Dirty event:** a postfix on `HediffSet.DirtyCache()`. In 1.6 that method is called from `AddDirect`, `RemoveHediff`, `Notify_HediffChanged` (injury healing, stage and severity changes), `RestorePart`, `Notify_Resurrected`, **`Notify_GenesChanged`**, `Kill` and on load. The postfix only flips a bool.
- **Safety-net expiry: 1000 ticks** (≈ 16.7 s at 1×), raised from 250. The event covers every standard body change, so the timer only catches drift that raises no event (stat-driven setMax curves, `capacityFactorEffectMultiplier` stats, exotic mods).
- **Settings generation counter** (exponent change) and **clock rollback** (loading an earlier save) both force a recompute.
- Manipulation compensation is **not cached**. It reads the live, already-cached vanilla Manipulation level on each query, so it always cancels exactly what vanilla applied in the same evaluation.

## 10. Compatibility philosophy

There is no `ModsConfig.IsActive(...)`, no DefName checks, no per-bionic XML, and no compatibility patches. Mods that express their effects through standard RimWorld mechanisms are understood automatically: added parts, part-efficiency offsets, missing parts, part HP, capMods (hediff or gene), stat offsets and factors, and capacity factors. Load Support multiplies the carrying capacity the game already composed instead of replacing it.

## 11. Known limitations

- Non-standard body mechanics are invisible: a C# worker that ignores the vanilla tags, limb buffs expressed only as stat offsets, or custom tags. Those regions read as absent or normal.
- Additive Manipulation capMod offsets combined with non-100% limbs are approximated (§7).
- An inspiration that adds a flat offset to `CarryingCapacity` (applied by vanilla *after* capacity factors) would be scaled by the compensation. I know of no vanilla inspiration that does this.
- Moving capMods use `SetMaxDefined` only; `statFactorMod` on capMods is not read (vanilla 1.6 never reads that field either).
- A missing foot disables that leg for the lower region, exactly like vanilla's limb math.
- A pawn with no legs drops to the 0.05 floor.
- Arm sensitivity of hand-carry is now low (§7, design consequence).

## 12. Debug tools

- **Settings → Parametric → Debug logging** (off by default). Logs a breakdown when one of your pawns is first measured or changes by more than 0.005, plus a startup report listing the stat's real capacity factors, its parts in execution order, and Parametric's Harmony patches.
- **Dev-mode debug actions**, category **Parametric**: *Load Support: log pawn (click)*, *log all pawns on map*, *benchmark*, *clear cache*.

```
[Parametric:LoadSupport] Troga (Human1234) changed 1.00 -> 2.10
Pawn: Troga (Human1234)
Body analysis (efficiency -> strength):
  - Lower Body: 15.59  (efficiency 300%)
  - Core: 1.00  (efficiency 100%)
  - Upper Body: 1.00  (efficiency 100%)
Weighted Support (no bottleneck): 8.294
Structural chain (legs+core harmonic): 2.225
Structural/Bottleneck Factor: 0.253
Final LoadSupport: 2.098
Manipulation compensation: level 100%, limbs 100%, vanilla factor x1 -> limb-normalised x1  => x1
Vanilla/Base Carry Capacity: 75 kg
Final Carry Capacity: 157.3 kg
Base Inventory/Caravan Mass Capacity: 35 kg
Final Inventory/Caravan Mass Capacity: 73.4 kg
```

## 13. Testing

`Tests/run-tests.sh` runs both suites under Mono. The latest output is in `Tests/last-run.txt`.

1. **FormulaTests** (pure math, no RimWorld): curve targets, 33 scenarios, monotonicity and continuity sweeps, NaN/∞ protection, and 200k random fuzz inputs. **All pass.**
2. **IntegrationTests** (84 checks, **all pass**) load the **real 1.6 Assembly-CSharp, real Harmony 2.4.1 and the built Parametric.dll** outside Unity. They cover:
   - Parametric's real patches (exactly 3 methods; nothing left under the old ID).
   - StatPart injection: idempotent, appended after `StatPart_BodySize`, clears `immutable`.
   - Synthetic human, quadruped, blob and tentacle bodies through vanilla limb and part efficiency.
   - **The full `CarryingCapacity` pipeline** via real `pawn.GetStatValue()`, including the real Manipulation worker, `CalculateCapacityLevel`, `PawnCapacitiesHandler`, `PawnCapacityFactor.GetFactor` + weight Lerp, and vanilla `StatPart_BodySize`. It runs all 16 required cases plus consciousness and combined cases, and the double-count bug is asserted gone (3507 → 1169 kg).
   - Runtime factor configurations: weight 0.5, max 1.0, allowedDefect 0.2, useReciprocal, no Manipulation factor, and an extra non-Manipulation capacity factor (preserved).
   - `MassUtility` = BodySize × 35 × LS with no compensation, plus the setting toggles.
   - Cache behaviour: silent change stays cached; 1000-tick expiry; the **real `HediffSet.DirtyCache()`** postfix; clock rollback; settings generation; weak keys; zero-allocation lookups.
   - Benchmarks.

   Stubbed only where Unity is required: logging, `Find.Scenario`, `Prefs.DevMode`, DLC-active checks, render-cache calls in `DirtyCache`, and the Consciousness, Breathing and BloodPumping workers (set to controllable constants).

**Benchmarks** (Mono JIT outside Unity; in-game numbers will differ but the ratios hold):

| Path | Cost |
|---|---|
| Full body evaluation (uncached) | ~12 µs per pawn, at most once per 1000 ticks or per dirty event |
| Cached lookup | ~0.13 µs, 0 bytes allocated |
| Manipulation compensation | ~0.08 µs per query |
| `GetStatValue(CarryingCapacity)` | 0.30 µs vanilla → 0.53 µs with Parametric |

**Still to check in-game:** real vanilla BodyDef XML, the settings UI, save/load, removing a bionics mod, a live caravan, and TPS on a real colony. Use the debug actions.

---

## Build

```bash
cd Source/Parametric
dotnet build -c Release -p:HarmonyDll="<...>/Harmony/Current/Assemblies/0Harmony.dll"
#   assumes the mod sits in RimWorld/Mods/Parametric; otherwise -p:RimWorldManaged=<.../Managed>
#   without a local RimWorld it falls back to Krafs.Rimworld.Ref + Lib.Harmony reference packages
# or, with Mono:
RIMWORLD_MANAGED=<.../Managed> HARMONY_DLL=<.../0Harmony.dll> ./build.sh
```

The output is `1.6/Assemblies/Parametric.dll`. Both build paths delete a stale `LoadSupport.dll` if one is present. **Do not** ship `0Harmony.dll`.

## Source layout

```
Source/Parametric/
  Parametric.csproj
  ParametricMod.cs                      Mod class, settings UI, Harmony (aRed.Parametric), startup bootstrap
  ParametricSettings.cs
  LoadSupport/                          namespace Parametric.LoadSupport
    LoadSupportFormula.cs               pure math (unit-tested standalone)
    BodyRegionAnalyzer.cs               pawn → 3 region efficiencies (vanilla APIs)
    LoadSupportCalculator.cs            analyzer + formula, exception-safe
    LoadSupportCache.cs                 weak-keyed ephemeral cache
    ManipulationCompensation.cs         exact removal of vanilla's Manipulation-limb share
    StatPart_LoadSupport.cs             CarryingCapacity integration
    HarmonyPatches.cs                   the 3 postfixes
    LoadSupportLog.cs                   "[Parametric:LoadSupport] " prefix
  Debug/ParametricDebug.cs              namespace Parametric.Debug
```

### Harmony patches (all postfixes)

| Target | Why |
|---|---|
| `MassUtility.Capacity(Pawn, StringBuilder)` | Inventory and caravan mass × Load Support (`Priority.Last`) |
| `HediffSet.DirtyCache()` | Cache invalidation (flips a bool) |
| `Pawn.GetInspectString()` | Optional inspect-pane line (one bool check when off) |

## v0.2 candidates (not implemented)

- Decide how much arms should matter for **hand-carry** now that vanilla's Manipulation factor is replaced, for example a stat-path-specific upper weight.
- An exact (non-multiplicative) reconstruction for additive Manipulation capMod offsets, if that ever matters in practice.
- Use final **Moving** as a secondary signal for the lower region. This was evaluated and **not adopted**: the vanilla Moving worker also multiplies Pelvis and Spine (already in Core, so they'd be double-counted) plus Breathing, BloodPumping and Consciousness (transient states would swing caravan capacity), and levels are rounded to 0.01.
- Refresh a formed caravan's cached mass when a member's Load Support changes.
