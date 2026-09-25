using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Parametric.Overload
{
    [StaticConstructorOnStartup]
    public static class OverloadTextures
    {
        // Vanilla transporter-loading icon; no custom art needed.
        public static readonly Texture2D Icon = ContentFinder<Texture2D>.Get("UI/Commands/LoadTransporter", false) ?? BaseContent.BadTex;
    }

    /// <summary>
    /// Pawn gizmo "Overload: 75%".
    ///   Left click  → FloatMenu with the presets (No overload / 75% / 50% / 25% / 10%); applies to every selected pawn.
    ///   Right click → "Apply 75% overload limit to all colony pawns" using this pawn's current setting.
    /// The tooltip shows live comfortable capacity, routine limit, supported mass and the current factor.
    /// </summary>
    public class Command_OverloadPolicy : Command
    {
        private readonly List<Pawn> pawns = new List<Pawn>();

        public Command_OverloadPolicy(Pawn pawn)
        {
            pawns.Add(pawn);
            float policy = OverloadUtility.PolicyFor(pawn);
            defaultLabel = "Overload_GizmoLabel".Translate(PolicyShortLabel(policy));
            defaultDesc = Tooltip(pawn, policy);
            icon = OverloadTextures.Icon;
        }

        public static string PolicyShortLabel(float policy)
        {
            return OverloadFormula.SanitizePolicy(policy) >= 1f ? "Overload_Off".Translate().ToString() : OverloadFormula.PolicyLabel(policy);
        }

        public static string PolicyMenuLabel(float policy)
        {
            return OverloadFormula.SanitizePolicy(policy) >= 1f ? "Overload_NoOverloadOption".Translate().ToString() : OverloadFormula.PolicyLabel(policy);
        }

        private static string Tooltip(Pawn pawn, float policy)
        {
            string text = "Overload_GizmoDesc".Translate();
            OverloadUtility.State st = OverloadUtility.Evaluate(pawn);
            float comfortable = st.ComfortableMass > 0f ? st.ComfortableMass : OverloadUtility.ComfortableCapacityCached(pawn);
            float mass = st.Mass > 0f ? st.Mass : OverloadUtility.ActualSupportedMass(pawn);
            text += "\n\n" + "Overload_GizmoLiveMass".Translate(
                comfortable.ToStringMass(),
                OverloadFormula.RoutineCapacity(comfortable, policy).ToStringMass(),
                mass.ToStringMass());
            if (st.HasHandStack)
            {
                float hand = st.ComfortableHand > 0f ? st.ComfortableHand : OverloadUtility.ComfortableHandCapacityCached(pawn);
                text += "\n" + "Overload_GizmoLiveHand".Translate(
                    hand.ToString("0"), OverloadFormula.RoutineCapacity(hand, policy).ToString("0"), st.HandLoad.ToString("0.#"));
            }
            text += "\n" + "Overload_GizmoLiveFactor".Translate(st.Factor.ToStringPercent());
            if (OverloadUtility.HasIndividualPolicy(pawn)) text += "\n" + "Overload_GizmoIndividual".Translate();
            return text;
        }

        public override void ProcessInput(Event ev)
        {
            base.ProcessInput(ev);
            var options = new List<FloatMenuOption>();
            for (int i = 0; i < OverloadFormula.Presets.Length; i++)
            {
                float preset = OverloadFormula.Presets[i];
                options.Add(new FloatMenuOption(PolicyMenuLabel(preset), () =>
                {
                    for (int j = 0; j < pawns.Count; j++) OverloadUtility.SetPolicy(pawns[j], preset);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }

        public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
        {
            get
            {
                float policy = OverloadUtility.PolicyFor(pawns[0]);
                string label = PolicyMenuLabel(policy);
                yield return new FloatMenuOption("Overload_ApplyToColony".Translate(label), () =>
                {
                    int n = OverloadUtility.ApplyToColony(policy);
                    Messages.Message("Overload_AppliedMessage".Translate(label, n), MessageTypeDefOf.NeutralEvent, false);
                });
            }
        }

        public override bool GroupsWith(Gizmo other)
        {
            return other is Command_OverloadPolicy;
        }

        public override void MergeWith(Gizmo other)
        {
            base.MergeWith(other);
            var o = other as Command_OverloadPolicy;
            if (o == null) return;
            for (int i = 0; i < o.pawns.Count; i++)
                if (!pawns.Contains(o.pawns[i])) pawns.Add(o.pawns[i]);
        }
    }

    /// <summary>PATCH — Pawn.GetGizmos [pass-through Postfix]: appends the Overload gizmo for eligible player pawns.</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Patch_Pawn_GetGizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo g in __result) yield return g;
            if (OverloadUtility.CanUseGizmo(__instance) && __instance.Spawned)
                yield return new Command_OverloadPolicy(__instance);
        }
    }
}
