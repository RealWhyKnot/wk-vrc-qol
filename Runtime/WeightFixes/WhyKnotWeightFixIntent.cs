// WhyKnotWeightFixIntent.cs
//
// Per-renderer marker that asks the Weight Sanity Check pipeline to
// re-detect and apply cross-side weight fixes against this renderer's
// CURRENT mesh at play-mode entry and at avatar upload.
//
// Why this exists: the destructive WeightFixer writes corrected weights
// into a cloned .mesh asset under Assets/AvatarQol Generated/, and the
// renderer's sharedMesh is rewired to that clone. A Blender re-import
// that replaces the FBX subassets repoints the renderer back at the
// fresh (uncorrected) mesh -- and any new vertices Blender added are
// also uncorrected. Storing fix INTENT on a component instead means the
// scan re-runs against whatever mesh is currently on the renderer, so a
// re-import keeps the fix without manual rerun.
//
// IEditorOnly: stripped by the VRChat SDK on upload after WeightFix's
// preprocess hook has already applied fixes to the in-memory mesh clone.

using UnityEngine;
using VRC.SDKBase;

namespace WhyKnot.AvatarQol.Components {

    [System.Serializable]
    public sealed class WeightFixPrecomputedIssue {
        public SkinnedMeshRenderer renderer;
        public string rendererPath;
        public int vertexIndex;
        public Vector3 worldPosition;
        public int vertexSide;
        public Transform offendingBone;
        public int boneSide;
        public float weight;
        public int category;
    }

    [AddComponentMenu("WhyKnot/Avatar QoL/Weight Fix Intent")]
    [DisallowMultipleComponent]
    public sealed class WhyKnotWeightFixIntent : MonoBehaviour, IEditorOnly {

        [Tooltip("The SkinnedMeshRenderer to apply weight fixes to. Defaults to the renderer on this GameObject.")]
        public SkinnedMeshRenderer targetRenderer;

        [Header("Detection thresholds")]
        [Tooltip("Weights below this fraction are treated as noise and ignored. Default 0 surfaces every cross-side weight, however small -- bleed in the 0.0001..0.001 band still stretches the mesh visibly under motion. Raise toward 0.02 if a body mesh is flagging too much. Range 0..0.5.")]
        [Range(0f, 0.5f)] public float weightFloor = 0f;

        [Tooltip("Half-width in metres of the on-spine centre stripe in Hips local X. Vertices closer to the centerline than this count as Center, not Left or Right. Default 0 disables the stripe so every vertex is Left or Right by sign of Hips-local X. Raise toward 0.005 only if bind-pose noise around the spine is producing false positives.")]
        [Range(0f, 0.2f)] public float centerMargin = 0f;

        [Tooltip("When on, vertices in the centre stripe also get scanned, using the higher centreThreshold below. Off by default; only meaningful once Centre margin is raised above 0 so centre-stripe vertices actually exist.")]
        public bool scanCenterBand = false;

        [Tooltip("Minimum weight a centre-stripe vertex must have to a Left or Right bone before it counts as a centre-band bleed. Higher than the regular floor because small bleed near the spine is usually fine. Only used when 'Scan centre band' is on.")]
        [Range(0f, 0.5f)] public float centerCrossSideFloor = 0.10f;

        [Header("When To Run")]
        [Tooltip("Apply fixes when entering Play mode. Mesh is cloned in memory; the source asset is never modified.")]
        public bool processInPlayMode = true;

        [Tooltip("Apply fixes during avatar Build & Publish. Mesh is cloned in memory; the source asset is never modified.")]
        public bool processOnUpload = true;

        [Tooltip("Write per-renderer scan stats and per-issue fix actions to the WhyKnot log when this intent runs. Useful when an expected fix isn't landing in play mode.")]
        public bool verboseLog;

        [HideInInspector] public string precomputeSignature;
        [HideInInspector] public int precomputeVersion;
        [HideInInspector] public System.Collections.Generic.List<WeightFixPrecomputedIssue> precomputedIssues =
            new System.Collections.Generic.List<WeightFixPrecomputedIssue>();

        private void Reset() {
            targetRenderer = GetComponent<SkinnedMeshRenderer>();
        }
    }
}
