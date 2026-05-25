// AvatarQolCategoryColors.cs
//
// Domain-specific issue-category colors for the Weight Sanity Check and
// PhysBone Clipping Risk windows. These three swatches form the pill
// triplet (humanoid / spatial / center-band) that those tools paint next
// to each finding to convey confidence. They stay in wk-vrc-qol --
// not in the shared wk-core palette -- because they only carry meaning
// inside those two tools' visual vocabulary.

using UnityEngine;

namespace WhyKnot.AvatarQol {

    internal static class AvatarQolCategoryColors {

        /// <summary>High-confidence finding (humanoid bone match).</summary>
        public static Color Humanoid => new Color(0.85f, 0.30f, 0.30f, 1f);

        /// <summary>Medium-confidence finding (spatial-only match).</summary>
        public static Color Spatial => new Color(0.85f, 0.55f, 0.20f, 1f);

        /// <summary>Low-confidence finding (center-band, ambiguous).</summary>
        public static Color Center => new Color(0.50f, 0.55f, 0.65f, 1f);
    }
}
