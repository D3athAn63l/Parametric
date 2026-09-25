using System;
using System.Collections.Generic;
using System.Text;
using LudeonTK;
using Parametric.LoadSupport;
using RimWorld;
using Verse;

namespace Parametric.Debug
{
    /// <summary>
    /// Diagnostics. Nothing here runs unless debug logging is enabled in mod settings or a dev-mode
    /// debug action is used. Automatic logging fires only when a player pawn's value is first measured or
    /// changes by more than 0.005, so an idle colony produces no log lines.
    /// </summary>
    public static class ParametricDebug
    {
        public static void LogChange(Pawn pawn, float? previous, LoadSupportResult r)
        {
            string header = previous.HasValue
                ? LoadSupportLog.Prefix + LoadSupportCalculator.SafeLabel(pawn) + " changed " + previous.Value.ToString("0.00") + " -> " + r.LoadSupport.ToString("0.00")
                : LoadSupportLog.Prefix + LoadSupportCalculator.SafeLabel(pawn) + " measured " + r.LoadSupport.ToString("0.00");
            Log.Message(header + "\n" + Describe(pawn, r));
        }

        /// <summary>Full breakdown in the format used for testing modded bionics.</summary>
        public static string Describe(Pawn pawn, LoadSupportResult r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Pawn: " + LoadSupportCalculator.SafeLabel(pawn));
            sb.AppendLine("Body analysis (efficiency -> strength):");
            AppendRegion(sb, "Lower Body", r.HasLower, r.LowerEfficiency, r.LowerStrength);
            AppendRegion(sb, "Core", r.HasCore, r.CoreEfficiency, r.CoreStrength);
            AppendRegion(sb, "Upper Body", r.HasUpper, r.UpperEfficiency, r.UpperStrength);
            sb.AppendLine("Weighted Support (no bottleneck): " + r.WeightedSupport.ToString("0.###"));
            sb.AppendLine("Structural chain (legs+core harmonic): " + r.StructuralSupport.ToString("0.###"));
            sb.AppendLine("Structural/Bottleneck Factor: " + r.BottleneckFactor.ToString("0.###"));
            sb.AppendLine("Final LoadSupport: " + r.LoadSupport.ToString("0.###") + (r.Fallback ? "  (neutral fallback)" : ""));

            try
            {
                ManipulationCompensation.Info comp;
                ManipulationCompensation.Compute(StatDefOf.CarryingCapacity, pawn, r, out comp);
                if (comp.Applied)
                    sb.AppendLine("Manipulation compensation: level " + comp.ManipulationLevel.ToStringPercent()
                                  + ", limbs " + comp.LimbEfficiency.ToStringPercent()
                                  + ", with 100% limbs " + comp.NormalizedLevel.ToStringPercent()
                                  + (comp.ExactNeutral ? " (exact)" : " (ratio fallback)")
                                  + ", vanilla factor x" + comp.VanillaFactor.ToString("0.###")
                                  + " -> limb-normalised x" + comp.NormalizedFactor.ToString("0.###")
                                  + "  => x" + comp.Multiplier.ToString("0.###"));
                else if (comp.BelowNormalLimbs)
                    sb.AppendLine("Manipulation compensation: limbs " + comp.LimbEfficiency.ToStringPercent()
                                  + " <= 100%, vanilla Manipulation penalty kept (x1)");
                else
                    sb.AppendLine("Manipulation compensation: not applicable (x1)");

                float baseCarry, finalCarry, baseMass, finalMass;
                MeasureCapacities(pawn, out baseCarry, out finalCarry, out baseMass, out finalMass);
                sb.AppendLine("Vanilla/Base Carry Capacity: " + baseCarry.ToString("0.#") + " kg");
                sb.AppendLine("Final Carry Capacity: " + finalCarry.ToString("0.#") + " kg");
                sb.AppendLine("Base Inventory/Caravan Mass Capacity: " + baseMass.ToString("0.#") + " kg");
                sb.Append("Final Inventory/Caravan Mass Capacity: " + finalMass.ToString("0.#") + " kg");
                if (ParametricMod.Settings != null && !ParametricMod.Settings.LoadSupportAppliesTo(pawn))
                    sb.Append("\n(NOTE: Load Support disabled for this pawn by settings; final = base)");
            }
            catch (Exception ex)
            {
                sb.Append("Capacity read failed: " + ex.Message);
            }
            return sb.ToString();
        }

        private static void AppendRegion(StringBuilder sb, string name, bool present, float eff, float strength)
        {
            if (present)
                sb.AppendLine("  - " + name + ": " + strength.ToString("0.00") + "  (efficiency " + eff.ToStringPercent() + ")");
            else
                sb.AppendLine("  - " + name + ": not present in this anatomy (weight redistributed)");
        }

        /// <summary>Reads both capacities with and without Load Support (bypass flag; no stat caching involved).</summary>
        public static void MeasureCapacities(Pawn pawn, out float baseCarry, out float finalCarry, out float baseMass, out float finalMass)
        {
            StatDef stat = StatDefOf.CarryingCapacity;
            finalCarry = pawn.GetStatValue(stat, true, -1);
            finalMass = MassUtility.Capacity(pawn, null);
            bool old = StatPart_LoadSupport.Bypass;
            StatPart_LoadSupport.Bypass = true;
            try
            {
                baseCarry = pawn.GetStatValue(stat, true, -1);
                baseMass = MassUtility.Capacity(pawn, null);
            }
            finally
            {
                StatPart_LoadSupport.Bypass = old;
            }
        }

        public static void LogStartupReport()
        {
            var sb = new StringBuilder(ParametricMod.LogPrefix + "Startup report\n");
            try
            {
                StatDef stat = StatDefOf.CarryingCapacity;
                sb.AppendLine("CarryingCapacity: defaultBaseValue=" + stat.defaultBaseValue + " min=" + stat.minValue + " max=" + stat.maxValue
                              + " immutable=" + stat.immutable + " cacheable=" + stat.cacheable);
                if (!stat.capacityFactors.NullOrEmpty())
                    foreach (PawnCapacityFactor f in stat.capacityFactors)
                        sb.AppendLine("  capacityFactor: " + f.capacity + " weight=" + f.weight + " max=" + f.max
                                      + " allowedDefect=" + f.allowedDefect + " useReciprocal=" + f.useReciprocal
                                      + (f.capacity == PawnCapacityDefOf.Manipulation ? "  <- compensated by Load Support" : ""));
                if (!stat.statFactors.NullOrEmpty())
                    foreach (StatDef f in stat.statFactors)
                        sb.AppendLine("  statFactor: " + f.defName);
                if (!stat.parts.NullOrEmpty())
                    for (int i = 0; i < stat.parts.Count; i++)
                        sb.AppendLine("  part[" + i + "]: " + stat.parts[i].GetType().FullName + " priority=" + stat.parts[i].priority);
                sb.Append("Harmony patches owned by " + ParametricMod.HarmonyId + ": ");
                var ids = new List<string>();
                foreach (System.Reflection.MethodBase m in HarmonyLib.Harmony.GetAllPatchedMethods())
                {
                    HarmonyLib.Patches info = HarmonyLib.Harmony.GetPatchInfo(m);
                    if (info != null && info.Owners.Contains(ParametricMod.HarmonyId))
                        ids.Add(m.DeclaringType.Name + "." + m.Name);
                }
                sb.Append(string.Join(", ", ids.ToArray()));
            }
            catch (Exception ex)
            {
                sb.Append("report failed: " + ex.Message);
            }
            Log.Message(sb.ToString());
        }

        // ------------------------------------------------------------------
        // Dev-mode debug actions (Debug actions menu → "Parametric")
        // ------------------------------------------------------------------

        [DebugAction("Parametric", "Load Support: log pawn (click)", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugLogPawn(Pawn p)
        {
            if (p == null) return;
            LoadSupportResult r = LoadSupportCache.GetResult(p, true);
            Log.Message(LoadSupportLog.Prefix + "\n" + Describe(p, r));
        }

        [DebugAction("Parametric", "Load Support: log all pawns on map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugLogAll()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            var sb = new StringBuilder(LoadSupportLog.Prefix + "All spawned pawns on current map\n");
            int n = 0;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p == null || p.RaceProps == null) continue;
                LoadSupportResult r = LoadSupportCache.GetResult(p, true);
                float comp = ManipulationCompensation.Compute(StatDefOf.CarryingCapacity, p, r);
                sb.AppendLine(LoadSupportCalculator.SafeLabel(p)
                              + "  L=" + r.LowerStrength.ToString("0.00")
                              + " C=" + r.CoreStrength.ToString("0.00")
                              + " U=" + r.UpperStrength.ToString("0.00")
                              + "  bottleneck=" + r.BottleneckFactor.ToString("0.00")
                              + "  LoadSupport=" + r.LoadSupport.ToString("0.00")
                              + "  manipComp=" + comp.ToString("0.00")
                              + (ParametricMod.Settings.LoadSupportAppliesTo(p) ? "" : "  [not applied]"));
                n++;
            }
            sb.Append(n + " pawns.");
            Log.Message(sb.ToString());
        }

        [DebugAction("Parametric", "Load Support: benchmark (all pawns on map)", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugBenchmark()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            var pawns = new List<Pawn>(map.mapPawns.AllPawnsSpawned);
            int count = Math.Max(1, pawns.Count);
            const int rounds = 20;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int k = 0; k < rounds; k++)
                for (int i = 0; i < pawns.Count; i++)
                    LoadSupportCalculator.Calculate(pawns[i]);
            sw.Stop();
            double uncachedUs = sw.Elapsed.TotalMilliseconds * 1000.0 / (rounds * count);

            sw.Reset(); sw.Start();
            for (int k = 0; k < rounds * 50; k++)
                for (int i = 0; i < pawns.Count; i++)
                    LoadSupportCache.Get(pawns[i]);
            sw.Stop();
            double cachedUs = sw.Elapsed.TotalMilliseconds * 1000.0 / (rounds * 50 * count);

            sw.Reset(); sw.Start();
            for (int k = 0; k < rounds * 50; k++)
                for (int i = 0; i < pawns.Count; i++)
                    ManipulationCompensation.Compute(StatDefOf.CarryingCapacity, pawns[i], LoadSupportCache.GetResult(pawns[i]));
            sw.Stop();
            double compUs = sw.Elapsed.TotalMilliseconds * 1000.0 / (rounds * 50 * count) - cachedUs;

            Log.Message(LoadSupportLog.Prefix + "Benchmark over " + pawns.Count + " pawns: full body evaluation "
                        + uncachedUs.ToString("0.00") + " µs/pawn, cached lookup " + cachedUs.ToString("0.000")
                        + " µs/pawn, manipulation compensation +" + Math.Max(0, compUs).ToString("0.000") + " µs/query.");
        }

        [DebugAction("Parametric", "Load Support: trace mass capacity (click pawn)", actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DebugTraceMassPawn(Pawn p)
        {
            if (p == null) return;
            MassCapacityTrace.Arm(p);
            Messages.Message("Parametric: tracing the next " + MassCapacityTrace.MaxRecords + " mass-capacity calculations of " + p.LabelShort
                             + ". Open the info card and the caravan dialog, then check the log.", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction("Parametric", "Load Support: trace mass capacity (any pawn)", allowedGameStates = AllowedGameStates.Playing)]
        private static void DebugTraceMassAny()
        {
            MassCapacityTrace.Arm(null);
        }

        [DebugAction("Parametric", "Load Support: stop mass trace", allowedGameStates = AllowedGameStates.Playing)]
        private static void DebugTraceMassStop()
        {
            MassCapacityTrace.Disarm("stopped by user");
        }

        [DebugAction("Parametric", "Load Support: report mass-capacity patches", allowedGameStates = AllowedGameStates.Playing)]
        private static void DebugMassPatchReport()
        {
            Log.Message(LoadSupportLog.Prefix + MassCapacityTrace.PatchReport());
        }

        [DebugAction("Parametric", "Load Support: clear cache", allowedGameStates = AllowedGameStates.Playing)]
        private static void DebugClear()
        {
            LoadSupportCache.Clear();
            Messages.Message("Parametric: Load Support cache cleared.", MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
