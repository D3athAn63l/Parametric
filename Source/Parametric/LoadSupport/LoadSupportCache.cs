using System;
using System.Runtime.CompilerServices;
using Verse;

namespace Parametric.LoadSupport
{
    /// <summary>
    /// Ephemeral, per-session cache of LoadSupport results.
    ///
    ///  • Keyed by the Pawn object itself through a ConditionalWeakTable: entries die with the pawn,
    ///    thingIDs from a previous save can never collide, nothing leaks across game loads.
    ///  • Never serialised. Nothing in the save file references this mod's data, so removing a mod that
    ///    supplied a bionic (or removing this mod) cannot leave dangling references.
    ///  • Invalidation:
    ///      1. Event: HediffSet.DirtyCache() (fires on hediff add/remove/change, part loss, prosthetic install,
    ///         restoration...) marks the entry dirty. See HarmonyPatches.
    ///         In 1.6 DirtyCache is called from AddDirect, RemoveHediff, Notify_HediffChanged (injury healing,
    ///         stage/severity changes), RestorePart, Notify_Resurrected, Notify_GenesChanged, Kill and load.
    ///      2. Time: an entry older than RefreshIntervalTicks is recomputed on next request. This is only the safety
    ///         net for what the event misses (stat-driven setMax curves / capacityFactorEffectMultiplier, exotic mods).
    ///      3. Settings change / explicit clear: a global generation counter.
    ///  • Lookups are allocation-free; recomputation runs at most once per pawn per interval, and only when
    ///    something actually asks for the value (no ticking).
    /// </summary>
    public static class LoadSupportCache
    {
        /// <summary>
        /// Safety-net expiry: 1000 ticks ≈ 16.7 s at 1× speed. Raised from 250 in 0.1.1 because the DirtyCache event
        /// covers every standard body change (see above); the timer only catches non-evented drift.
        /// </summary>
        public const int RefreshIntervalTicks = 1000;

        private sealed class Entry
        {
            public LoadSupportResult Result = LoadSupportResult.Neutral;
            public int ComputedTick = int.MinValue;
            public int Generation = -1;
            public bool Dirty = true;
            public bool HasEverComputed;
        }

        private static ConditionalWeakTable<Pawn, Entry> table = new ConditionalWeakTable<Pawn, Entry>();
        private static readonly ConditionalWeakTable<Pawn, Entry>.CreateValueCallback createEntry = _ => new Entry();
        private static int generation;

        /// <summary>Main entry point. Returns the pawn's current LoadSupport multiplier (always finite and > 0).</summary>
        public static float Get(Pawn pawn)
        {
            return GetResult(pawn).LoadSupport;
        }

        public static LoadSupportResult GetResult(Pawn pawn, bool forceRecalculate = false)
        {
            if (pawn == null) return LoadSupportResult.Neutral;

            Entry e = table.GetValue(pawn, createEntry);
            int now = CurrentTick();

            bool stale = forceRecalculate
                         || e.Dirty
                         || e.Generation != generation
                         || now == int.MinValue                       // no game running (main menu, def previews)
                         || now < e.ComputedTick                      // tick went backwards (new game / load)
                         || (long)now - e.ComputedTick >= RefreshIntervalTicks;

            if (!stale) return e.Result;

            LoadSupportResult previous = e.Result;
            bool hadPrevious = e.HasEverComputed;

            LoadSupportResult fresh = LoadSupportCalculator.Calculate(pawn);

            e.Result = fresh;
            e.ComputedTick = now;
            e.Generation = generation;
            e.Dirty = false;
            e.HasEverComputed = true;

            // Auto-logging is limited to the player's own pawns to keep the log readable on big maps;
            // the dev-mode debug actions cover everyone else.
            if (ParametricMod.Settings != null && ParametricMod.Settings.debugLogging
                && pawn.Faction != null && pawn.Faction.IsPlayer)
            {
                if (!hadPrevious || Math.Abs(previous.LoadSupport - fresh.LoadSupport) > 0.005f)
                    Parametric.Debug.ParametricDebug.LogChange(pawn, hadPrevious ? previous.LoadSupport : (float?)null, fresh);
            }
            return fresh;
        }

        /// <summary>Called from the HediffSet.DirtyCache postfix. Allocation-free; no-op for pawns we never measured.</summary>
        public static void MarkDirty(Pawn pawn)
        {
            if (pawn == null) return;
            Entry e;
            if (table.TryGetValue(pawn, out e)) e.Dirty = true;
        }

        /// <summary>Invalidate everything (settings changed, debug action).</summary>
        public static void InvalidateAll()
        {
            unchecked { generation++; }
        }

        /// <summary>Drop all entries (e.g. debug action). Normally unnecessary because entries are weak.</summary>
        public static void Clear()
        {
            table = new ConditionalWeakTable<Pawn, Entry>();
            InvalidateAll();
        }

        private static int CurrentTick()
        {
            try
            {
                if (Current.Game == null || Find.TickManager == null) return int.MinValue;
                return Find.TickManager.TicksGame;
            }
            catch
            {
                return int.MinValue;
            }
        }
    }
}
