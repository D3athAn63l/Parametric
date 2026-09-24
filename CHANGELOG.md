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
- **StatPart ordering documentation.** `priority` does not control runtime order (only `StatDef.PostLoad` sorts, once, at def load). The part runs after existing parts because it is appended after load. Comments and README are corrected.

### Changed
- The upper region is now **limb-only**. Manipulation capMods are no longer read into Load Support, because vanilla's factor already applies them once.
- Lower-region Moving capMods now also read **active Biotech genes** and honour `capacityFactorEffectMultiplier`, mirroring vanilla aggregation. setMax is used only when defined.
- The cache safety-net expiry went from 250 to **1000 ticks**. `HediffSet.DirtyCache` is confirmed to fire for hediff add/remove/change, healing, restoration, resurrection, gene changes and load.
- The stat info card shows the Manipulation compensation line. The debug breakdown and startup report include compensation, factor configuration and part order.

### Tests
- New **full CarryingCapacity pipeline** tests through the real `StatWorker`, with the real Manipulation worker, capacities handler, `GetFactor` + weight Lerp and `StatPart_BodySize`. They cover 16 required cases plus combined cases, runtime factor configurations, and a preserved non-Manipulation factor.
- The real `HediffSet.DirtyCache()` now runs in tests (render-cache calls removed by a test-only transpiler). A weak-key cleanup test has been added. 84 integration checks and all formula tests pass.

## 0.1.0: Load Support (first release, temporary name)
- Body-derived LoadSupport multiplier: lower, core and upper regions via vanilla part/limb efficiency and body-part tags.
- Superhuman curve `e^k` (k = 2.5 by default). Structural bottleneck: harmonic legs→core chain with arms coupled to it.
- `StatPart` on `CarryingCapacity`. `MassUtility.Capacity` postfix for inventory and caravans.
- Ephemeral weak-keyed cache with `HediffSet.DirtyCache` invalidation.
- Settings, debug logging, dev-mode debug actions, formula and integration test suites.
