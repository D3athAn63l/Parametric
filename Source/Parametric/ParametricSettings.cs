using Parametric.LoadSupport;
using Verse;

namespace Parametric
{
    /// <summary>
    /// Mod settings for Parametric. Stored in RimWorld's Config folder, never in save files.
    /// Fields are grouped per module; v0.1 has only the Load Support module plus mod-level debug logging.
    /// </summary>
    public class ParametricSettings : ModSettings
    {
        // ---------------- Load Support module ----------------
        public const bool DefaultLoadSupportEnabled = true;
        public const bool DefaultApplyToMassCapacity = true;
        public const bool DefaultIncludeNonHumanlike = true;
        public const bool DefaultShowInInspectPane = false;

        /// <summary>Module switch. Off = Load Support changes nothing (StatPart and postfixes become no-ops).</summary>
        public bool loadSupportEnabled = DefaultLoadSupportEnabled;

        /// <summary>Strength = efficiency ^ exponent for efficiency above 100%.</summary>
        public float superhumanExponent = LoadSupportFormula.DefaultExponent;

        /// <summary>Also scale MassUtility.Capacity (inventory encumbrance, caravans, transport pods, shuttles).</summary>
        public bool applyToMassCapacity = DefaultApplyToMassCapacity;

        /// <summary>Apply to animals, mechanoids and other non-humanlike pawns (healthy bodies are still exactly 1.0).</summary>
        public bool includeNonHumanlike = DefaultIncludeNonHumanlike;

        public bool showInInspectPane = DefaultShowInInspectPane;

        // ---------------- Mod-level ----------------
        public const bool DefaultDebugLogging = false;
        public bool debugLogging = DefaultDebugLogging;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref loadSupportEnabled, "loadSupportEnabled", DefaultLoadSupportEnabled);
            Scribe_Values.Look(ref superhumanExponent, "superhumanExponent", LoadSupportFormula.DefaultExponent);
            Scribe_Values.Look(ref applyToMassCapacity, "applyToMassCapacity", DefaultApplyToMassCapacity);
            Scribe_Values.Look(ref includeNonHumanlike, "includeNonHumanlike", DefaultIncludeNonHumanlike);
            Scribe_Values.Look(ref showInInspectPane, "showInInspectPane", DefaultShowInInspectPane);
            Scribe_Values.Look(ref debugLogging, "debugLogging", DefaultDebugLogging);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                superhumanExponent = LoadSupportFormula.ClampExponent(superhumanExponent);
        }

        public void ResetToDefaults()
        {
            loadSupportEnabled = DefaultLoadSupportEnabled;
            superhumanExponent = LoadSupportFormula.DefaultExponent;
            applyToMassCapacity = DefaultApplyToMassCapacity;
            includeNonHumanlike = DefaultIncludeNonHumanlike;
            showInInspectPane = DefaultShowInInspectPane;
            debugLogging = DefaultDebugLogging;
        }

        /// <summary>Single gate used by every Load Support integration point.</summary>
        public bool LoadSupportAppliesTo(Pawn pawn)
        {
            if (!loadSupportEnabled || pawn == null) return false;
            RaceProperties race = pawn.RaceProps;
            if (race == null) return false;
            if (!includeNonHumanlike && !race.Humanlike) return false;
            return true;
        }
    }
}
