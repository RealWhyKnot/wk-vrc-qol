// PhysBoneReinitHook.cs
//
// Mid-play `renderer.sharedMesh = clone` is invisible to already-initialized
// VRCPhysBone components -- they snapshot their bone-transform list at
// Awake/Start. Without an explicit reinit pass, PhysBone motion on the
// affected renderers is undefined until the next domain reload.
//
// callbackOrder = 10000 places this hook well after every mesh-mutating
// pipeline (NDMF / VRCFury / Modular Avatar / d4rkAvatarOptimizer / us).
// We do NOT use int.MaxValue because Unity's IOrderedCallback sort has
// a documented bug with int.MaxValue / int.MinValue (Unity Issue Tracker,
// reproducible through 2021.1; status in 2022.3 unverified).
//
// The SDK currently requires InitTransforms(true), followed by parameter
// and shape refreshes, to rebuild the runtime state after mesh / transform
// changes in play mode or during upload preprocessing.

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

#if VRC_SDK_VRCSDK3
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Intent {

    [InitializeOnLoad]
    internal sealed class PhysBoneReinitHook : IVRCSDKPreprocessAvatarCallback {

        public int callbackOrder => 10000;

        private static readonly HashSet<GameObject> _pendingRoots = new HashSet<GameObject>();

        static PhysBoneReinitHook() {
            // Unsubscribe-then-subscribe so repeated static-ctor runs (every
            // assembly reload, every Domain-Reload-disabled play-mode entry)
            // do not stack handlers.
            EditorApplication.delayCall -= FlushPlayModeReinits;
            EditorApplication.delayCall += FlushPlayModeReinits;
        }

        /// <summary>
        /// Mark an avatar root as needing a PhysBone reinit after a mesh swap.
        /// Called by intent runners on play-mode entry. Upload-time reinit
        /// runs via OnPreprocessAvatar in this same class.
        /// </summary>
        public static void RequestReinit(GameObject avatarRoot) {
            if (avatarRoot == null) return;
            _pendingRoots.Add(avatarRoot);
            EditorApplication.delayCall -= FlushPlayModeReinits;
            EditorApplication.delayCall += FlushPlayModeReinits;
        }

        private static void FlushPlayModeReinits() {
            if (_pendingRoots.Count == 0) return;
            foreach (var root in _pendingRoots) ReinitUnderRoot(root);
            _pendingRoots.Clear();
        }

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            ReinitUnderRoot(avatarGameObject);
            return true;
        }

        private static void ReinitUnderRoot(GameObject root) {
#if VRC_SDK_VRCSDK3
            if (root == null) return;

            foreach (var physBone in root.GetComponentsInChildren<VRCPhysBone>(true)) {
                if (physBone == null) continue;
                try {
                    physBone.InitTransforms(true);
                    physBone.InitParameters();
                } catch (Exception ex) {
                    AvatarQolLogger.Instance.Warning($"PhysBone reinit on {physBone.name} failed: {ex.Message}");
                }
            }

            foreach (var collider in root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true)) {
                if (collider == null) continue;
                try {
                    collider.UpdateShape();
                } catch (Exception ex) {
                    AvatarQolLogger.Instance.Warning($"PhysBone collider refresh on {collider.name} failed: {ex.Message}");
                }
            }

            foreach (var contact in root.GetComponentsInChildren<ContactBase>(true)) {
                if (contact == null) continue;
                try {
                    contact.UpdateShape();
                } catch (Exception ex) {
                    AvatarQolLogger.Instance.Warning($"Contact refresh on {contact.name} failed: {ex.Message}");
                }
            }
#else
            _ = root;
#endif
        }
    }
}
