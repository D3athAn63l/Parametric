# Changelog

## 0.1.0 — first release

- LoadSupport multiplier derived from the current body in three regions: lower body (moving limbs), core (spine, pelvis, root part) and upper body (manipulation limbs). It uses vanilla `PawnCapacityUtility` limb and part efficiency and body-part tags, never DefNames.
- Hediff capMods on Moving and Manipulation are read with square-root dampening.
- Superhuman curve: `e^k` above 100% efficiency (k = 2.5 by default, configurable 1.0–4.0), linear below 100%.
- Structural bottleneck: a weighted harmonic chain (legs → core), with arms coupled to the chain. Continuous, no tiers.
- `CarryingCapacity` integration through a `StatPart` appended last at startup, so it composes with all other modifiers.
- Inventory and caravan mass: a `MassUtility.Capacity` postfix (toggle).
- Ephemeral cache: `ConditionalWeakTable`, `HediffSet.DirtyCache` invalidation, 250-tick expiry and a settings generation counter. Nothing is saved.
- Settings: enable, exponent, mass toggle, non-humanlike toggle, inspect-pane line, debug logging.
- Diagnostics: per-change breakdown log for player pawns, startup report, and dev-mode debug actions (log pawn, log all, benchmark, clear cache).
- Test suites: standalone formula tests, and an integration harness against the real 1.6 Assembly-CSharp and Harmony 2.4.1.
