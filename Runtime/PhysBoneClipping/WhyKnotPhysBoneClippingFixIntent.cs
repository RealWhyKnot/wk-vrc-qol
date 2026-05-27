// WhyKnotPhysBoneClippingFixIntent.cs
//
// Per-renderer marker that asks the PhysBone clipping fixer to push a
// moving mesh out of a body/comparison mesh at play-mode entry and avatar
// upload. The source mesh asset is left untouched; the build hook clones
// the renderer's current mesh in memory, applies the same geometry fix used
// by the window, then restores the original renderer state after the run.

using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace WhyKnot.AvatarQol.Components {

    [AddComponentMenu("WhyKnot/Avatar QoL/PhysBone Clipping Fix")]
    [DisallowMultipleComponent]
    public sealed class WhyKnotPhysBoneClippingFixIntent : MonoBehaviour, IEditorOnly {

        [Tooltip("The SkinnedMeshRenderer whose mesh should be pushed out of the comparison surfaces. Defaults to the renderer on this GameObject.")]
        public SkinnedMeshRenderer targetRenderer;

        [Tooltip("Body, clothing, accessories, or other skinned meshes this renderer should not clip into.")]
        public List<SkinnedMeshRenderer> comparisonRenderers = new List<SkinnedMeshRenderer>();

        [Tooltip("Also scan the target mesh against itself and push apart obvious self-intersections.")]
        public bool checkSelf = true;

        [Header("Detection")]
        [Tooltip("How far behind the comparison surface a vertex must be before it counts as clipping, in metres.")]
        [Range(0f, 0.02f)] public float insideTolerance = 0.001f;

        [Tooltip("Extra space to leave outside the comparison surface after a fix, in metres.")]
        [Range(0f, 0.05f)] public float surfacePadding = 0.005f;

        [Tooltip("Number of scan/fix passes to run. Extra passes catch vertices exposed by the previous pass.")]
        [Range(1, 8)] public int maxFixPasses = 4;

        [Header("When To Run")]
        [Tooltip("Apply fixes when entering Play mode. Mesh is cloned in memory; the source asset is never modified.")]
        public bool processInPlayMode = true;

        [Tooltip("Apply fixes during avatar Build & Publish. Mesh is cloned in memory; the source asset is never modified.")]
        public bool processOnUpload = true;

        [Tooltip("Write scan/fix counts to the WhyKnot log when this component runs.")]
        public bool verboseLog;

        private void Reset() {
            targetRenderer = GetComponent<SkinnedMeshRenderer>();
        }
    }
}
