# Load Support: body-based carrying capacity for RimWorld 1.6

v0.1.0 · requires Harmony · temporary name (see *Renaming*)

A pawn's carrying capacity follows its **current physical body**. Each pawn gets a **LoadSupport** multiplier (a healthy body is exactly ×1.00), and that multiplier scales the carrying capacity the game already computed.

The mod never asks *"which mod or bionic is this?"*. It asks *"what is this pawn physically capable of right now?"*. It does that through RimWorld's own part-efficiency system, so vanilla, DLC and modded prosthetics are handled the same way, with zero compatibility patches.

---

## 1. What LoadSupport means

| LoadSupport | Meaning |
|---|---|
| 0.05 | Floor. The body is wrecked (no legs, no arms); capacity stays valid and never hits 0. |
| 0.5 | About half normal load-bearing ability |
| **1.0** | **Normal healthy body of *its own* anatomy** (human, animal, mech, modded race) |
| 2.0 | Twice normal |
| 10+ | Extreme, heavily and consistently enhanced body |

LoadSupport is recalculated from the live body and hediffs. It is **never saved**.

## 2. The three body regions

Regions are found through **BodyPartTagDefs**, which are the same tags vanilla's Moving and Manipulation capacity workers use. DefNames are never used.

| Region | Parts (by tag) | Efficiency source (vanilla API) |
|---|---|---|
| **Lower body** | `MovingLimbCore` / `MovingLimbSegment` / `MovingLimbDigit` (legs, feet, toes or equivalents) | `PawnCapacityUtility.CalculateLimbEfficiency(…, appendageWeight 0.4)`, the exact call the vanilla Moving worker makes |
| **Core** | parts tagged `Spine`, parts tagged `Pelvis`, and the body's root part (`BodyDef.corePart`, e.g. torso) | `PawnCapacityUtility.CalculatePartEfficiency` per part. Sub-weights spine 0.4, pelvis 0.4, root 0.2, renormalised over what exists |
| **Upper body** | `ManipulationLimbCore` / `…Segment` / `…Digit` (shoulders, arms, hands or equivalents) | `CalculateLimbEfficiency(…, appendageWeight 0.8)`, the exact call the vanilla Manipulation worker makes |

These vanilla functions already understand:

- missing parts and missing parents (a lost leg zeroes its foot too)
- `Hediff_AddedPart` with `addedPartProps.partEfficiency` (every vanilla prosthetic/bionic/archotech part and nearly every modded one). Parts below an added part count as 1.0, so a bionic leg is 125%, not 125% × 125% for the foot.
- `HediffStage.partEfficiencyOffset` (part-level buffs and debuffs from any mod)
- part HP (injuries, scars)

**Whole-body capacity modifiers.** Hediff `capMods` on **Moving** feed the lower region and those on **Manipulation** feed the upper region, aggregated like vanilla does (sum offsets, multiply postFactors, min of setMax). They are **dampened with a square root**: a +100% Moving exoskeleton hediff gives ×1.41 lower-body efficiency, not ×2. Movement and load-bearing are related, but they are not the same thing.

**Unusual anatomy.** If a region's tags don't exist in the body at all (an animal has no manipulation limbs, a floating mech has no legs), that region is dropped and the remaining weights are renormalised. A healthy body of *any* anatomy is therefore exactly 1.0. A body with no recognised tags at all falls back to 1.0.

## 3. Superhuman scaling

Region efficiency *e* is converted to region strength *s*:

```
e ≤ 0        → s = 0
0 < e ≤ 1    → s = e              (linear degradation for injured/weak bodies; no collapse)
e > 1        → s = e ^ k          (k = "superhuman exponent", default 2.5, setting 1.0–4.0)
```

| efficiency | 100% | 125% (bionic) | 150% (archotech) | 200% | 300% | 500% |
|---|---|---|---|---|---|---|
| strength (k = 2.5) | 1.00 | 1.75 | 2.76 | 5.66 | 15.59 | 55.90 |

The curve is continuous at 100%. The settings page shows a live preview for the current exponent.

## 4. Structural bottleneck

A plain weighted sum (`0.5·L + 0.35·C + 0.15·U`) would let 300% legs on a normal human spine produce 8.3× capacity. The formula has two stages instead:

**a) Legs → core is a chain**, so it uses a weighted **harmonic** mean. That is springs in series: the weakest link dominates.

```
S = (wL + wC) / (wL / sL + wC / sC)        wL = 0.50, wC = 0.35
```

With a normal core (sC = 1), S can never exceed 0.85 / 0.35 ≈ **2.43**, however strong the legs are. That asymptote *is* the bottleneck, so no tiers or hard caps are needed.

**b) Arms lift, but what they lift is still held up by the chain.** Their effective strength is coupled to S, and the result is blended by weight:

```
U' = 2 · sU · S / (sU + S)
LoadSupport = (0.85 · S + 0.15 · U') / 1.00
```

The result is clamped to [0.05, 10⁶]. The upper bound exists only to keep the value finite; it is not a gameplay cap. NaN/∞ from a malformed hediff turns that region neutral (1.0).

| Body | LoadSupport |
|---|---|
| Healthy | **1.00** |
| 300% legs, normal core & arms | **2.10** (naive weighted: 8.29) |
| 1000% legs, normal core & arms | **2.27**. The normal core caps it |
| 300% legs + 300% core, normal arms | **13.53** |
| 300% everywhere | **15.59** (= 3^2.5, full scaling) |
| 300% arms only | **1.13**. Arms can't out-lift the body beneath them |

The debug log prints **Weighted Support** (the naive sum, for comparison) and **Structural/Bottleneck Factor** (final ÷ naive, where 1.0 means no structural penalty).

## 5. How final carry capacity is produced

RimWorld 1.6 has **two different carry numbers**. Both were checked in the 1.6.9676 assemblies.

| Number | Where it comes from | Used for | What this mod does |
|---|---|---|---|
| **`CarryingCapacity` stat** (75 for a human) | normal StatWorker pipeline | hauling: `Pawn_CarryTracker.MaxStackSpaceEver` = stat ÷ `VolumePerUnit` | A `StatPart` is appended as the **last** part at startup. It multiplies whatever the base value, factors (genes, capacities, body size…) and other mods' parts produced. It shows in the stat's info-card breakdown. |
| **`MassUtility.Capacity`** (BodySize × 35 kg) | a plain static method, **not** the stat | inventory encumbrance, and every caravan, transport pod, shuttle and gift mass check via `CollectionsMassCalculator.Capacity` | One Harmony **postfix** multiplies the result (setting, default on). Its explanation line gets `(load support x1.31 = 45.8 kg)`. |

```
Final = (whatever the game and other mods computed) × LoadSupport
```

Nothing is replaced and no pawn is set to a fixed number.

**Caravans:** because every caravan capacity call funnels through `MassUtility.Capacity`, caravans work naturally with no second subsystem. One caveat: a formed caravan caches its total (`Caravan.cachedCaravanMassCapacity`) and only refreshes when vanilla marks it dirty (pawns join or leave, and so on). A limb lost *while travelling* shows up on the next vanilla refresh, not instantly.

### Example calculations

These come from the integration tests, which run the vanilla `PawnCapacityUtility` code on synthetic bodies. Stat base 75, mass base 35 kg.

| Pawn | Lower e→s | Core e→s | Upper e→s | LoadSupport | Carry stat | Caravan mass |
|---|---|---|---|---|---|---|
| **Normal pawn** | 1.00→1.00 | 1.00→1.00 | 1.00→1.00 | **1.000** | 75.0 | 35.0 kg |
| **Injured pawn** (a leg at 50% HP, torso at 70% HP) | 0.72→0.72 | 0.94→0.94 | 1.00→1.00 | **0.812** | 60.9 | 28.4 kg |
| Missing one leg | 0.50→0.50 | 1.00→1.00 | 1.00→1.00 | 0.651 | 48.8 | 22.8 kg |
| Missing both legs | 0.00→0.00 | 1.00→1.00 | 1.00→1.00 | 0.050 (floor) | 3.8 | 1.8 kg |
| **Vanilla bionic pawn** (bionic legs, arms, spine) | 1.25→1.75 | 1.10→1.27 | 1.25→1.75 | **1.529** | 114.7 | 53.5 kg |
| Archotech legs + arms, bionic spine | 1.50→2.76 | 1.10→1.27 | 1.50→2.76 | 1.913 | 143.5 | 67.0 kg |
| **Heavily enhanced modded pawn** (300% legs, spine, pelvis, arms; organic torso) | 3.00→15.59 | 2.60→10.90 | 3.00→15.59 | **13.405** | 1005 | 469 kg |
| **Powerful legs, normal organic core** (300% legs) | 3.00→15.59 | 1.00→1.00 | 1.00→1.00 | **2.098** | 157.3 | 73.4 kg |
| Powerful legs, *damaged* spine (300% legs, spine 50%) | 3.00→15.59 | 0.80→0.80 | 1.00→1.00 | 1.732 | 129.9 | 60.6 kg |

Worked through, **powerful legs on a normal core**:

```
sL = 3.0^2.5 = 15.59     sC = 1     sU = 1
S  = 0.85 / (0.50/15.59 + 0.35/1) = 0.85 / 0.3821 = 2.225
U' = 2·1·2.225 / (1 + 2.225)                        = 1.380
LS = 0.85·2.225 + 0.15·1.380                        = 2.098      (naive weighted sum would be 8.29)
```

## 6. Why there are no compatibility patches

There is no `ModsConfig.IsActive(...)`, no DefName check and no per-bionic XML. Nearly every body mod expresses its effect through the mechanisms listed in §2: added parts with a part efficiency, part efficiency offsets, missing parts, part HP, or capacity modifiers. The generic calculation *is* the compatibility layer.

A specific patch should only be needed if a mod changes the body in a completely non-standard way (see below).

## 7. Known limitations

- **Non-standard implementations are invisible.** Examples: a mod that boosts limbs only through `statOffsets`, custom C# capacity workers, or custom tags instead of the vanilla `MovingLimb*` / `ManipulationLimb*` / `Spine` / `Pelvis` tags. Those regions read as absent or normal.
- **Gene capMods are not read.** Only hediff capMods are. Genes still affect carrying capacity through vanilla's own stat pipeline. `capacityFactorEffectMultiplier` and `statFactorMod` on capMods are ignored.
- **Race-level tag choices matter.** If a modded race tags its legs `MovingLimbCore` but has no `Spine`/`Pelvis`, its core is just the root part. That still works and a healthy body is still 1.0, but core injuries count less.
- **Overlap with vanilla's stat factors.** As far as I know, vanilla's `CarryingCapacity` already has a **Manipulation** capacity factor (the Core XML wasn't available to confirm). If it does, arm injuries and bionic arms count twice (vanilla factor × our 15% upper region). Turn on debug logging to print the stat's actual factors at startup.
- **Missing foot = unusable leg** for the lower region. That is exactly vanilla's limb math (limb = leg × foot), deliberately not second-guessed.
- **Legless pawns drop to about 5%.** A strictly heavy penalty, by design of the chain model.
- **Caravan totals** refresh on vanilla's schedule (see §5).
- Hauling mods that read neither `CarryingCapacity` nor `MassUtility.Capacity` won't see the value. As far as I know Pick Up And Haul checks inventory space through `MassUtility`, so it should inherit the inventory multiplier naturally. This mod does not touch its AI.

---

## Settings

| Setting | Default | |
|---|---|---|
| Enable body-based carrying capacity | on | Master switch; off = the mod changes nothing |
| Superhuman scaling exponent | 2.5 | 1.0–4.0, live preview |
| Also apply to inventory/caravan mass | on | The `MassUtility.Capacity` postfix |
| Also apply to non-humanlike pawns | on | Animals, mechs. Healthy is still 1.0 |
| Show LoadSupport in inspect pane | off | Adds a "Load support: x1.23" line |
| Debug logging | off | Breakdown when a *player* pawn's value is first measured or changes by more than 0.005, plus a startup report. No log spam when idle. |

Dev-mode **debug actions** (category *Load Support*): log a clicked pawn, log all pawns on the map, benchmark, clear cache.

Example debug output:

```
Pawn: Troga (Human1234)
Body analysis (efficiency -> strength):
  - Lower Body: 15.59  (efficiency 300%)
  - Core: 1.00  (efficiency 100%)
  - Upper Body: 1.00  (efficiency 100%)
Weighted Support (no bottleneck): 8.294
Structural chain (legs+core harmonic): 2.225
Structural/Bottleneck Factor: 0.253
Final LoadSupport: 2.098
Vanilla/Base Carry Capacity: 75 kg
Final Carry Capacity: 157.3 kg
Base Inventory/Caravan Mass Capacity: 35 kg
Final Inventory/Caravan Mass Capacity: 73.4 kg
```

## Architecture

```
Source/LoadSupport/
  LoadSupportMod.cs              Mod class, settings UI, Harmony bootstrap; [StaticConstructorOnStartup] StatPart injection
  LoadSupportSettings.cs         6 settings + AppliesTo(pawn) gate
  Core/LoadSupportFormula.cs     pure math, no RimWorld references (unit-tested standalone)
  Core/BodyRegionAnalyzer.cs     pawn → 3 region efficiencies via vanilla PawnCapacityUtility
  Core/LoadSupportCalculator.cs  analyzer + formula, exception-safe (ErrorOnce → neutral 1.0)
  Core/LoadSupportCache.cs       ConditionalWeakTable<Pawn, entry>; dirty flag + 250-tick expiry + generation
  Integration/StatPart_LoadSupport.cs   CarryingCapacity multiplier
  Integration/HarmonyPatches.cs         3 postfixes (below)
  Debug/LoadSupportDebug.cs      breakdown formatting, startup report, dev-mode debug actions
```

### Harmony patches (all postfixes, nothing else)

| Target | Why |
|---|---|
| `RimWorld.MassUtility.Capacity(Pawn, StringBuilder)` | Inventory and caravan mass. Multiplies `__result`; `Priority.Last` so it composes with other mods' patches. |
| `Verse.HediffSet.DirtyCache()` | Cache invalidation. Vanilla calls it on hediff add/remove/change, part loss, prosthetic install and restoration. The postfix only flips a bool (allocation-free). |
| `Verse.Pawn.GetInspectString()` | Optional inspect-pane line. One bool check when the setting is off. |

The `CarryingCapacity` integration is a `StatPart`, not a patch.

### Cache and invalidation

- `ConditionalWeakTable<Pawn, Entry>`: keyed by the object, so there are no thingID collisions between saves, no leaks, and entries die with the pawn.
- Recalculated when any of these holds: **dirty** (the `DirtyCache` postfix fired), **older than 250 ticks** (the safety net for healing, severity drift and anything non-standard), a **settings generation change**, the **tick clock going backwards** (a load), or **no game running**.
- Only computed on demand. Nothing ticks.
- Measured outside Unity: about 13–19 µs per full body evaluation and **0 bytes** allocated per cached lookup. With the 250-tick expiry that averages well under 1 µs/tick even for dozens of haulers.
- **Save safety:** nothing is serialised (no `ExposeData`, no `GameComponent`, no `WorldComponent`); settings live in the Config folder. Removing a mod that supplied a bionic just changes what the next evaluation reads. Removing *this* mod leaves no trace in saves.

## Building

```bash
# dotnet SDK (any OS). Assumes the mod sits in RimWorld/Mods/LoadSupport, otherwise pass -p:RimWorldManaged=...
cd Source/LoadSupport
dotnet build -c Release -p:HarmonyDll="<...>/Harmony/Current/Assemblies/0Harmony.dll"
# no RimWorld install found → falls back to Krafs.Rimworld.Ref + Lib.Harmony NuGet reference packages

# Mono mcs (Linux/macOS, no NuGet)
RIMWORLD_MANAGED=<.../Managed> HARMONY_DLL=<.../0Harmony.dll> ./build.sh
```

Output goes to `1.6/Assemblies/LoadSupport.dll`. Do **not** ship `0Harmony.dll`; the Harmony mod provides it.

## Testing

`Tests/run-tests.sh` runs two suites under Mono. The latest output is in `Tests/last-run.txt`.

1. **FormulaTests**: the curve targets, 33 scenarios, monotonic/continuity sweeps, and 200k random fuzz inputs (NaN, negatives, absent regions, bad exponents). All pass.
2. **IntegrationTests**: loads the **real 1.6 Assembly-CSharp + real Harmony 2.4.1 + the built mod DLL** outside Unity. It:
   - applies the mod's real patches and checks exactly 3 methods are patched
   - checks the StatPart injection is idempotent, runs last and clears `immutable`
   - builds synthetic human, quadruped, blob and tentacle bodies from real Verse classes, adds real `Hediff_MissingPart` / `Hediff_AddedPart` / `Hediff_Injury` / capMod hediffs, and runs the vanilla limb and part efficiency code
   - calls the real patched `MassUtility.Capacity`
   - checks cache expiry, dirty marking, tick rollback, settings invalidation, and zero-allocation lookups

   All pass. The real `HediffSet.DirtyCache()` body touches Unity resources outside the engine, so that one call is skipped; its patch binding is verified.

**Not verifiable here** (needs the game running): real vanilla BodyDef XML, in-game UI, save/load round trips, mod removal, a live caravan and TPS. Checklist for an in-game pass:

1. New colony, dev mode, enable debug logging, then *Log LoadSupport (all pawns)*. Every healthy pawn should read 1.00.
2. Use dev tools to remove a leg, install prosthetic, bionic and archotech legs, and compare the info-card **Carrying capacity** breakdown.
3. Save, reload and check the values are the same. Then disable a bionics mod, reload, and check there are no errors from this mod.
4. Form a caravan and check the mass tab shows `(load support x…)` on each pawn line.
5. Run *Benchmark LoadSupport* on a 20+ pawn colony.

## Renaming

Change `<name>` and `<packageId>` in `About/About.xml`, `LoadSupport_ModName` in `Languages/English/Keyed/LoadSupport.xml`, and `HarmonyId` in `LoadSupportMod.cs`. The namespace and assembly name can stay or change freely, because nothing is serialised.
