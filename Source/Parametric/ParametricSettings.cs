using Parametric.LoadSupport;
using Verse;

namespace Parametric
{
    /// <summary>
    /// Mod settings for Parametric. Stored in RimWorld's Config folder, never in save files.
    /// Fields are grouped per module (Load Support, Overload) plus mod-level debug logging.
    /// Overload's per-pawn policies are player state and live in the save (OverloadGameComponent), not here.
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

        // ---------------- Overload module ----------------
        public const bool DefaultOverloadEnabled = true;
        public const float DefaultOverloadPlayerPolicy = 1f;     // 100% = no overload until the player chooses otherwise
        public const float DefaultOverloadNonPlayerPolicy = 1f;

        /// <summary>Module switch. Off = Overload changes nothing (no routine capacity, no slowdown, no cargo spill).</summary>
        public bool overloadEnabled = DefaultOverloadEnabled;

        /// <summary>Policy of player pawns without an individual setting (gizmo).</summary>
        public float overloadPlayerDefault = DefaultOverloadPlayerPolicy;

        /// <summary>Policy of every non-player pawn (raiders, visitors...); never stored per pawn.</summary>
        public float overloadNonPlayerDefault = DefaultOverloadNonPlayerPolicy;

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
            Scribe_Values.Look(ref overloadEnabled, "overloadEnabled", DefaultOverloadEnabled);
            Scribe_Values.Look(ref overloadPlayerDefault, "overloadPlayerDefault", DefaultOverloadPlayerPolicy);
            Scribe_Values.Look(ref overloadNonPlayerDefault, "overloadNonPlayerDefault", DefaultOverloadNonPlayerPolicy);
            Scribe_Values.Look(ref debugLogging, "debugLogging", DefaultDebugLogging);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                superhumanExponent = LoadSupportFormula.ClampExponent(superhumanExponent);
                overloadPlayerDefault = Parametric.Overload.OverloadFormula.SanitizePolicy(overloadPlayerDefault);
                overloadNonPlayerDefault = Parametric.Overload.OverloadFormula.SanitizePolicy(overloadNonPlayerDefault);
            }
        }

        public void ResetToDefaults()
        {
            loadSupportEnabled = DefaultLoadSupportEnabled;
            superhumanExponent = LoadSupportFormula.DefaultExponent;
            applyToMassCapacity = DefaultApplyToMassCapacity;
            includeNonHumanlike = DefaultIncludeNonHumanlike;
            showInInspectPane = DefaultShowInInspectPane;
            overloadEnabled = DefaultOverloadEnabled;
            overloadPlayerDefault = DefaultOverloadPlayerPolicy;
            overloadNonPlayerDefault = DefaultOverloadNonPlayerPolicy;
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
