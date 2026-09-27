# Passion Progression validation — v0.3.0

Base: main commit `59d8350402fb46b16c6e8f572a9313e81b70e839`.

## Results

- `build.sh`: success, no compiler warnings; rebuilt `1.6/Assemblies/Parametric.dll`.
- `Tests/run-tests.sh`: formula suite passes; **all 344 integration checks pass**.
- All 272 existing checks retained, except the expected Harmony patch count changes from four to eight.
- 72 added checks exercise threshold matrices and repeated normalization, real XP crossings at 10/20, real negative-XP decay, large skill reductions, aptitude semantics, saturation/XP preservation, null/missing/disabled skill cases, unknown passion values, faction/race independence, all four lifecycle hooks, setting gates, backfill population selection, native skill/settings Scribe round trips, and old settings defaults.
- Load Support and Overload source files are unchanged.

## Reproduce

Requires Mono (`mcs`, `mono`), the RimWorld managed DLLs and the Harmony mod DLL:

```sh
export RIMWORLD_MANAGED=/path/to/RimWorldWin64_Data/Managed
export HARMONY_DLL=/path/to/0Harmony.dll
./build.sh
./Tests/run-tests.sh
```

The supplied reduced DLL bundle lacks `com.rlabrecque.steamworks.net.dll`. This run used `STEAMWORKS_METADATA_STUB=1`, which compiles `Tests/Support/SteamworksMetadata.cs` into the temporary test directory only. It supplies the opaque `PublishedFileId_t` metadata needed to load `Scenario`; no Steam API behavior is tested or simulated. Prefer the real dependency when available. The stand-in is not part of the shipped mod.

## What this does and does not prove

The harness executes the actual supplied `Assembly-CSharp.dll`, Harmony, built Parametric assembly, `SkillRecord.Learn`, XP/passion fields, native serialization, and the map/caravan/transporter aggregation logic. The existing integration harness substitutes Unity-only behavior and constructs synthetic pawns/defs. The new tests supply cached disability results and stub the Unity-dependent generation/spawn/game-init bodies while invoking their real patched methods. Full content generation, live work-disability calculation, held-pawn and Odyssey gravship discovery, settings layout, and an actual save loaded in Unity require the [runtime checklist](passion-progression-runtime.md).

The existing inventory benchmarks emit synthetic non-destroyable-thing diagnostics, as in the pre-feature test log. The suite reports no Parametric error logs.
