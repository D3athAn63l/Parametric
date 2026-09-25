# Changelog

## 0.1.1: Parametric (Load Support module)

### Renamed
- The mod is now **Parametric** (package `aRed.Parametric`, Harmony ID `aRed.Parametric`, assembly `Parametric.dll`, root namespace `Parametric`). **Load Support** remains the name of its first and only module (`Parametric.LoadSupport`).
- Source moved to `Source/Parametric/` (`LoadSupport/`, `Debug/`). Translation file `Parametric.xml`: mod-level keys are `Parametric_*`, module keys stay `LoadSupport_*`.
- The settings category is **Parametric**, with a **Load Support** section. The master toggle is now **Enable Load Support** (`loadSupportEnabled`). Settings are stored under the new package, so they start from defaults.
- Logs use `[Parametric]` and `[Parametric:LoadSupport]`. The debug-action category is **Parametric**.
- `LoadSupport.dll` has been removed from `1.6/Assemblies`, and both build paths delete it if a stale copy is present.

### Fixed
- **CarryingCapacity double-counted Manipulation.** Vanilla's Manipulation capacity factor and Load Support's upper region both scaled with the arms. 300% arms on a 300% body gave about 3507 kg instead of about 1169 kg. The new `ManipulationCompensation` removes only the manipulation-limb share of vanilla's factor, using the runtime `StatDef.capacityFactors` and the real `PawnCapacityFactor.GetFactor` (so weight, max, allowedDefect and useReciprocal are respected). It keeps consciousness, capMod, gene and all other contributions. `MassUtility` is unaffected, since it has no Manipulation factor.
- **Weak or injured manipulators were over-compensated.** The first 0.1.1 draft removed vanilla's Manipulation penalty in both directions, so a one-armed pawn hauled 71 kg (vanilla 37.5), and 1% arms carried about 64 kg while 0% carried 0. Compensation is now **enhancement-only**:
  - Limbs **≤ 100%**: vanilla's Manipulation penalty is preserved and Load Support multiplies on top (one missing arm 35.6 kg, both arms at 33% 22.9 kg).
  - Limbs **> 100%**: only the above-normal limb bonus is removed.
- **Additive Manipulation capMods are handled exactly.** The systemic share is now the pawn's real Manipulation with **100% limbs**. `CalculateManipulationWithNeutralLimbs` reproduces vanilla 1.6's capMod phase: hediff and active-gene offsets, postFactors with `capacityFactorEffectMultiplier`, `EvaluateSetMax`, `minValue` and `RoundedHundredth`. This replaces the old Y / X estimate. Bionic arms + Manipulation +50% now give 117.1 kg instead of 109.3 kg, and 300% arms + setMax 120% give 84.9 kg instead of 34.0 kg.
  - The calculation checks itself by reproducing the real level from the real limbs. If a mod replaces or re-maths the Manipulation worker, it falls back to Y / X, but only for superhuman limbs. A result-preserving Harmony patch keeps the exact path.
- **Load Support could be applied twice to a pawn's inventory/caravan mass capacity** when another mod computes that pawn's mass capacity from *inside* `MassUtility.Capacity`. The typical case is a hook that returns a modded "mass carry capacity" stat whose worker reads the patched method again, guarded against its own recursion. The inner call already carried Load Support, and Parametric's postfix on the outer call multiplied it again. The info card showed `base × LS`, the caravan dialog `base × LS × LS`, a ratio of exactly LS.
  - Confirmed in game with the new mass trace. Vanilla Expanded Framework transpiles `MassUtility.Capacity` to return its `VEF_MassCarryCapacity` stat, whose worker calls `MassUtility.Capacity` again for the same pawn. With a 50.41 Load Support pawn, the caravan dialog showed about 92,500 kg while the info card showed 1835 kg. It now shows +1832 kg (1835 minus gear) in both places. The fix is generic: nothing references VEF.
  - Vanilla 1.6 calls `MassUtility.Capacity` once per pawn on every path (verified in the assembly), so this needs a re-entrant hook.
  - A per-thread same-pawn re-entrancy guard (prefix + finalizer, ~0.01 µs, no allocation) now scales a call only if no nested call for the same pawn was already scaled inside it. Nested calls for other pawns are still scaled.
  - A mod that *stores* an already-scaled value and returns it from a later, separate call is not covered and is documented; the new trace flags it.
- **StatPart ordering documentation.** `priority` does not control runtime order (only `StatDef.PostLoad` sorts, once, at def load). The part runs after existing parts because it is appended after load. Comments and README are corrected.

### Changed
- The upper region is now **limb-only**. Manipulation capMods are no longer read into Load Support, because vanilla's factor already applies them once.
- Lower-region Moving capMods now also read **active Biotech genes** and honour `capacityFactorEffectMultiplier`, mirroring vanilla aggregation. setMax is used only when defined.
- The cache safety-net expiry went from 250 to **1000 ticks**. `HediffSet.DirtyCache` is confirmed to fire for hediff add/remove/change, healing, restoration, resurrection, gene changes and load.
- The stat info card shows the Manipulation compensation line (only when superhuman manipulators are normalised). The debug breakdown and startup report include compensation, factor configuration and part order.

### Tests
- New **full CarryingCapacity pipeline** tests through the real `StatWorker`, with the real Manipulation worker, capacities handler, `GetFactor` + weight Lerp and `StatPart_BodySize`. They cover 16 required cases plus combined cases, runtime factor configurations, and a preserved non-Manipulation factor.
- The real `HediffSet.DirtyCache()` now runs in tests (render-cache calls removed by a test-only transpiler). A weak-key cleanup test has been added.
- **Expanded regression tests**, all through the real `GetStatValue(CarryingCapacity)`:
  - weak-limb series 100/85/60/50/25/10/1/0% (monotonic, no cliff) and superhuman series 100/125/150/300/500%;
  - prosthetics (simple ×1 and ×2, poor 60%, bionic, archotech, 300%, 500%);
  - positive and negative additive Manipulation capMods at normal and superhuman limbs, plus postFactor and setMax;
  - consciousness 50% with bionic and 300% arms;
  - a Harmony-patched Manipulation worker (exact path and fallback);
  - multiplicative and additive StatParts from "another mod";
  - MassUtility staying uncompensated.
- Caravan / mass-capacity chain tests through the real `MassUtility.Capacity` and `CollectionsMassCalculator.Capacity`, with test-only third-party patches:
  - a plain multiplicative postfix;
  - a re-entrant stat-backed prefix (the ×LS² bug reproduced with the guard off, fixed with it on);
  - stat-backed postfixes before and after Parametric's;
  - a nested call for another pawn;
  - an exception inside the method;
  - a stored-value re-feed (documented limitation).
- 168 integration checks and all formula tests pass.

### Added
- Dev-mode **mass-capacity trace** (*Load Support: trace mass capacity*):
  - Arming it logs every Harmony patch owner on the mass-capacity chain, then the next 24 distinct `MassUtility.Capacity` calls. Each record has the depth, the incoming and outgoing values, Load Support, the decision, the settings, the tick and a short call stack, with warnings when an incoming value already contains Load Support.
  - Off by default, bounded, and it disarms itself. *Report mass-capacity patches* logs the owner report alone.

### Documented
- **Additive StatParts that run before Parametric's** are scaled by the compensation for pawns with superhuman manipulators (a +100 kg part counts as 33 kg with 300% arms). Pawns at or below 100% are unaffected, and multiplicative parts are exact. This is measured by a test and accepted for v0.1 (README §11).

## 0.1.0: Load Support (first release, temporary name)
- Body-derived LoadSupport multiplier: lower, core and upper regions via vanilla part/limb efficiency and body-part tags.
- Superhuman curve `e^k` (k = 2.5 by default). Structural bottleneck: harmonic legs→core chain with arms coupled to it.
- `StatPart` on `CarryingCapacity`. `MassUtility.Capacity` postfix for inventory and caravans.
- Ephemeral weak-keyed cache with `HediffSet.DirtyCache` invalidation.
- Settings, debug logging, dev-mode debug actions, formula and integration test suites.
