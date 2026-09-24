using System;
using Verse;

namespace LoadSupport
{
    /// <summary>
    /// Uncached calculation: body reading → formula. Never throws, never returns NaN/∞/≤0.
    /// Callers should go through <see cref="LoadSupportCache"/> instead of calling this directly.
    /// </summary>
    public static class LoadSupportCalculator
    {
        public static LoadSupportResult Calculate(Pawn pawn)
        {
            if (pawn == null) return LoadSupportResult.Neutral;
            try
            {
                BodyRegionAnalyzer.Reading b = BodyRegionAnalyzer.Read(pawn);
                float exponent = LoadSupportMod.Settings != null
                    ? LoadSupportMod.Settings.superhumanExponent
                    : LoadSupportFormula.DefaultExponent;

                return LoadSupportFormula.Compute(
                    b.HasLower, b.LowerEfficiency,
                    b.HasCore, b.CoreEfficiency,
                    b.HasUpper, b.UpperEfficiency,
                    exponent);
            }
            catch (Exception ex)
            {
                // One error per pawn per session, then silently neutral. The cache stores the neutral result
                // for the refresh interval, so a broken pawn can never throw every tick.
                Log.ErrorOnce("[LoadSupport] Could not evaluate body of " + SafeLabel(pawn)
                              + "; using neutral LoadSupport 1.0. " + ex,
                              ("LoadSupport_calc_" + pawn.thingIDNumber).GetHashCode());
                return LoadSupportResult.Neutral;
            }
        }

        internal static string SafeLabel(Pawn pawn)
        {
            try { return pawn.LabelShortCap + " (" + pawn.ThingID + ")"; }
            catch { return "pawn"; }
        }
    }
}
