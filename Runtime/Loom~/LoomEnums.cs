// LoomEnums.cs
//
// Small enums shared across Loom authoring types and the build pipeline.
// Kept in one file because each is two-to-four values; per-enum files
// would be ceremony without payoff.

namespace WhyKnot.AvatarQol.Loom {

    /// <summary>
    /// The shape of the synced parameter a Thread compiles to.
    /// Bool is the M1 surface; Float (slider) and Int (n-state radio)
    /// arrive in M2 alongside the slider / radio UI.
    /// </summary>
    public enum ThreadKind {
        Bool,
        Float,
        Int,
    }

    /// <summary>
    /// How an ObjectToggleAction relates the target's active state to the
    /// containing Thread's value. Identical to VRCFury's mode field on
    /// ObjectToggleAction so migration is field-for-field.
    /// </summary>
    public enum ObjectToggleMode {
        /// <summary>Target is active while the Thread is on.</summary>
        TurnOn,
        /// <summary>Target is inactive while the Thread is on (inverted).</summary>
        TurnOff,
        /// <summary>Target's active state flips on every Thread change.</summary>
        Toggle,
    }
}
