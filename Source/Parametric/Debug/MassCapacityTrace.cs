using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Parametric.LoadSupport;
using RimWorld;
using Verse;

namespace Parametric.Debug
{
    /// <summary>
    /// One-shot trace of MassUtility.Capacity calls, for compatibility reports (caravan / inventory mass).
    ///
    /// Off by default. Nothing here runs during normal play: the Capacity postfix only reads the static
    /// <see cref="Armed"/> bool. When armed from the dev-mode debug menu, the next <see cref="MaxRecords"/> distinct
    /// calls (optionally for one pawn) are logged with: pawn, nesting depth (overall and same-pawn), whether an
    /// explanation StringBuilder was passed, the value entering Parametric's postfix, Load Support, the decision,
    /// the Overload decision (routine multiplier), the outgoing value, settings, game tick and a short managed call stack. Repeated identical calls (the caravan
    /// dialog redraws every frame) are counted, not re-logged. The trace disarms itself.
    /// Arming also logs every Harmony patch (with owner IDs) on the mass-capacity call chain.
    /// </summary>
    public static class MassCapacityTrace
    {
        public const int MaxRecords = 24;
        private const int MaxStackFrames = 14;
        private const int MaxCallsObserved = 5000; // hard stop even if every call is a repeat

        /// <summary>Read by the postfix on every call. Everything else in this class only runs while this is true.</summary>
        public static bool Armed;

        private static Pawn target;
        private static int logged, observed;
        private static readonly Dictionary<string, int> repeats = new Dictionary<string, int>();
        private static readonly Dictionary<int, float> lastOutput = new Dictionary<int, float>();

        /// <summary>Test hook: receives every formatted record (the log still gets it too).</summary>
        public static Action<string> Sink;

        public static void Arm(Pawn onlyThisPawn)
        {
            target = onlyThisPawn;
            logged = 0; observed = 0;
            repeats.Clear(); lastOutput.Clear();
            Armed = true;
            Emit(LoadSupportLog.Prefix.TrimEnd() + "[MassTrace] armed for the next " + MaxRecords + " distinct MassUtility.Capacity calls"
                 + (onlyThisPawn != null ? " of " + LoadSupportCalculator.SafeLabel(onlyThisPawn) : " (any pawn)")
                 + ". Now open the pawn's info card / gear tab / caravan formation dialog.\n" + PatchReport());
        }

        public static void Disarm(string why)
        {
            if (!Armed) return;
            Armed = false;
            var sb = new StringBuilder(LoadSupportLog.Prefix.TrimEnd() + "[MassTrace] finished (" + why + "): "
                                       + logged + " distinct calls logged, " + observed + " calls observed.");
            foreach (KeyValuePair<string, int> kv in repeats)
                if (kv.Value > 1) sb.Append("\n  repeated x" + kv.Value + ": " + kv.Key);
            Emit(sb.ToString());
            target = null;
        }

        public static void Record(Pawn p, bool hasExplanation, int frame, float incoming, float factor, MassCapacityDecision decision,
                                  float overloadFactor, OverloadDecision overload, float outgoing)
        {
            if (!Armed) return;
            try
            {
                if (target != null && p != target) return;
                observed++;
                if (observed > MaxCallsObserved) { Disarm("call limit"); return; }

                int d = frame + 1;
                int same = Patch_MassUtility_Capacity.SamePawnDepth(p, frame);
                string stack = ShortStack();
                string key = Label(p) + "|d" + d + "|s" + same + "|" + hasExplanation + "|" + incoming.ToString("0.###") + "|"
                             + decision + "|" + overload + "|" + outgoing.ToString("0.###") + "|" + stack;
                int n;
                repeats.TryGetValue(key, out n);
                repeats[key] = n + 1;
                if (n > 0) return; // identical call already logged

                var sb = new StringBuilder();
                sb.Append(LoadSupportLog.Prefix.TrimEnd()).Append("[MassTrace] #").Append(logged + 1).Append('\n');
                sb.Append("Pawn: ").Append(Label(p)).Append('\n');
                sb.Append("Depth: ").Append(d).Append(" (same-pawn enclosing calls: ").Append(same).Append(")\n");
                sb.Append("Explanation: ").Append(hasExplanation ? "non-null" : "null").Append('\n');
                sb.Append("Incoming MassUtility result (entering Parametric postfix): ").Append(incoming.ToString("0.###")).Append(" kg\n");
                float vanillaBase = SafeVanillaBase(p);
                if (vanillaBase > 0f)
                {
                    float ratio = incoming / vanillaBase;
                    sb.Append("  vanilla BodySize x 35 = ").Append(vanillaBase.ToString("0.###")).Append(" kg; incoming is x").Append(ratio.ToString("0.###")).Append(" of it\n");
                    float ls = SafeLoadSupport(p);
                    if (ls > 1.5f && ratio > 0.8f * ls)
                        sb.Append("  WARNING: the incoming value already appears to contain Load Support (x").Append(ls.ToString("0.##")).Append(")\n");
                }
                float prev;
                if (p != null && lastOutput.TryGetValue(p.thingIDNumber, out prev) && prev > 0f && Math.Abs(incoming - prev) > 0.001f)
                    sb.Append("  incoming / previous Parametric output for this pawn = x").Append((incoming / prev).ToString("0.###")).Append('\n');
                else if (p != null && lastOutput.TryGetValue(p.thingIDNumber, out prev) && Math.Abs(incoming - prev) <= 0.001f
                         && (decision == MassCapacityDecision.Scaled || overload == OverloadDecision.Applied))
                    sb.Append("  WARNING: incoming equals Parametric's previous OUTPUT for this pawn (a stored, already-scaled value was fed back in)\n");
                sb.Append("Load Support: ").Append(SafeLoadSupport(p).ToString("0.###")).Append('\n');
                sb.Append("Decision: ").Append(decision).Append(decision == MassCapacityDecision.Scaled ? " (x" + factor.ToString("0.###") + ")" : "").Append('\n');
                sb.Append("Overload: ").Append(overload).Append(overload == OverloadDecision.Applied ? " (x" + overloadFactor.ToString("0.###") + ", policy "
                          + Parametric.Overload.OverloadFormula.PolicyLabel(Parametric.Overload.OverloadUtility.PolicyFor(p)) + ")" : "").Append('\n');
                sb.Append("Outgoing result: ").Append(outgoing.ToString("0.###")).Append(" kg\n");
                ParametricSettings s = ParametricMod.Settings;
                if (s != null)
                    sb.Append("Settings: enabled=").Append(s.loadSupportEnabled).Append(" mass=").Append(s.applyToMassCapacity)
                      .Append(" nonHumanlike=").Append(s.includeNonHumanlike).Append(" exponent=").Append(s.superhumanExponent.ToString("0.##"))
                      .Append(" nestingGuard=").Append(Patch_MassUtility_Capacity.NestingGuardEnabled)
                      .Append(" overload=").Append(s.overloadEnabled).Append('\n');
                sb.Append("Tick: ").Append(SafeTick()).Append('\n');
                sb.Append("Call stack (innermost first):\n").Append(stack.Replace(" <- ", "\n"));
                Emit(sb.ToString());

                if (p != null && (decision == MassCapacityDecision.Scaled || overload == OverloadDecision.Applied)) lastOutput[p.thingIDNumber] = outgoing;
                logged++;
                if (logged >= MaxRecords) Disarm("record limit");
            }
            catch (Exception ex)
            {
                Armed = false;
                Log.Warning(LoadSupportLog.Prefix + "[MassTrace] stopped: " + ex.Message);
            }
        }

        /// <summary>Harmony patches (all owners) on every method of the mass-capacity chain, plus mass/carry StatDefs.</summary>
        public static string PatchReport()
        {
            var sb = new StringBuilder("Harmony patches on the mass-capacity call chain:\n");
            var methods = new List<MethodBase>();
            Add(methods, AccessTools.Method(typeof(MassUtility), nameof(MassUtility.Capacity), new[] { typeof(Pawn), typeof(StringBuilder) }));
            foreach (Type t in new[] { typeof(MassUtility), typeof(CollectionsMassCalculator) })
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t)) Add(methods, m);
            foreach (string name in new[] { "RimWorld.TransferableOneWayWidget:DrawMass", "RimWorld.TransferableOneWayWidget:GetPawnMassTip",
                                            "RimWorld.ITab_Pawn_Gear:TryDrawMassInfo", "RimWorld.Planet.FactionGiftUtility:CheckCanCarryGift" })
                Add(methods, AccessTools.Method(name));
            foreach (string typeName in new[] { "RimWorld.CaravanUIUtility", "RimWorld.Dialog_FormCaravan", "RimWorld.Dialog_LoadTransporters", "RimWorld.Planet.Caravan" })
            {
                Type t = AccessTools.TypeByName(typeName);
                if (t == null) continue;
                foreach (MethodInfo m in AccessTools.GetDeclaredMethods(t))
                    if (m.Name.IndexOf("Mass", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("Capacity", StringComparison.OrdinalIgnoreCase) >= 0)
                        Add(methods, m);
            }
            // Anything else already patched whose name mentions mass/capacity/carry (catches other mods' hooks generically).
            foreach (MethodBase m in Harmony.GetAllPatchedMethods())
                if (m.Name.IndexOf("Mass", StringComparison.OrdinalIgnoreCase) >= 0 || m.Name.IndexOf("Capacity", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.Name.IndexOf("Carry", StringComparison.OrdinalIgnoreCase) >= 0)
                    Add(methods, m);

            int unpatched = 0;
            foreach (MethodBase m in methods)
            {
                Patches info = Harmony.GetPatchInfo(m);
                if (info == null || info.Owners.Count == 0) { unpatched++; continue; }
                sb.Append("  ").Append(Describe(m)).Append('\n');
                AppendPatches(sb, "Prefixes", info.Prefixes);
                AppendPatches(sb, "Postfixes", info.Postfixes);
                AppendPatches(sb, "Transpilers", info.Transpilers);
                AppendPatches(sb, "Finalizers", info.Finalizers);
            }
            sb.Append("  (").Append(unpatched).Append(" further chain methods have no patches)\n");

            sb.Append("StatDefs mentioning mass/carry (worker, parts):\n");
            try
            {
                foreach (StatDef st in DefDatabase<StatDef>.AllDefsListForReading)
                {
                    string n = (st.defName ?? "") + " " + (st.label ?? "");
                    if (n.IndexOf("mass", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("carry", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sb.Append("  ").Append(st.defName).Append(" worker=").Append(st.workerClass != null ? st.workerClass.FullName : "null");
                    if (st.parts != null) foreach (StatPart part in st.parts) sb.Append(" part=").Append(part.GetType().FullName);
                    sb.Append('\n');
                }
            }
            catch (Exception ex) { sb.Append("  (stat scan failed: ").Append(ex.Message).Append(")\n"); }
            return sb.ToString().TrimEnd();
        }

        private static void Add(List<MethodBase> list, MethodBase m)
        {
            if (m != null && !list.Contains(m)) list.Add(m);
        }

        private static void AppendPatches(StringBuilder sb, string kind, IList<Patch> patches)
        {
            if (patches == null || patches.Count == 0) return;
            sb.Append("    ").Append(kind).Append(":\n");
            var sorted = new List<Patch>(patches);
            sorted.Sort((a, b) => a.CompareTo(b)); // Harmony's own execution order
            foreach (Patch pt in sorted)
                sb.Append("      - ").Append(pt.owner).Append("  priority=").Append(pt.priority).Append(" index=").Append(pt.index)
                  .Append("  ").Append(pt.PatchMethod != null ? Describe(pt.PatchMethod) : "?").Append('\n');
        }

        private static string Describe(MethodBase m)
        {
            return (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "." + m.Name;
        }

        private static string ShortStack()
        {
            var st = new StackTrace(2, false); // skip ShortStack + Record
            var sb = new StringBuilder();
            int n = 0;
            for (int i = 0; i < st.FrameCount && n < MaxStackFrames; i++)
            {
                MethodBase m = st.GetFrame(i).GetMethod();
                if (m == null) continue;
                if (m.DeclaringType == typeof(Patch_MassUtility_Capacity)) continue;
                if (n > 0) sb.Append(" <- ");
                sb.Append("  ").Append(Describe(m));
                n++;
            }
            return sb.ToString();
        }

        private static float SafeVanillaBase(Pawn p)
        {
            try { return p != null && MassUtility.CanEverCarryAnything(p) ? p.BodySize * 35f : 0f; }
            catch { return 0f; }
        }

        private static float SafeLoadSupport(Pawn p)
        {
            try { return p != null ? LoadSupportCache.Get(p) : 1f; }
            catch { return 1f; }
        }

        private static string SafeTick()
        {
            try { return Current.Game != null && Find.TickManager != null ? Find.TickManager.TicksGame.ToString() : "-"; }
            catch { return "-"; }
        }

        private static string Label(Pawn p)
        {
            return p == null ? "null" : LoadSupportCalculator.SafeLabel(p);
        }

        private static void Emit(string text)
        {
            if (Sink != null) Sink(text);
            Log.Message(text);
        }
    }
}
