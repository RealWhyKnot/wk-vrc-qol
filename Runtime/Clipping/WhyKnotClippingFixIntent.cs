// WhyKnotClippingFixIntent.cs
//
// Per-renderer marker that asks the clipping fixer to push a target mesh
// out of body/comparison meshes at play-mode entry and avatar upload. The
// source mesh asset is left untouched; the build hook clones the renderer's
// current mesh in memory, applies the same fix used by the window, then
// restores the original renderer state after the run.

using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace WhyKnot.AvatarQol.Components {

    [System.Serializable]
    public sealed class ClippingFixPrecomputedIssue {
        public int kind;
        public SkinnedMeshRenderer renderer;
        public string rendererPath;
        public SkinnedMeshRenderer comparisonRenderer;
        public string comparisonPath;
        public int vertexIndex = -1;
        public int targetTriangleIndex = -1;
        public int comparisonTriangleIndex = -1;
        public int[] affectedVertexIndices = new int[0];
        public Vector3 worldPosition;
        public Vector3 nearestSurfacePosition;
        public Vector3 surfaceNormal;
        public Vector3 pushWorld;
        public float penetrationDepth;
        public float score;
        public string reason;
        public Component physBoneComponent;
        public string physBoneSourceLabel;
        public Transform physBoneRoot;
        public Transform drivenBone;
        public float physBoneWeight;
        public float estimatedMotion;
        public float clearance;
        public bool hasEffectiveColliders;
        public bool physBoneHighSeverity;
    }

    [AddComponentMenu("WhyKnot/Avatar QoL/Clipping Fix")]
    [DisallowMultipleComponent]
    public sealed class WhyKnotClippingFixIntent : MonoBehaviour, IEditorOnly {

        [Tooltip("The SkinnedMeshRenderer whose mesh should be pushed out of the comparison surfaces. Defaults to the renderer on this GameObject.")]
        public SkinnedMeshRenderer targetRenderer;

        [Tooltip("Avatar Animator used to find native VRC PhysBones and supported generated/custom PhysBone sources. Defaults to the parent Animator.")]
        public Animator animator;

        [Tooltip("Body, clothing, accessories, or other skinned meshes this renderer should not clip into.")]
        public List<SkinnedMeshRenderer> comparisonRenderers = new List<SkinnedMeshRenderer>();

        [Tooltip("Also scan the target mesh against itself and push apart obvious self-intersections.")]
        public bool checkSelf = true;

        [Tooltip("Also scan native VRC PhysBones and supported generated/custom PhysBone sources, then push risky weighted vertices away from nearby surfaces.")]
        public bool includePhysBoneMotion = true;

        [Header("Detection")]
        [Tooltip("How far behind the comparison surface a vertex must be before it counts as clipping, in metres.")]
        [Range(0f, 0.02f)] public float insideTolerance = 0.001f;

        [Tooltip("Extra space to leave outside the comparison surface after a fix, in metres.")]
        [Range(0f, 0.05f)] public float surfacePadding = 0.005f;

        [Tooltip("Minimum skin weight for a vertex to be considered driven by a PhysBone source.")]
        [Range(0.005f, 0.20f)] public float physBoneWeightFloor = 0.03f;

        [Tooltip("Extra clearance a PhysBone-driven vertex should keep from nearby mesh surfaces before it is considered risky, in metres.")]
        [Range(0.005f, 0.08f)] public float physBoneClearanceMargin = 0.025f;

        [Tooltip("Number of scan/fix passes to run. Extra passes catch vertices exposed by the previous pass.")]
        [Range(1, 8)] public int maxFixPasses = 4;

        [Tooltip("Maximum number of motion warnings one PhysBone source can contribute per scan.")]
        [Range(1, 24)] public int maxIssuesPerPhysBone = 8;

        [Header("When To Run")]
        [Tooltip("Apply fixes when entering Play mode. Mesh is cloned in memory; the source asset is never modified.")]
        public bool processInPlayMode = true;

        [Tooltip("Apply fixes during avatar Build & Publish. Mesh is cloned in memory; the source asset is never modified.")]
        public bool processOnUpload = true;

        [Tooltip("Write scan/fix counts to the WhyKnot log when this component runs.")]
        public bool verboseLog;

        [HideInInspector] public string precomputeSignature;
        [HideInInspector] public int precomputeVersion;
        [HideInInspector] public List<ClippingFixPrecomputedIssue> precomputedIssues =
            new List<ClippingFixPrecomputedIssue>();

        private void Reset() {
            targetRenderer = GetComponent<SkinnedMeshRenderer>();
            animator = GetComponentInParent<Animator>(true);
        }
    }
}
