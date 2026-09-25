using RimWorld;
using Verse;
using Verse.AI;

namespace Parametric.Overload
{
    /// <summary>Where and how cargo leaves a pawn. The default uses vanilla drop APIs next to a spawned pawn.</summary>
    public interface IOverloadDropper
    {
        /// <summary>True if things can be placed in the world for this pawn right now (spawned on a map).</summary>
        bool HasDropContext(Pawn pawn);
        /// <summary>Drop <paramref name="count"/> units of an inventory stack (split as needed). False = nothing moved.</summary>
        bool DropFromInventory(Pawn pawn, Thing thing, int count);
        /// <summary>Drop <paramref name="count"/> units of the hand-carried stack. False = nothing moved.</summary>
        bool DropCarried(Pawn pawn, int count);
    }

    /// <summary>
    /// Vanilla placement: ThingOwner.TryDrop / Pawn_CarryTracker.TryDropCarriedThing with ThingPlaceMode.Near at the
    /// pawn's position. Both split the stack with Thing.SplitOff, place it with GenDrop, and on a failed placement
    /// re-absorb the split part (verified in 1.6), so nothing is ever duplicated or destroyed.
    /// </summary>
    public sealed class VanillaOverloadDropper : IOverloadDropper
    {
        public bool HasDropContext(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && !pawn.Dead && pawn.Map != null;
        }

        public bool DropFromInventory(Pawn pawn, Thing thing, int count)
        {
            Thing dropped;
            return pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, count, out dropped);
        }

        public bool DropCarried(Pawn pawn, int count)
        {
            Thing carried = pawn.carryTracker.CarriedThing;
            bool whole = carried != null && count >= carried.stackCount;
            Thing dropped;
            bool ok = pawn.carryTracker.TryDropCarriedThing(pawn.Position, count, ThingPlaceMode.Near, out dropped);
            // A job that was hauling this stack cannot continue with empty hands; end it cleanly (the AI picks a new one).
            if (ok && whole && pawn.carryTracker.CarriedThing == null && pawn.jobs != null && pawn.jobs.curJob != null)
                pawn.jobs.EndCurrentJob(JobCondition.Incompletable);
            return ok;
        }
    }

    public struct OverloadReconcileReport
    {
        public bool Ran;
        public string Skipped;               // why nothing was checked (null if it ran)
        public float Comfortable, Routine, Policy;
        public float MassBefore, MassAfter;
        public int StacksTouched, UnitsDropped;
        public float MassDropped;
        public int CarriedUnitsDropped, CarriedLimit;
        public bool PlacementFailed;
        public bool StillOverRoutine;        // unavoidable mass (apparel/equipment) or nothing droppable left
    }

    /// <summary>
    /// Brings a pawn back under its CURRENT routine limit by shedding droppable cargo, largest stack mass first,
    /// splitting the last stack so only what is needed falls. Never touches apparel, equipment, implants or pawns;
    /// never destroys anything; never raises capacity to fit. What cannot be dropped simply stays and keeps the pawn
    /// reactively overloaded.
    ///
    /// Also sheds the part of a hand-carried stack that exceeds the pawn's CURRENT vanilla hand-carry limit
    /// (Pawn_CarryTracker.MaxStackSpaceEver = CarryingCapacity / VolumePerUnit): an injured thief whose carrying
    /// capacity fell spills the excess of what it is carrying. Only stackable things; carried pawns and corpses are
    /// never dropped.
    /// </summary>
    public static class OverloadReconciler
    {
        public static IOverloadDropper Dropper = new VanillaOverloadDropper();

        private const float Epsilon = 0.001f;
        private const int MaxDropSteps = 64;

        public static OverloadReconcileReport Reconcile(Pawn pawn)
        {
            var r = new OverloadReconcileReport();
            if (!OverloadUtility.Active) { r.Skipped = "Overload disabled"; return r; }
            if (pawn == null || pawn.Dead || pawn.Destroyed) { r.Skipped = "no living pawn"; return r; }
            if (OverloadUtility.IsExempt(pawn)) { r.Skipped = "trade pawn (left to vanilla)"; return r; }
            if (!Dropper.HasDropContext(pawn)) { r.Skipped = "no place to drop (not spawned)"; return r; }

            r.Ran = true;
            OverloadUtility.MarkDirty(pawn);
            r.Policy = OverloadUtility.PolicyFor(pawn);
            r.Comfortable = OverloadUtility.ComfortableCapacity(pawn);
            r.Routine = OverloadFormula.RoutineCapacity(r.Comfortable, r.Policy);
            r.MassBefore = OverloadUtility.ActualSupportedMass(pawn);

            if (pawn.inventory != null && OverloadFormula.IsValidCapacity(r.Comfortable))
                ShedInventory(pawn, ref r);

            ShedHandCarryExcess(pawn, ref r);

            r.MassAfter = OverloadUtility.ActualSupportedMass(pawn);
            r.StillOverRoutine = OverloadFormula.IsValidCapacity(r.Comfortable) && r.MassAfter > r.Routine + Epsilon;

            ParametricSettings s = ParametricMod.Settings;
            if (s != null && s.debugLogging && (r.UnitsDropped > 0 || r.CarriedUnitsDropped > 0 || r.PlacementFailed))
                Log.Message(OverloadLog.Prefix + Describe(pawn, r));
            return r;
        }

        private static void ShedInventory(Pawn pawn, ref OverloadReconcileReport r)
        {
            float excess = r.MassBefore - r.Routine;
            ThingOwner<Thing> inv = pawn.inventory.innerContainer;
            for (int step = 0; step < MaxDropSteps && excess > Epsilon; step++)
            {
                // Largest total stack mass first: fewest stack operations, deterministic, no allocation.
                Thing best = null;
                float bestStackMass = 0f, bestUnitMass = 0f;
                for (int i = 0; i < inv.Count; i++)
                {
                    Thing t = inv[i];
                    if (!IsDroppableCargo(t)) continue;
                    float unit = t.GetStatValue(StatDefOf.Mass, true, -1);
                    if (!(unit > 0f)) continue;
                    float stackMass = unit * t.stackCount;
                    if (stackMass > bestStackMass) { best = t; bestStackMass = stackMass; bestUnitMass = unit; }
                }
                if (best == null) break;

                int units = OverloadFormula.UnitsToDrop(excess, bestUnitMass, best.stackCount);
                if (units <= 0) break;
                if (!Dropper.DropFromInventory(pawn, best, units)) { r.PlacementFailed = true; break; }
                r.StacksTouched++;
                r.UnitsDropped += units;
                r.MassDropped += units * bestUnitMass;
                excess = OverloadUtility.ActualSupportedMass(pawn) - r.Routine; // re-measure: never trust bookkeeping
            }
        }

        private static void ShedHandCarryExcess(Pawn pawn, ref OverloadReconcileReport r)
        {
            Pawn_CarryTracker carry = pawn.carryTracker;
            Thing carried = carry != null ? carry.CarriedThing : null;
            if (carried == null || carried is Pawn || carried is Corpse || carried.def.stackLimit <= 1) return;
            int limit = carry.MaxStackSpaceEver(carried.def);
            if (limit < 0) limit = 0;
            r.CarriedLimit = limit;
            int excessUnits = carried.stackCount - limit;
            if (excessUnits <= 0) return;
            if (Dropper.DropCarried(pawn, excessUnits)) r.CarriedUnitsDropped = excessUnits;
            else r.PlacementFailed = true;
        }

        /// <summary>Ordinary inventory cargo: not a pawn, and not something vanilla destroys when dropped.</summary>
        public static bool IsDroppableCargo(Thing t)
        {
            return t != null && !(t is Pawn) && t.def != null && !t.def.destroyOnDrop && t.stackCount > 0;
        }

        /// <summary>Total mass of droppable inventory cargo (debug display).</summary>
        public static float DroppableCargoMass(Pawn pawn)
        {
            if (pawn == null || pawn.inventory == null) return 0f;
            float m = 0f;
            ThingOwner<Thing> inv = pawn.inventory.innerContainer;
            for (int i = 0; i < inv.Count; i++)
                if (IsDroppableCargo(inv[i])) m += inv[i].GetStatValue(StatDefOf.Mass, true, -1) * inv[i].stackCount;
            return m;
        }

        public static string Describe(Pawn pawn, OverloadReconcileReport r)
        {
            if (!r.Ran) return Parametric.LoadSupport.LoadSupportCalculator.SafeLabel(pawn) + ": reconciliation skipped (" + r.Skipped + ")";
            return Parametric.LoadSupport.LoadSupportCalculator.SafeLabel(pawn)
                   + ": comfortable " + r.Comfortable.ToString("0.#") + " kg, policy " + OverloadFormula.PolicyLabel(r.Policy)
                   + ", routine limit " + r.Routine.ToString("0.#") + " kg, mass " + r.MassBefore.ToString("0.#") + " -> " + r.MassAfter.ToString("0.#")
                   + " kg; dropped " + r.UnitsDropped + " units (" + r.MassDropped.ToString("0.#") + " kg) from " + r.StacksTouched + " stack(s)"
                   + (r.CarriedUnitsDropped > 0 ? ", " + r.CarriedUnitsDropped + " hand-carried units (limit " + r.CarriedLimit + ")" : "")
                   + (r.PlacementFailed ? "; PLACEMENT FAILED, cargo kept" : "")
                   + (r.StillOverRoutine ? "; still over the routine limit (unavoidable gear or nothing left to drop)" : "");
        }
    }
}
