using Verse;

namespace LoadSupport
{
    public class LoadSupportSettings : ModSettings
    {
        public const bool DefaultEnabled = true;
        public const bool DefaultApplyToMassCapacity = true;
        public const bool DefaultIncludeNonHumanlike = true;
        public const bool DefaultShowInInspectPane = false;
        public const bool DefaultDebugLogging = false;

        /// <summary>Master switch. Off = the mod changes nothing (StatPart and postfixes become no-ops).</summary>
        public bool enabled = DefaultEnabled;

        /// <summary>Strength = efficiency ^ exponent for efficiency above 100%.</summary>
        public float superhumanExponent = LoadSupportFormula.DefaultExponent;

        /// <summary>Also scale MassUtility.Capacity (inventory encumbrance, caravans, transport pods, shuttles).</summary>
        public bool applyToMassCapacity = DefaultApplyToMassCapacity;

        /// <summary>Apply to animals, mechanoids and other non-humanlike pawns (healthy bodies are still exactly 1.0).</summary>
        public bool includeNonHumanlike = DefaultIncludeNonHumanlike;

        public bool showInInspectPane = DefaultShowInInspectPane;
        public bool debugLogging = DefaultDebugLogging;

        public override void ExposeData()
        {
            base.ExposeData();
            // Mod settings live in the RimWorld Config folder, never in save files.
            Scribe_Values.Look(ref enabled, "enabled", DefaultEnabled);
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
            enabled = DefaultEnabled;
            superhumanExponent = LoadSupportFormula.DefaultExponent;
            applyToMassCapacity = DefaultApplyToMassCapacity;
            includeNonHumanlike = DefaultIncludeNonHumanlike;
            showInInspectPane = DefaultShowInInspectPane;
            debugLogging = DefaultDebugLogging;
        }

        /// <summary>Single gate used by every integration point.</summary>
        public bool AppliesTo(Pawn pawn)
        {
            if (!enabled || pawn == null) return false;
            RaceProperties race = pawn.RaceProps;
            if (race == null) return false;
            if (!includeNonHumanlike && !race.Humanlike) return false;
            return true;
        }
    }
}
