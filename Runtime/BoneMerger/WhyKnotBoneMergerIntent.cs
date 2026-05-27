// WhyKnotBoneMergerIntent.cs
//
// Per-avatar marker that asks the Bone Merger pipeline to redirect skin
// weights from each pair's "merge from" bone onto its "merge into" bone
// at play-mode entry and at avatar upload.
//
// Why this exists: the destructive Bone Merger window writes a cloned
// .mesh asset under Assets/AvatarQol Generated/ and rewires the renderer
// to it. A Blender re-import that replaces the FBX subassets repoints
// the renderer back at the fresh (un-merged) mesh, and the merge has to
// be redone by hand. Storing merge INTENT on a component instead means
// the pipeline re-runs against whatever mesh is currently on the
// renderer at play/upload, so a re-import keeps the fix without manual
// rerun.
//
// IEditorOnly: stripped by the VRChat SDK on upload after the build hook
// has already applied merges to the in-memory mesh clone.
//
// Non-destructive build/play applies weight redirects only -- bone
// GameObjects are NEVER deleted in this path even when
// deleteMergedBones is on. Deletion is destructive scene mutation,
// available only through the Bone Merger window's Apply path.

using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace WhyKnot.AvatarQol.Components {

    [Serializable]
    public sealed class BoneMergerPair {
        [Tooltip("The bone whose weights move OFF. In destructive Apply this bone's GameObject is destroyed; in non-destructive build/play it is left in place.")]
        public Transform mergeFrom;

        [Tooltip("The bone that picks up the weights and survives. Must also appear in the renderer's bones[] array, otherwise the pair is skipped for that renderer.")]
        public Transform mergeInto;
    }

    [Serializable]
    public sealed class BoneMergerPrecomputedRenderer {
        public SkinnedMeshRenderer renderer;
        public List<BoneMergerPair> pairs = new List<BoneMergerPair>();
    }

    [AddComponentMenu("WhyKnot/Avatar QoL/Bone Merger Intent")]
    [DisallowMultipleComponent]
    public sealed class WhyKnotBoneMergerIntent : MonoBehaviour, IEditorOnly {

        [Tooltip("The bone pairs to merge. Each row: weights move from the FIRST bone onto the SECOND bone across every SkinnedMeshRenderer under the avatar.")]
        public List<BoneMergerPair> pairs = new List<BoneMergerPair>();

        [Tooltip("Destructive-only: when the Bone Merger window's Apply button is pressed, also destroy each merged-away bone's GameObject. The build / play hooks ignore this flag -- they never touch GameObjects.")]
        public bool deleteMergedBones = true;

        [Tooltip("Destructive-only and only honored when 'Delete merged bones' is on: re-parent the merged-away bone's children onto the kept bone before deletion so they aren't destroyed with the parent. The build / play hooks ignore this flag.")]
        public bool reparentChildren = true;

        [Header("When To Run")]
        [Tooltip("Apply merges when entering Play mode. The renderer's mesh is cloned in memory; the source asset is never modified.")]
        public bool processInPlayMode = true;

        [Tooltip("Apply merges during avatar Build & Publish. The renderer's mesh is cloned in memory; the source asset is never modified.")]
        public bool processOnUpload = true;

        [Tooltip("Write per-renderer merge stats to the WhyKnot log when this intent runs. Useful when an expected merge isn't landing in play mode.")]
        public bool verboseLog;

        [HideInInspector] public string precomputeSignature;
        [HideInInspector] public int precomputeVersion;
        [HideInInspector] public List<BoneMergerPrecomputedRenderer> precomputedRenderers =
            new List<BoneMergerPrecomputedRenderer>();
    }
}
