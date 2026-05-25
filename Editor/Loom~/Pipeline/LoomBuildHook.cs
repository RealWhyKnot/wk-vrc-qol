// LoomBuildHook.cs
//
// Loom's IVRCSDKPreprocessAvatarCallback. Runs discover -> validate ->
// plan -> emit at upload time and restores the descriptor's original FX
// controller / expression parameters / expressions menu refs in
// IVRCSDKPostprocessAvatarCallback. Temp emitted assets in
// Assets/_LoomTemp/<...>/ are deleted on restore.
//
// callbackOrder = -4500: after the mesh-mutating Intent hooks at -5000
// so renderer state has settled before we emit, and before NDMF (-1025) /
// SDK RemoveAvatarEditorOnly so downstream consumers see Loom's FX
// additions as part of the avatar.
//
// Why a Dictionary<GameObject, LoomEmitterReceipt> instead of a single
// static: VRChat's batch upload (multiple avatars in one Build & Publish)
// fires Preprocess for each in turn. _activeUploadAvatar tracks the
// current one so OnPostprocessAvatar -- which receives no avatar handle --
// can restore the right descriptor. The dict is the fallback path for
// the belt-and-braces dispose when SDK invocation doesn't match what we
// recorded.
//
// Crash recovery: a [InitializeOnLoad] sweep on startup deletes any
// leftover Assets/_LoomTemp folder content. We are guaranteed not to be
// in the middle of an upload at static-ctor time, so anything in there
// is from a previous run that crashed mid-build.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Editor.BuildPipeline;

namespace WhyKnot.AvatarQol.Loom.Pipeline {

    [InitializeOnLoad]
    internal sealed class LoomBuildHook :
        IVRCSDKPreprocessAvatarCallback,
        IVRCSDKPostprocessAvatarCallback {

        public int callbackOrder => -4500;

        private static readonly Dictionary<GameObject, ActiveBuild> _uploadBuilds =
            new Dictionary<GameObject, ActiveBuild>();
        private static GameObject _activeUploadAvatar;

        static LoomBuildHook() {
            EditorApplication.delayCall -= SweepLeftoverTempAssetsOnce;
            EditorApplication.delayCall += SweepLeftoverTempAssetsOnce;
        }

        // ---- Upload path -------------------------------------------------

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            if (avatarGameObject == null) return true;

            // Defensive: if a prior upload exception bypassed OnPostprocessAvatar,
            // restore anything still held for this avatar before we re-patch
            // its descriptor.
            DisposeBuild(avatarGameObject);

            var descriptor = avatarGameObject.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) return true;

            var discovery = LoomDiscovery.Discover(avatarGameObject);
            if (discovery.Threads.Count == 0) return true;

            var diagnostics = LoomValidator.Validate(discovery);
            bool anyError = false;
            foreach (var d in diagnostics) {
                LogDiagnostic(d);
                if (d.Severity == LoomDiagnosticSeverity.Error) anyError = true;
            }
            if (anyError) {
                AvatarQolLogger.Instance.Error(
                    $"Loom blocked upload of '{avatarGameObject.name}': fix the errors above and retry.");
                return false;
            }

            var plan = LoomPlanner.Plan(discovery);
            var receipt = LoomEmitter.Emit(plan, descriptor);
            if (receipt == null) {
                AvatarQolLogger.Instance.Warning(
                    $"Loom emit returned no receipt for '{avatarGameObject.name}'; nothing was substituted.");
                return true;
            }

            _activeUploadAvatar = avatarGameObject;
            _uploadBuilds[avatarGameObject] = new ActiveBuild {
                Descriptor = descriptor,
                Receipt = receipt,
            };
            AvatarQolLogger.Instance.Info(
                $"Loom: emitted {plan.Layers.Count} layer(s), {plan.Parameters.Count} parameter(s), " +
                $"{plan.MenuItems.Count} menu item(s) for '{avatarGameObject.name}'.");
            return true;
        }

        public void OnPostprocessAvatar() {
            if (_activeUploadAvatar != null) {
                DisposeBuild(_activeUploadAvatar);
                _activeUploadAvatar = null;
            } else {
                DisposeAllBuilds();
            }
        }

        // ---- Helpers -----------------------------------------------------

        private static void DisposeBuild(GameObject avatarRoot) {
            if (avatarRoot == null || !_uploadBuilds.TryGetValue(avatarRoot, out var build)) return;
            LoomEmitter.Restore(build.Descriptor, build.Receipt);
            LoomEmitter.DeleteTempFolder(build.Receipt.TempAssetFolder);
            _uploadBuilds.Remove(avatarRoot);
        }

        private static void DisposeAllBuilds() {
            foreach (var build in _uploadBuilds.Values) {
                LoomEmitter.Restore(build.Descriptor, build.Receipt);
                LoomEmitter.DeleteTempFolder(build.Receipt.TempAssetFolder);
            }
            _uploadBuilds.Clear();
        }

        private static void LogDiagnostic(LoomDiagnostic d) {
            switch (d.Severity) {
                case LoomDiagnosticSeverity.Error:
                    AvatarQolLogger.Instance.Error($"Loom validation: {d.Message}");
                    break;
                case LoomDiagnosticSeverity.Warning:
                    AvatarQolLogger.Instance.Warning($"Loom validation: {d.Message}");
                    break;
                default:
                    AvatarQolLogger.Instance.Info($"Loom validation: {d.Message}");
                    break;
            }
        }

        private static void SweepLeftoverTempAssetsOnce() {
            EditorApplication.delayCall -= SweepLeftoverTempAssetsOnce;
            const string root = "Assets/_LoomTemp";
            if (!AssetDatabase.IsValidFolder(root)) return;
            AssetDatabase.DeleteAsset(root);
        }

        private sealed class ActiveBuild {
            public VRCAvatarDescriptor Descriptor;
            public LoomEmitterReceipt Receipt;
        }
    }
}
