// DetectedIssue.cs
//
// One cross-side weight that the detector flagged on a renderer. Shape
// matches the fixer's IssueRef (Renderer + VertexIndex + OffendingBone +
// Weight) plus side/category context the UI uses for rendering.
//
// Side classification fields are kept in WhyKnot.AvatarQol.Internal.Utilities.BoneSide
// so the runtime apply hook and the UI agree on enum values without a
// separate vendor enum.

using UnityEngine;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.WeightFixes {

    internal enum IssueCategory {
        HumanoidCrossSide,    // bone has Humanoid ancestor on opposite side
        SpatialCrossSide,     // bone has no Humanoid ancestor; pivot on opposite side
        CenterBandSideBleed,  // vertex is in the centre stripe; bone is Left or Right above the higher centre floor
    }

    internal sealed class DetectedIssue {
        public SkinnedMeshRenderer Renderer;
        public string RendererPath;
        public int VertexIndex;
        public Vector3 WorldPosition;
        public BoneSide VertexSide;
        public Transform OffendingBone;
        public BoneSide BoneSide;
        public float Weight;
        public IssueCategory Category;
    }
}
