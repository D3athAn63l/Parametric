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
    ///                           NOT part of vanilla mass accounting and is not added here.
    ///
    /// Hand carry is Overload's SECOND channel, in vanilla's own CarryingCapacity system and units:
    ///   COMFORTABLE hand capacity = the real CarryingCapacity stat with only Overload's hand multiplier skipped
    ///   ROUTINE hand capacity     = comfortable hand × (2 − policy) — what vanilla hand hauling sees (StatPart_OverloadHandCarry)
    ///   ACTUAL hand load          = carried stackCount × VolumePerUnit (not Mass)
    ///
    /// Movement factor = min(mass factor, hand factor), each OverloadFormula.ReactiveFactor(actual, comfortable) —
    /// the denominator is always the comfortable capacity of that channel.
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

        // ---------------- Hand-carry channel (vanilla CarryingCapacity / Pawn_CarryTracker) ----------------

        /// <summary>Set while <see cref="ComfortableHandCapacity"/> runs; StatPart_OverloadHandCarry skips only its own multiplier for this pawn.</summary>
        [ThreadStatic] public static Pawn ComfortableHandQueryPawn;

        /// <summary>
        /// The real final CarryingCapacity (vanilla, Manipulation, other mods, Parametric's Manipulation compensation
        /// and Load Support) with ONLY Overload's hand multiplier left out. Uncached. Does not touch
        /// StatPart_LoadSupport.Bypass, so Load Support stays in.
        /// </summary>
        public static float ComfortableHandCapacity(Pawn pawn)
        {
            if (pawn == null || StatDefOf.CarryingCapacity == null) return 0f;
            Pawn previous = ComfortableHandQueryPawn;
            ComfortableHandQueryPawn = pawn;
            try { return pawn.GetStatValue(StatDefOf.CarryingCapacity, true, -1); }
            finally { ComfortableHandQueryPawn = previous; }
        }

        /// <summary>What vanilla hand hauling sees (JobGiver_Steal, MaxStackSpaceEver, haul jobs): the real CarryingCapacity.</summary>
        public static float RoutineHandCapacity(Pawn pawn)
        {
            if (pawn == null || StatDefOf.CarryingCapacity == null) return 0f;
            return pawn.GetStatValue(StatDefOf.CarryingCapacity, true, -1);
        }

        /// <summary>
        /// The hand-carried stack, if it belongs to the hand channel: carried pawns and corpses are Burden territory
        /// (not modelled here) and are ignored.
        /// </summary>
        public static Thing HandStack(Pawn pawn)
        {
            Thing t = pawn != null && pawn.carryTracker != null ? pawn.carryTracker.CarriedThing : null;
            if (t == null || t is Pawn || t is Corpse || t.def == null) return null;
            return t;
        }

        /// <summary>
        /// Hand load in CarryingCapacity units: stackCount × ThingDef.VolumePerUnit — the same relation vanilla uses
        /// (Pawn_CarryTracker.MaxStackSpaceEver = CarryingCapacity / VolumePerUnit; JobGiver_Steal count =
        /// CarryingCapacity / VolumePerUnit). NOT the Mass stat: the two carrying systems have different units.
        /// </summary>
        public static float ActualHandLoad(Pawn pawn)
        {
            Thing t = HandStack(pawn);
            return t == null ? 0f : t.stackCount * t.def.VolumePerUnit;
        }

        // ---------------- Combined reactive factor ----------------

        public enum Channel { None, Mass, Hand }

        /// <summary>Both channels of one evaluation (used by the StatPart, the gizmo tooltip and debug).</summary>
        public struct State
        {
            public float Mass, ComfortableMass, MassFactor;
            public float HandLoad, ComfortableHand, HandFactor;
            public float Factor;          // min(MassFactor, HandFactor), or 1 if not applicable / exempt
            public Channel Limiting;      // which channel set Factor (None when Factor = 1)
            public bool HasHandStack;
        }

        /// <summary>Current reactive movement factor (1 when not overloaded, not applicable, or exempt).</summary>
        public static float CurrentFactor(Pawn pawn)
        {
            return Evaluate(pawn).Factor;
        }

        /// <summary>Set while this thread computes a pawn's factor: if another mod's capacity code reads MoveSpeed, no infinite loop.</summary>
        [ThreadStatic] private static Pawn computingFactorFor;

        /// <summary>
        /// Mass channel:  ReactiveFactor(GearMass + InventoryMass, comfortable mass capacity)
        /// Hand channel:  ReactiveFactor(stackCount × VolumePerUnit, comfortable CarryingCapacity)
        /// Result:        min(mass, hand) — the worse channel decides. Never the product: the two vanilla carrying
        ///                systems have different units and semantics, and multiplying would punish a pawn twice.
        /// </summary>
        public static State Evaluate(Pawn pawn)
        {
            var st = new State { MassFactor = 1f, HandFactor = 1f, Factor = 1f };
            if (!Active || pawn == null || pawn.Dead || !InWalkingContext(pawn)) return st;
            if (computingFactorFor == pawn) return st;

            st.Mass = ActualSupportedMass(pawn);
            Thing hand = HandStack(pawn);
            st.HasHandStack = hand != null;
            if (hand != null) st.HandLoad = hand.stackCount * hand.def.VolumePerUnit;
            if (!(st.Mass > 0f) && !(st.HandLoad > 0f)) return st;

            Pawn previous = computingFactorFor;
            computingFactorFor = pawn;
            try
            {
                if (st.Mass > 0f)
                {
                    st.ComfortableMass = ComfortableCapacityCached(pawn);
                    st.MassFactor = OverloadFormula.ReactiveFactor(st.Mass, st.ComfortableMass);
                }
                if (st.HandLoad > 0f)
                {
                    st.ComfortableHand = ComfortableHandCapacityCached(pawn);
                    st.HandFactor = OverloadFormula.ReactiveFactor(st.HandLoad, st.ComfortableHand);
                }
            }
            finally { computingFactorFor = previous; }

            float f = Math.Min(st.MassFactor, st.HandFactor);
            if (f < 1f && IsExempt(pawn)) return st; // Factor stays 1
            st.Factor = f;
            st.Limiting = f >= 1f ? Channel.None : (st.HandFactor < st.MassFactor ? Channel.Hand : Channel.Mass);
            return st;
        }

        // ---------------- Per-tick comfortable-capacity cache (movement hot path) ----------------

        private sealed class Entry
        {
            public int Tick = int.MinValue;       // comfortable mass capacity
            public int Generation = -1;
            public float Comfortable;
            public int HandTick = int.MinValue;   // comfortable hand capacity
            public int HandGeneration = -1;
            public float ComfortableHand;
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

        /// <summary>Comfortable CarryingCapacity reused within one game tick; same invalidation as the mass value.</summary>
        public static float ComfortableHandCapacityCached(Pawn pawn)
        {
            int now = CurrentTick();
            if (now == int.MinValue) return ComfortableHandCapacity(pawn);
            Entry e = table.GetValue(pawn, createEntry);
            if (e.HandTick == now && e.HandGeneration == generation) return e.ComfortableHand;
            e.ComfortableHand = ComfortableHandCapacity(pawn);
            e.HandTick = now;
            e.HandGeneration = generation;
            return e.ComfortableHand;
        }

        public static void MarkDirty(Pawn pawn)
        {
            Entry e;
            if (pawn != null && table.TryGetValue(pawn, out e)) { e.Tick = int.MinValue; e.HandTick = int.MinValue; }
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
