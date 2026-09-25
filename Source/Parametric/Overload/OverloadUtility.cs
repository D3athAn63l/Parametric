using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace Parametric.Overload
{
    /// <summary>
    /// The three Overload quantities, all derived from RimWorld's own APIs:
    ///
    ///   COMFORTABLE capacity  = MassUtility.Capacity with every legitimate influence (vanilla, BodySize, other mods'
    ///                           hooks and stats, Load Support) but WITHOUT Overload. Obtained by calling the real,
    ///                           patched MassUtility.Capacity while a per-thread "comfortable query" marker names this
    ///                           pawn; the Capacity postfix then skips only the Overload step for that pawn.
    ///   ROUTINE capacity      = comfortable × (2 − policy). This is what MassUtility.Capacity returns to everyone else
    ///                           (vanilla caravan/pack/pickup logic and any hauling mod that asks the normal API).
    ///   ACTUAL supported mass = MassUtility.GearMass + MassUtility.InventoryMass (= vanilla GearAndInventoryMass):
    ///                           worn apparel, equipment and inventory. The hand-carried thing (Pawn_CarryTracker) is
    ///                           NOT part of vanilla mass accounting and is not added: hand hauling is governed by the
    ///                           separate CarryingCapacity stat (vanilla's MaxStackSpaceEver). Counting it here would
    ///                           slow every ordinary hauler and govern hand carry twice.
    ///
    /// Movement factor = OverloadFormula.ReactiveFactor(actual, comfortable) — the denominator is always comfortable.
    /// </summary>
    public static class OverloadUtility
    {
        /// <summary>Set while <see cref="ComfortableCapacity"/> runs; the MassUtility.Capacity postfix skips Overload for this pawn.</summary>
        [ThreadStatic] public static Pawn ComfortableQueryPawn;

        public static bool Active
        {
            get { ParametricSettings s = ParametricMod.Settings; return s != null && s.overloadEnabled; }
        }

        // ---------------- Policy ----------------

        public static bool IsPlayerPawn(Pawn pawn)
        {
            Faction f = pawn != null ? pawn.Faction : null;
            return f != null && f.IsPlayer;
        }

        /// <summary>
        /// Non-player pawns that take part in vanilla trade (a trader, or a member of a trade-caravan lord). Vanilla's
        /// trader-caravan generator assigns wares to carriers by stack count, not mass (verified in 1.6:
        /// PawnGroupKindWorker_Trader.GenerateCarriers), so their loads routinely exceed their own capacity by design.
        /// They are left entirely to vanilla (no exposed capacity, no slowdown, no spill).
        /// </summary>
        public static bool IsTradePawn(Pawn pawn)
        {
            if (pawn == null) return false;
            if (pawn.trader != null && pawn.trader.traderKind != null) return true;
            Lord lord = pawn.MapHeld != null ? pawn.GetLord() : null;
            return lord != null && lord.LordJob is LordJob_TradeWithColony;
        }

        public static bool IsExempt(Pawn pawn)
        {
            return pawn != null && !IsPlayerPawn(pawn) && IsTradePawn(pawn);
        }

        /// <summary>Effective policy (1 = no overload). Hot path of MassUtility.Capacity: dictionary lookup at most.</summary>
        public static float PolicyFor(Pawn pawn)
        {
            ParametricSettings s = ParametricMod.Settings;
            if (s == null || !s.overloadEnabled || pawn == null) return 1f;
            if (IsPlayerPawn(pawn))
            {
                float p;
                OverloadGameComponent comp = OverloadGameComponent.Instance;
                if (comp != null && comp.TryGetPolicy(pawn, out p)) return OverloadFormula.SanitizePolicy(p);
                return OverloadFormula.SanitizePolicy(s.overloadPlayerDefault);
            }
            float npc = OverloadFormula.SanitizePolicy(s.overloadNonPlayerDefault);
            if (npc >= 1f) return 1f;             // default: nothing to decide, skip the trade check
            return IsExempt(pawn) ? 1f : npc;
        }

        public static bool HasIndividualPolicy(Pawn pawn)
        {
            float p;
            OverloadGameComponent comp = OverloadGameComponent.Instance;
            return comp != null && IsPlayerPawn(pawn) && comp.TryGetPolicy(pawn, out p);
        }

        /// <summary>Sets a player pawn's policy; a stricter routine limit schedules a reconciliation for the next tick.</summary>
        public static void SetPolicy(Pawn pawn, float policy)
        {
            OverloadGameComponent comp = OverloadGameComponent.Instance;
            ParametricSettings s = ParametricMod.Settings;
            if (comp == null || s == null || pawn == null) return;
            float before = PolicyFor(pawn);
            comp.SetPolicy(pawn, policy, s.overloadPlayerDefault);
            float after = PolicyFor(pawn);
            if (OverloadFormula.RoutineMultiplier(after) < OverloadFormula.RoutineMultiplier(before) && HasDroppableCargo(pawn))
                comp.RequestReconciliation(pawn, 0);
        }

        /// <summary>Applies a policy to every eligible pawn in <paramref name="candidates"/>. Returns how many were set.</summary>
        public static int ApplyToPawns(float policy, IEnumerable<Pawn> candidates)
        {
            int n = 0;
            if (candidates == null) return 0;
            foreach (Pawn p in candidates)
            {
                if (!CanUseGizmo(p) || p.IsPrisoner) continue;
                SetPolicy(p, policy);
                n++;
            }
            return n;
        }

        /// <summary>All living player-faction pawns on maps, in caravans and in travelling transporters (vanilla's collection).</summary>
        public static int ApplyToColony(float policy)
        {
            return ApplyToPawns(policy, PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction);
        }

        /// <summary>Player pawns that can carry anything at all (colonists, slaves, pack animals…; any race). No hostiles, prisoners or guests.</summary>
        public static bool CanUseGizmo(Pawn pawn)
        {
            return Active && pawn != null && !pawn.Dead && IsPlayerPawn(pawn) && !pawn.IsPrisoner && MassUtility.CanEverCarryAnything(pawn);
        }

        // ---------------- Capacities and mass ----------------

        /// <summary>Real mass capacity without Overload (Load Support and every other mod's influence included). Uncached.</summary>
        public static float ComfortableCapacity(Pawn pawn)
        {
            if (pawn == null) return 0f;
            Pawn previous = ComfortableQueryPawn;
            ComfortableQueryPawn = pawn;
            try { return MassUtility.Capacity(pawn, null); }
            finally { ComfortableQueryPawn = previous; }
        }

        /// <summary>What ordinary loading sees: the real MassUtility.Capacity (comfortable × routine multiplier).</summary>
        public static float RoutineCapacity(Pawn pawn)
        {
            return pawn == null ? 0f : MassUtility.Capacity(pawn, null);
        }

        /// <summary>Vanilla gear + inventory mass (apparel, equipment, inventory). The hand-carried thing is not included (see class doc).</summary>
        public static float ActualSupportedMass(Pawn pawn)
        {
            if (pawn == null) return 0f;
            float m = MassUtility.GearMass(pawn);
            if (pawn.inventory != null) m += MassUtility.InventoryMass(pawn);
            return m;
        }

        public static bool HasDroppableCargo(Pawn pawn)
        {
            if (pawn == null) return false;
            if (pawn.inventory != null && pawn.inventory.innerContainer.Count > 0) return true;
            return pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null;
        }

        /// <summary>
        /// Movement physics apply to pawns on a map (and to free-standing pawns, e.g. in tests). Pawns inside another
        /// holder (caravans, transporters, being carried) do not walk; vanilla caravan speed uses mass usage/capacity,
        /// not pawn MoveSpeed.
        /// </summary>
        public static bool InWalkingContext(Pawn pawn)
        {
            return pawn.Spawned || pawn.ParentHolder == null;
        }

        /// <summary>Current reactive movement factor (1 when not overloaded, not applicable, or exempt).</summary>
        public static float CurrentFactor(Pawn pawn)
        {
            float mass, comfortable;
            return CurrentFactor(pawn, out mass, out comfortable);
        }

        /// <summary>Set while this thread computes a pawn's factor: if another mod's mass-capacity code reads MoveSpeed, no infinite loop.</summary>
        [ThreadStatic] private static Pawn computingFactorFor;

        public static float CurrentFactor(Pawn pawn, out float mass, out float comfortable)
        {
            mass = 0f; comfortable = 0f;
            if (!Active || pawn == null || pawn.Dead || !InWalkingContext(pawn)) return 1f;
            if (computingFactorFor == pawn) return 1f;
            mass = ActualSupportedMass(pawn);
            if (!(mass > 0f)) return 1f;
            Pawn previous = computingFactorFor;
            computingFactorFor = pawn;
            try { comfortable = ComfortableCapacityCached(pawn); }
            finally { computingFactorFor = previous; }
            float f = OverloadFormula.ReactiveFactor(mass, comfortable);
            if (f < 1f && IsExempt(pawn)) return 1f;
            return f;
        }

        // ---------------- Per-tick comfortable-capacity cache (movement hot path) ----------------

        private sealed class Entry
        {
            public int Tick = int.MinValue;
            public int Generation = -1;
            public float Comfortable;
        }

        private static ConditionalWeakTable<Pawn, Entry> table = new ConditionalWeakTable<Pawn, Entry>();
        private static readonly ConditionalWeakTable<Pawn, Entry>.CreateValueCallback createEntry = _ => new Entry();
        private static int generation;

        /// <summary>
        /// Comfortable capacity reused within one game tick (mass is always read live, so movement stays reactive).
        /// Dropped by HediffSet.DirtyCache and by settings changes; recomputed on the next tick in any case.
        /// </summary>
        public static float ComfortableCapacityCached(Pawn pawn)
        {
            int now = CurrentTick();
            if (now == int.MinValue) return ComfortableCapacity(pawn);
            Entry e = table.GetValue(pawn, createEntry);
            if (e.Tick == now && e.Generation == generation) return e.Comfortable;
            e.Comfortable = ComfortableCapacity(pawn);
            e.Tick = now;
            e.Generation = generation;
            return e.Comfortable;
        }

        public static void MarkDirty(Pawn pawn)
        {
            Entry e;
            if (pawn != null && table.TryGetValue(pawn, out e)) e.Tick = int.MinValue;
        }

        public static void InvalidateAll()
        {
            unchecked { generation++; }
        }

        public static int CurrentTick()
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
