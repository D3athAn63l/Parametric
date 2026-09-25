using System;

namespace Parametric.Overload
{
    /// <summary>
    /// Pure Overload math (no RimWorld types; unit-tested standalone).
    ///
    ///   C = comfortable capacity (the pawn's real mass capacity, Load Support included, Overload excluded)
    ///   W = actual supported mass (gear + inventory)
    ///   R = W / C
    ///
    /// Reactive movement factor:   R ≤ 1 → 1        R > 1 → 2 − R        (continuous, no steps)
    ///   110% → 0.90, 125% → 0.75, 150% → 0.50, 175% → 0.25, 190% → 0.10, 200% → 0 (mathematically).
    ///   The applied factor never goes below <see cref="EmergencyMinimumFactor"/> (see there).
    ///
    /// Policy p (the gizmo's percentage) is the factor that ordinary loading may deliberately reach. Routine (exposed)
    /// capacity uses the inverse of the same curve:   routine multiplier = 2 − p
    ///   100% → ×1.00, 75% → ×1.25, 50% → ×1.50, 25% → ×1.75, 10% → ×1.90
    /// The policy is an automation limit, not a movement guarantee: movement always follows W / C.
    /// </summary>
    public static class OverloadFormula
    {
        public const float NoOverload = 1f;
        public const float MinPolicy = 0.10f;

        /// <summary>
        /// Floor for the APPLIED movement factor ("effectively immobilized"). Vanilla 1.6 Pawn.TicksPerMove handles a
        /// MoveSpeed of exactly 0 (450 ticks/cell) and clamps every result to [1, 450] ticks, so zero would be safe for
        /// vanilla; the floor keeps MoveSpeed strictly positive for any other code that divides by it. For a human
        /// (4.6 c/s) it already means ≥ 260 ticks per cell before any other penalty.
        /// </summary>
        public const float EmergencyMinimumFactor = 0.05f;

        /// <summary>Gizmo / settings presets, strictest last. 1 = no overload.</summary>
        public static readonly float[] Presets = { 1f, 0.75f, 0.5f, 0.25f, 0.10f };

        /// <summary>NaN/∞/≤0/&gt;1 → 1 (no overload); below <see cref="MinPolicy"/> → MinPolicy.</summary>
        public static float SanitizePolicy(float policy)
        {
            if (float.IsNaN(policy) || float.IsInfinity(policy) || !(policy > 0f) || policy >= 1f) return 1f;
            return policy < MinPolicy ? MinPolicy : policy;
        }

        /// <summary>Routine capacity multiplier for a policy: 2 − p (1.00 … 1.90).</summary>
        public static float RoutineMultiplier(float policy)
        {
            return 2f - SanitizePolicy(policy);
        }

        public static float RoutineCapacity(float comfortable, float policy)
        {
            if (!IsValidCapacity(comfortable)) return comfortable > 0f ? comfortable : 0f;
            return comfortable * RoutineMultiplier(policy);
        }

        public static bool IsValidCapacity(float capacity)
        {
            return capacity > 0f && !float.IsNaN(capacity) && !float.IsInfinity(capacity);
        }

        /// <summary>W / C, or 0 when either input is unusable (no load can be judged).</summary>
        public static float LoadRatio(float mass, float comfortable)
        {
            if (!IsValidCapacity(comfortable) || float.IsNaN(mass) || !(mass > 0f)) return 0f;
            if (float.IsInfinity(mass)) return float.PositiveInfinity;
            return mass / comfortable;
        }

        /// <summary>2 − R above 100% load (may be ≤ 0), 1 at or below it. Invalid inputs → 1.</summary>
        public static float RawFactor(float mass, float comfortable)
        {
            float r = LoadRatio(mass, comfortable);
            if (!(r > 1f)) return 1f;
            if (float.IsInfinity(r)) return float.NegativeInfinity;
            return 2f - r;
        }

        /// <summary>The factor applied to MoveSpeed: RawFactor, never below <see cref="EmergencyMinimumFactor"/>.</summary>
        public static float ReactiveFactor(float mass, float comfortable)
        {
            float raw = RawFactor(mass, comfortable);
            if (raw >= 1f) return 1f;
            return raw > EmergencyMinimumFactor ? raw : EmergencyMinimumFactor;
        }

        /// <summary>Mass that must go for W to fit into the routine limit (0 if it already fits or inputs are unusable).</summary>
        public static float ExcessMass(float mass, float comfortable, float policy)
        {
            if (!IsValidCapacity(comfortable) || float.IsNaN(mass) || float.IsInfinity(mass) || !(mass > 0f)) return 0f;
            float excess = mass - RoutineCapacity(comfortable, policy);
            return excess > 0f ? excess : 0f;
        }

        /// <summary>Units of a stack to drop to shed <paramref name="excess"/> kg: ceil(excess / unitMass), bounded by the stack.</summary>
        public static int UnitsToDrop(float excess, float unitMass, int stackCount)
        {
            if (!(excess > 0f) || !(unitMass > 0f) || float.IsInfinity(unitMass) || stackCount <= 0) return 0;
            if (float.IsInfinity(excess)) return stackCount;
            double units = Math.Ceiling(excess / (double)unitMass - 1e-6);
            if (units < 1d) units = 1d;
            return units >= stackCount ? stackCount : (int)units;
        }

        public static string PolicyLabel(float policy)
        {
            float p = SanitizePolicy(policy);
            return p >= 1f ? "100%" : Math.Round(p * 100f).ToString("0") + "%";
        }
    }
}
