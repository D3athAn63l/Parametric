using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace Parametric.Overload
{
    /// <summary>
    /// Overload's only saved state, plus the deferred reconciliation queue.
    ///
    /// PERSISTENCE
    ///   Player pawns' individual policies are player state, so they live in this GameComponent (saved with the game,
    ///   travels with caravans and map changes because it is keyed by the pawn's permanent thingIDNumber, not by map).
    ///   Only explicit overrides are stored: a pawn set back to the player default is removed and inherits it again.
    ///   Non-player pawns never get records; they use the global non-player default from the mod settings.
    ///   Records of pawns that no longer exist anywhere (maps, world, caravans, travelling transporters, alive or
    ///   dead) are pruned when saving.
    ///
    /// RECONCILIATION QUEUE (event-driven, coalesced)
    ///   Capacity-affecting events (HediffSet.DirtyCache, a Load Support drop found by the cache's timer, a stricter
    ///   policy, a settings change) call <see cref="Notify_CapacityMayHaveDropped"/> / <see cref="RequestReconciliation"/>.
    ///   Each pawn is queued once with a due tick; further events before it is due change nothing, so a burst of hits
    ///   becomes one reconciliation. Nothing is dropped from inside DirtyCache: the queue is drained in
    ///   GameComponentTick, which returns immediately when the queue is empty (no colony scans, no polling).
    /// </summary>
    public class OverloadGameComponent : GameComponent
    {
        /// <summary>Events within this window (≈0.5 s at 1×) collapse into one reconciliation.</summary>
        public const int CoalesceDelayTicks = 30;

        public static OverloadGameComponent Instance;

        private Dictionary<int, float> policies = new Dictionary<int, float>();
        private readonly Dictionary<Pawn, int> pending = new Dictionary<Pawn, int>();
        private readonly List<Pawn> due = new List<Pawn>();

        /// <summary>Diagnostics/tests: how many reconciliations have run in this session.</summary>
        public int ReconciliationsRun;

        public OverloadGameComponent(Game game) { Instance = this; }
        public OverloadGameComponent() { Instance = this; }

        // ---------------- Policies ----------------

        public int PolicyRecordCount { get { return policies.Count; } }

        public bool TryGetPolicy(Pawn pawn, out float policy)
        {
            policy = 1f;
            return pawn != null && policies.TryGetValue(pawn.thingIDNumber, out policy);
        }

        /// <summary>Sets an individual policy. Equal to the player default → the record is removed (inherits the default).</summary>
        public void SetPolicy(Pawn pawn, float policy, float playerDefault)
        {
            if (pawn == null) return;
            float p = OverloadFormula.SanitizePolicy(policy);
            if (Math.Abs(p - OverloadFormula.SanitizePolicy(playerDefault)) < 0.0001f) policies.Remove(pawn.thingIDNumber);
            else policies[pawn.thingIDNumber] = p;
        }

        public void ClearPolicy(Pawn pawn)
        {
            if (pawn != null) policies.Remove(pawn.thingIDNumber);
        }

        // ---------------- Reconciliation queue ----------------

        public int PendingCount { get { return pending.Count; } }

        public bool IsQueued(Pawn pawn)
        {
            return pawn != null && pending.ContainsKey(pawn);
        }

        /// <summary>Queue once; a later request can only bring the due tick forward, never postpone or duplicate it.</summary>
        public void RequestReconciliation(Pawn pawn, int delayTicks)
        {
            if (pawn == null) return;
            int dueTick = OverloadUtility.CurrentTick() + Math.Max(0, delayTicks);
            int existing;
            if (pending.TryGetValue(pawn, out existing) && existing <= dueTick) return;
            pending[pawn] = dueTick;
        }

        /// <summary>
        /// Cheap entry point for capacity events. Allocation-free; ignores pawns that carry nothing droppable or have no
        /// place to drop things (caravans, world pawns), and events raised while a game is loading.
        /// </summary>
        public static void Notify_CapacityMayHaveDropped(Pawn pawn)
        {
            OverloadGameComponent comp = Instance;
            if (comp == null || pawn == null || !OverloadUtility.Active) return;
            if (Scribe.mode != LoadSaveMode.Inactive) return; // loading/saving: the state did not change, it was restored
            if (!OverloadUtility.HasDroppableCargo(pawn)) return;
            if (!OverloadReconciler.Dropper.HasDropContext(pawn)) return;
            comp.RequestReconciliation(pawn, CoalesceDelayTicks);
        }

        public override void GameComponentTick()
        {
            if (pending.Count == 0) return;
            ProcessDue(OverloadUtility.CurrentTick());
        }

        /// <summary>Runs every queued reconciliation whose due tick has passed. Public for tests and debug actions.</summary>
        public int ProcessDue(int now)
        {
            if (pending.Count == 0) return 0;
            due.Clear();
            foreach (KeyValuePair<Pawn, int> kv in pending)
                if (kv.Value <= now) due.Add(kv.Key);
            for (int i = 0; i < due.Count; i++)
            {
                Pawn p = due[i];
                pending.Remove(p);
                try
                {
                    OverloadReconciler.Reconcile(p);
                    ReconciliationsRun++;
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce(OverloadLog.Prefix + "Reconciliation failed for " + Parametric.LoadSupport.LoadSupportCalculator.SafeLabel(p) + ": " + ex,
                                  ("Parametric_Overload_reconcile_" + p.thingIDNumber).GetHashCode());
                }
            }
            int n = due.Count;
            due.Clear();
            return n;
        }

        // ---------------- Save / load ----------------

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving) PruneStale();
            Scribe_Collections.Look(ref policies, "policies", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (policies == null) policies = new Dictionary<int, float>();
                pending.Clear();
                Instance = this;
            }
        }

        /// <summary>
        /// Drops records of pawns that exist nowhere any more. The population is vanilla's widest pawn collection,
        /// PawnsFinder.All_AliveOrDead, which in 1.6 is AllMapsWorldAndTemporary_AliveOrDead (map pawns, world pawns
        /// alive and dead, temporary pawns, gravship pawns) PLUS AllCaravansAndTravellingTransporters_AliveOrDead
        /// (caravan members and pawns in travelling transporters). Caravan pawns are NOT in the first collection, so
        /// using it alone would delete the policy of every pawn that is away in a caravan when the game is saved.
        /// </summary>
        private void PruneStale()
        {
            if (policies.Count == 0) return;
            try
            {
                PruneStaleAgainst(PawnsFinder.All_AliveOrDead);
            }
            catch
            {
                // Pruning is housekeeping only; never block a save.
            }
        }

        /// <summary>Removes records whose pawn is not in <paramref name="existing"/>. Returns how many were removed. Public for tests.</summary>
        public int PruneStaleAgainst(IEnumerable<Pawn> existing)
        {
            if (policies.Count == 0 || existing == null) return 0;
            var alive = new HashSet<int>();
            foreach (Pawn p in existing)
                if (p != null) alive.Add(p.thingIDNumber);
            if (alive.Count == 0) return 0; // no world (tests / odd states): never prune blindly
            var stale = new List<int>();
            foreach (int id in policies.Keys) if (!alive.Contains(id)) stale.Add(id);
            for (int i = 0; i < stale.Count; i++) policies.Remove(stale[i]);
            return stale.Count;
        }
    }

    public static class OverloadLog
    {
        public const string Prefix = "[Parametric:Overload] ";
    }
}
