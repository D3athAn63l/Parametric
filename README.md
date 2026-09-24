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

- the **`CarryingCapacity`** stat (hand-hauling), after the Manipulation correction in §7 (superhuman arms only; vanilla keeps its penalty for weak or missing arms)
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

### The rule: enhancement-only

`CarryingCapacity` asks two different questions:

1. **Manipulation**: can the pawn physically grip and control the hauled item?
2. **Load Support**: can the body structurally bear that weight?

Below baseline, **both** matter. Above baseline, only one of them may scale with the arms. So:

| Manipulation-limb efficiency X | What happens |
|---|---|
| **X < 100%** (injured, missing, weak, simple or poor prosthetics) | **Nothing is compensated.** Vanilla's Manipulation penalty stays, and Load Support multiplies on top. |
| **X = 100%** | Nothing changes. |
| **X > 100%** (bionic, archotech, modded superhuman parts) | Vanilla's **above-normal limb bonus** is removed. Load Support supplies the superhuman scaling instead. |

In short: **vanilla decides whether your hands still work; Parametric decides how superhuman your body is.**

```
One missing arm:   75 × 0.50 (vanilla Manipulation) × 0.95 (Load Support) ≈ 35.6 kg
Both arms at 33%:  75 × 0.33 × 0.925                                      ≈ 22.9 kg
Both arms missing: 75 × 0    × …                                          = 0 kg
```

The earlier 0.1.1 draft compensated in both directions, which erased the disability. A one-armed pawn hauled 71 kg, and 1% arms still carried about 64 kg while 0% carried 0. That cliff is gone. Hand-carry now falls smoothly to zero with Manipulation.

### How the superhuman share is removed (X > 1)

```
Y  = the pawn's real Manipulation level (the same cached value vanilla just used)
Y1 = the Manipulation level with 100% manipulation limbs and every other influence unchanged
vanilla    = Lerp(1, cf.GetFactor(Y),  cf.weight)
normalized = Lerp(1, cf.GetFactor(Y1), cf.weight)
ManipulationCompensation = Π over the stat's Manipulation factors of (normalized / vanilla)
```

**Neutral-limb Manipulation (`Y1`) is calculated exactly.** The vanilla 1.6 pipeline, verified against the assembly:

```
PawnCapacityWorker_Manipulation:  base = limbEfficiency(ManipulationLimb*, 0.8) × Consciousness
CalculateCapacityLevel:           zeroIfCannotBeAwake && !CanBeAwake → 0
                                  if base > 0: (base + Σ offset) × Π postFactor, min setMax
                                     hediff postFactors scaled by capacityFactorEffectMultiplier,
                                     setMax via EvaluateSetMax (curve/stat aware), active Biotech gene capMods
                                  max(minValue) → RoundedHundredth
```

`ManipulationCompensation.CalculateManipulationWithNeutralLimbs` reads the pawn's Manipulation capMods (hediffs and active genes, from their runtime defs) in one allocation-free pass. It then applies that capMod phase to two bases:

- `predicted = phase(X × Consciousness)`. This must reproduce the real level Y. It is the self-check.
- `Y1 = phase(1.0 × Consciousness)`. This is the answer.

So additive offsets survive limb normalisation exactly. For bionic arms plus a +50% Manipulation offset, the real level is 1.25 + 0.50 = 1.75 and the neutral-limb level is 1.00 + 0.50 = 1.50, giving 117 kg. The draft's Y / X ratio gave 109 kg.

**Fallback.** If `predicted` doesn't match Y within vanilla's 0.01 rounding, the Manipulation pipeline isn't the standard one. That covers a mod that replaces the worker, a patch that changes the worker's math, or a malformed hediff that throws. In that case Parametric uses the ratio `Y1 = Y / X`, which is exact for multiplicative influences such as consciousness and postFactors. The fallback is never used for X ≤ 1, so it can only remove superhuman enhancement and never compensates a disability. A Harmony patch that doesn't change the worker's result (logging, bookkeeping) still takes the exact path. No worker type check is made.

**Also kept.**
- `StatDefOf.CarryingCapacity.capacityFactors` is read **at runtime** and the **real** `PawnCapacityFactor.GetFactor` is called, so `weight`, `max`, `allowedDefect` and `useReciprocal` are respected. A mod that reconfigures (or patches) the factor is followed automatically. With no Manipulation factor there is no compensation.
- Everything else stays as vanilla composed it: BodySize (StatPart_BodySize), stat factors and offsets, genes, traits, other mods' parts, other capacity factors, and the **systemic** share of Manipulation (consciousness, drugs, capMods, genes, setMax). At 50% consciousness, a pawn with bionic arms keeps vanilla's ×0.5 (Y = 0.62, Y1 = 0.50).

**Left as vanilla computed it:** limbs ≤ 100%, no Manipulation factor on the stat, bodies with no manipulation limbs (animals), a vanilla factor of 0 (for example, the pawn can't be awake), and a neutral-limb factor ≤ 0. That last case is a systemic penalty so large that only the superhuman limbs keep Manipulation above zero; vanilla's value is kept rather than zeroing the stat.

`MassUtility.Capacity` has **no** Manipulation factor, so **no compensation** is applied there.

Full-pipeline results from the integration tests (the real `pawn.GetStatValue(CarryingCapacity)`, vanilla configuration). "Draft" is the first 0.1.1 revision, which compensated in both directions using Y / X.

| Pawn | Limbs | Manip | Vanilla | v0.1.0 | Draft | **Now** |
|---|---|---|---|---|---|---|
| Healthy | 1.00 | 1.00 | 75.0 | 75.0 | 75.0 | **75.0** |
| Missing one arm | 0.50 | 0.50 | 37.5 | 35.6 | 71.3 | **35.6** |
| Both arms compromised (40% HP) | 0.33 | 0.33 | 24.8 | 22.9 | 69.4 | **22.9** |
| Both arms missing | 0 | 0 | 0 | 0 | 0 | **0** |
| Two simple prosthetic arms | 0.85 | 0.85 | 63.8 | 63.0 | 74.1 | **63.0** |
| Two bionic arms | 1.25 | 1.25 | 93.8 | 97.6 | 78.1 | **78.1** |
| Two bionic legs only | 1.00 | 1.00 | 75.0 | 98.0 | 98.0 | **98.0** |
| Full vanilla bionic | 1.25 | 1.25 | 93.8 | 143.3 | 114.7 | **114.7** |
| Archotech arms + legs | 1.50 | 1.50 | 112.5 | 187.1 | 124.7 | **124.7** |
| Modded 300% arms only | 3.00 | 3.00 | 225.0 | 254.7 | 84.9 | **84.9** |
| Modded 300% full body | 3.00 | 3.00 | 225.0 | 3507.4 | 1169.1 | **1169.1** |
| Modded 500% full body | 5.00 | 5.00 | 375.0 | 20963.1 | 4192.6 | **4192.6** |
| Manipulation +50%, healthy arms | 1.00 | 1.50 | 112.5 | 112.5 | 112.5 | **112.5** |
| Manipulation +50%, bionic arms | 1.25 | 1.75 | 131.3 | 136.6 | 109.3 | **117.1** |
| Manipulation +50%, 300% arms | 3.00 | 3.50 | 262.5 | 297.1 | 99.0 | **127.3** |
| Manipulation −25%, 300% arms | 3.00 | 2.75 | 206.3 | 233.4 | 77.8 | **63.7** |
| Manipulation setMax 120%, 300% arms | 3.00 | 1.20 | 90.0 | 101.9 | 34.0 | **84.9** |
| Consciousness 50% | 1.00 | 0.50 | 37.5 | 37.5 | 37.5 | **37.5** |
| Consciousness 50% + bionic arms | 1.25 | 0.62 | 46.5 | 48.4 | 38.7 | **39.0** |
| Stat factor ×2 + 300% arms | 3.00 | 3.00 | 450.0 | 509.4 | 169.8 | **169.8** |
| Stat offset +50 + missing arm | 0.50 | 0.50 | 62.5 | 59.4 | 118.8 | **59.4** |

Weak-limb series (both arms at the given efficiency): 100% 75.0 · 85% 63.0 · 60% 43.3 · 50% 35.6 · 25% 17.1 · 10% 6.6 · 1% 0.6 · 0% 0 kg. The series is monotonic, never above vanilla, and has no cliff.
Superhuman series: 100% 75.0 · 125% 78.1 · 150% 80.3 · 300% 84.9 · 500% 85.9 kg. This is 75 × Load Support: arms alone are bottlenecked by a normal core (§5).

## 8. Inventory / caravan mass path

`MassUtility.Capacity(Pawn, StringBuilder)` = `BodySize × 35` (0 if the pawn can never carry). Vanilla 1.6 has **no mass-capacity StatDef**, so an info-card "mass carry capacity" row comes from another mod. Parametric multiplies the result by Load Support (setting, default on). It uses a **postfix** at `Priority.Last`, with a tiny prefix (`Priority.First`) and finalizer for the re-entrancy guard below.

Every vanilla consumer calls the method **exactly once per pawn** (verified in the 1.6 assembly), so there is no second subsystem:

- `CollectionsMassCalculator.Capacity` sums `Capacity(pawn) × count` over the pawns. It covers caravans, transport pods, shuttles and gifts.
- The caravan dialog's per-pawn `+X kg` (`TransferableOneWayWidget.DrawMass`) is `Capacity(pawn, null) − GearMass − InventoryMass`.
- `ITab_Pawn_Gear.TryDrawMassInfo`, `MassUtility.FreeSpace` / `UnboundedEncumbrancePercent` (encumbrance) and `FactionGiftUtility.CheckCanCarryGift` also call it once.

The caravan mass tab shows `(load support x1.36 = 47.7 kg)` on each pawn's line. A formed caravan caches its total and refreshes it on vanilla's own dirty events.

### Exactly once per pawn: the re-entrancy guard

Load Support only ever enters mass capacity through this postfix. A value that contains it twice therefore means the postfix ran on an input that already held its own output. The reproducible way this happens is **re-entrancy**: another mod computes a pawn's mass capacity *from inside* `MassUtility.Capacity` by calling it again for the same pawn. A typical case is a hook that returns a modded "mass carry capacity" stat whose worker reads the patched method, guarded against its own recursion. The inner call is already scaled; without a guard, the outer call scales it again:

```
info card (stat, computed outside):   35 × 1.04 × LS              ✓
caravan  (outer Capacity → stat → inner Capacity):
   inner: 35 × LS,  stat: × 1.04,  outer postfix: × LS  →  35 × 1.04 × LS × LS   ✗ (ratio caravan/info card = LS)
```

The prefix and finalizer keep a per-thread stack of the pawns whose capacity is being computed. **A call is scaled only if no nested call for the same pawn was already scaled inside it.** Nested calls for a *different* pawn are independent and are scaled normally.

This works whether the other mod hooks with a prefix, with a postfix before Parametric's, or with a postfix after it (all tested). An exception inside the method unwinds the stack through the finalizer. The guard costs about 0.01 µs per call and allocates nothing.

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
- **Additive StatParts that run before Parametric's.** `StatPart_LoadSupport` multiplies the value composed so far, so a flat part inserted before it (say, another mod's "+100 kg") is also multiplied. The integration test inserts exactly such a part:
  - It is scaled by Load Support. This is intended: the body's structure scales everything it carries.
  - For pawns with **superhuman manipulators only**, it is also scaled by the Manipulation compensation. With 300% arms the +100 kg counts as 33 kg before Load Support (122.6 kg instead of an ideal 198.1 kg). With a 300% body, 1688.8 kg instead of 2728.0 kg.
  - Pawns whose manipulators are at or below 100% are **exactly** unaffected, because the compensation is 1.
  - Multiplicative parts commute and are exact in every case (verified ×2).
  - Fixing this would mean transpiling `StatWorker` or removing the vanilla factor globally, so it is accepted for v0.1. A vanilla flat `CarryingCapacity` offset is applied *before* capacity factors, so it is compensated correctly (test "offset +50 + 300% arms").
- **Mass capacity: stored values fed back in.** The re-entrancy guard (§8) covers a mod that computes mass capacity *inside* `MassUtility.Capacity`. It can't see a mod that **stores** an already-scaled result and returns it from a *later, separate* call (for example, a prefix returning a cached stat value). There is no nesting to detect, so that value gets Load Support again. The mass trace (§12) flags this case with `WARNING: incoming equals Parametric's previous OUTPUT`. The integration test "P6" reproduces it.
- A Manipulation worker that is replaced or re-mathed by another mod gets the Y / X ratio fallback (§7) for superhuman pawns. It is exact for multiplicative influences; additive capMod offsets are then approximated.
- Moving capMods use `SetMaxDefined` only; `statFactorMod` on capMods is not read (vanilla 1.6 never reads that field either).
- A missing foot disables that leg for the lower region, exactly like vanilla's limb math.
- A pawn with no legs drops to the 0.05 floor.

## 12. Debug tools

- **Settings → Parametric → Debug logging** (off by default). Logs a breakdown when one of your pawns is first measured or changes by more than 0.005, plus a startup report listing the stat's real capacity factors, its parts in execution order, and Parametric's Harmony patches.
- **Dev-mode debug actions**, category **Parametric**: *Load Support: log pawn (click)*, *log all pawns on map*, *benchmark*, *clear cache*, and the mass-capacity tools below.
- **Mass-capacity trace** (for compatibility reports; off by default and costs nothing when unarmed):
  - *Load Support: trace mass capacity (click pawn)* or *(any pawn)* arms a one-shot trace. It first logs every Harmony patch (prefixes, postfixes, transpilers, finalizers, with **owner IDs**, priority and execution order) on `MassUtility.Capacity`, `CollectionsMassCalculator.*`, the caravan and transporter dialogs' mass methods, the gear tab, gift checks, and any other patched method whose name mentions mass, capacity or carry. It also lists StatDefs mentioning mass or carry, with their worker and parts.
  - For each of the next 24 distinct `MassUtility.Capacity` calls it then logs: the pawn; the nesting depth (overall and same-pawn); whether an explanation was passed; the value entering Parametric's postfix and its ratio to vanilla `BodySize × 35`; Load Support; the decision (`Scaled`, `AlreadyScaledInside`, …); the outgoing value; the settings; the tick; and a 14-frame managed call stack.
  - Warnings flag an incoming value that already appears to contain Load Support, or that equals Parametric's previous output. Repeated identical calls (the caravan dialog redraws every frame) are counted, not re-logged, and the trace disarms itself.
  - *Load Support: report mass-capacity patches* logs just the patch report. *stop mass trace* disarms early.

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
Manipulation compensation: limbs 100% <= 100%, vanilla Manipulation penalty kept (x1)
Vanilla/Base Carry Capacity: 75 kg
Final Carry Capacity: 157.3 kg
Base Inventory/Caravan Mass Capacity: 35 kg
Final Inventory/Caravan Mass Capacity: 73.4 kg
```

## 13. Testing

`Tests/run-tests.sh` runs both suites under Mono. The latest output is in `Tests/last-run.txt`.

1. **FormulaTests** (pure math, no RimWorld): curve targets, 33 scenarios, monotonicity and continuity sweeps, NaN/∞ protection, and 200k random fuzz inputs. **All pass.**
2. **IntegrationTests** (168 checks, **all pass**) load the **real 1.6 Assembly-CSharp, real Harmony 2.4.1 and the built Parametric.dll** outside Unity. They cover:
   - Parametric's real patches (exactly 3 methods; nothing left under the old ID).
   - StatPart injection: idempotent, appended after `StatPart_BodySize`, clears `immutable`.
   - Synthetic human, quadruped, blob and tentacle bodies through vanilla limb and part efficiency.
   - **The full `CarryingCapacity` pipeline** via real `pawn.GetStatValue()`, including the real Manipulation worker, `CalculateCapacityLevel`, `PawnCapacitiesHandler`, `PawnCapacityFactor.GetFactor` + weight Lerp, and vanilla `StatPart_BodySize`. It covers:
     - the core cases;
     - the **weak-limb series** 100/85/60/50/25/10/1/0% (monotonic, compensation exactly 1, no cliff);
     - the **superhuman series** 100/125/150/300/500%;
     - prosthetics (simple ×1 and ×2, poor 60%, bionic, archotech, 300%, 500%, archotech + missing);
     - **±additive Manipulation capMods** at 100/125/300/500% limbs, plus postFactor and setMax;
     - consciousness 50% with healthy, bionic, 300% and missing arms;
     - direct stat offsets and factors, and body size, with weak and superhuman arms.
   - A **patched Manipulation worker** (another mod's Harmony postfix): a result-preserving patch keeps the exact path; a math-changing patch is detected and takes the Y / X fallback; weak limbs are never compensated.
   - Runtime factor configurations: weight 0.5, max 1.0, allowedDefect 0.2, useReciprocal, no Manipulation factor, and an extra non-Manipulation capacity factor (preserved).
   - **Other mods' StatParts** inserted before Parametric's: multiplicative ×2 (exact) and additive +100 kg (measured; see §11).
   - `MassUtility` = BodySize × 35 × LS with no compensation (healthy, missing arm, bionic, 300% arms, 300% body), plus the setting toggles.
   - **Caravan / mass-capacity chain** through the real `MassUtility.Capacity`, `CollectionsMassCalculator.Capacity` (with and without explanation) and the dialog's `Capacity − GearMass`. It checks that Parametric's prefix, postfix and finalizer are each registered exactly once. Test-only third-party patches:
     - a plain ×1.04 postfix;
     - a **stat-backed prefix that re-enters the method** (reproduced ×LS² with the guard off, ×LS once with it on, and traced);
     - stat-backed postfixes before and after Parametric's;
     - a nested call for a different pawn (still scaled);
     - an exception inside the method (the stack unwinds);
     - a stored-value re-feed (the documented limitation, flagged by the trace).
   - Cache behaviour: silent change stays cached; 1000-tick expiry; the **real `HediffSet.DirtyCache()`** postfix; clock rollback; settings generation; weak keys; zero-allocation lookups.
   - Benchmarks.

   Stubbed only where Unity is required: logging, `Find.Scenario`, `Prefs.DevMode`, DLC-active checks, render-cache calls in `DirtyCache`, and the Consciousness, Breathing and BloodPumping workers (set to controllable constants).

**Benchmarks** (Mono JIT outside Unity; in-game numbers will differ but the ratios hold):

| Path | Cost |
|---|---|
| Full body evaluation (uncached) | ~10–12 µs per pawn, at most once per 1000 ticks or per dirty event |
| Cached lookup | ~0.11 µs, 0 bytes allocated |
| Manipulation compensation, limbs ≤ 100% (most pawns) | ~0.02 µs (early out) |
| Manipulation compensation, superhuman limbs (exact neutral-limb path) | ~0.16 µs (~0.23 µs with 3 extra hediffs) |
| `GetStatValue(CarryingCapacity)` | ~0.23 µs vanilla → ~0.51 µs with Parametric (bionic-armed pawn) |
| `MassUtility.Capacity` | ~0.03 µs vanilla → ~0.18 µs with Parametric (re-entrancy guard itself ~0.01 µs, 0 bytes) |

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
    ManipulationCompensation.cs         removal of vanilla's superhuman Manipulation-limb bonus (neutral-limb level)
    StatPart_LoadSupport.cs             CarryingCapacity integration
    HarmonyPatches.cs                   the 3 patched methods (mass capacity + re-entrancy guard, DirtyCache, inspect pane)
    LoadSupportLog.cs                   "[Parametric:LoadSupport] " prefix
  Debug/ParametricDebug.cs              namespace Parametric.Debug (breakdowns, dev-mode actions)
  Debug/MassCapacityTrace.cs            one-shot mass-capacity trace + Harmony owner report
```

### Harmony patches

| Target | Why |
|---|---|
| `MassUtility.Capacity(Pawn, StringBuilder)` | Inventory and caravan mass × Load Support: postfix (`Priority.Last`), plus a prefix (`Priority.First`) and finalizer for the same-pawn re-entrancy guard |
| `HediffSet.DirtyCache()` | Cache invalidation (flips a bool) |
| `Pawn.GetInspectString()` | Optional inspect-pane line (one bool check when off) |

## v0.2 candidates (not implemented)

- Additive StatParts before Parametric's are scaled by the compensation for superhuman-armed pawns (§11). An exact fix needs the compensation applied at the capacity-factor step, not as a late StatPart.
- Use final **Moving** as a secondary signal for the lower region. This was evaluated and **not adopted**: the vanilla Moving worker also multiplies Pelvis and Spine (already in Core, so they'd be double-counted) plus Breathing, BloodPumping and Consciousness (transient states would swing caravan capacity), and levels are rounded to 0.01.
- Refresh a formed caravan's cached mass when a member's Load Support changes.
