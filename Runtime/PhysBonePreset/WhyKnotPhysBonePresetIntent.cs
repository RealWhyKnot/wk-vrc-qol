// WhyKnotPhysBonePresetIntent.cs
//
// Per-avatar marker that asks the PhysBone Preset pipeline to spawn the
// VRCPhysBone (and any preset-defined collider) components on the
// recorded bones at play-mode entry and at avatar upload, instead of
// committing them as durable scene components.
//
// Why this exists: the PhysBone Preset window's Apply button creates
// the VRCPhysBone + VRCPhysBoneCollider components directly on the
// avatar. That's fine until a sibling tool (or the user) regenerates
// the rig and the destination bones don't exist anymore -- the apply
// has to be redone manually. Storing the bones + preset choice on this
// intent component instead means the same setup is re-spawned in
// memory each play / build, against whatever bone Transforms currently
// exist under the avatar.
//
// IEditorOnly: stripped by the VRChat SDK on upload after the build
// hook has already spawned the PhysBones into the in-memory clone the
// SDK uploads.

using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace WhyKnot.AvatarQol.Components {

    [AddComponentMenu("WhyKnot/Avatar QoL/PhysBone Preset Intent")]
    [DisallowMultipleComponent]
    public sealed class WhyKnotPhysBonePresetIntent : MonoBehaviour, IEditorOnly {

        [Tooltip("The bones the preset will spawn PhysBones on. Each top-level Transform becomes a chain root; descendants are walked automatically by the preset.")]
        public List<Transform> bones = new List<Transform>();

        [Tooltip("Stable identifier of the preset to apply (e.g. \"tail\", \"hair\"). Pick from the PhysBone Preset window's card list and click Add as Intent to populate this.")]
        public string presetId;

        [Header("Post-apply tweaks (multiplicative)")]
        [Tooltip("Multiplier on the Pull parameter for every spawned PhysBone. 1 = preset default.")]
        public float tweakPull = 1f;
        [Tooltip("Multiplier on the Spring parameter for every spawned PhysBone. 1 = preset default.")]
        public float tweakSpring = 1f;
        [Tooltip("Multiplier on the Stiffness parameter for every spawned PhysBone. 1 = preset default.")]
        public float tweakStiff = 1f;
        [Tooltip("Multiplier on the Gravity parameter for every spawned PhysBone. 1 = preset default.")]
        public float tweakGravity = 1f;
        [Tooltip("Multiplier on the Radius parameter for every spawned PhysBone and PhysBoneCollider. 1 = preset default.")]
        public float tweakRadius = 1f;

        [Header("When To Run")]
        [Tooltip("Spawn the PhysBones when entering Play mode. Components live in memory only; nothing is written back to the scene file.")]
        public bool processInPlayMode = true;

        [Tooltip("Spawn the PhysBones during avatar Build & Publish. Components live in memory only; nothing is written back to the scene file.")]
        public bool processOnUpload = true;

        [Tooltip("Write per-apply spawn stats to the WhyKnot log when this intent runs.")]
        public bool verboseLog;
    }
}
