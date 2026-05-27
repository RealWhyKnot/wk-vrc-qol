// BoneMergerBuildHook.cs
//
// Runs BoneMergerOp.ApplyNonDestructive for every WhyKnotBoneMergerIntent
// on an avatar at two lifecycle points:
//
//   - EditorApplication.playModeStateChanged.ExitingEditMode (Play-mode entry)
//   - IVRCSDKPreprocessAvatarCallback                       (Build & Publish upload)
//
// On exit / post-build, captured originals are restored from each session
// so the source mesh asset is never modified -- edit-time the user sees
// the un-merged mesh, play / build see the merged in-memory clone.
//
// callbackOrder = -5000 lands us before NDMF (-1025) and before VRCSDK's
// RemoveAvatarEditorOnly. We must apply the in-memory clones BEFORE the
// SDK strips our IEditorOnly intent components or NDMF rewrites the
// renderer references.
//
// Why ExitingEditMode and NOT EnteredPlayMode: Unity docs note that
// EnteredPlayMode "may occur after the game's update loop has already
// executed one or more times." Animator / PhysBones / cloth read the
// mesh on frame 1; if we swap later we silently miss the first frame.
//
// After play-mode apply, PhysBoneReinitHook.RequestReinit forces every
// VRCPhysBone under the avatar to re-snapshot its bone-transform list
// against the new (merged) mesh's bones[] array.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;

namespace WhyKnot.AvatarQol.BoneMerger {

    [InitializeOnLoad]
    internal sealed class BoneMergerBuildHook :
        IVRCSDKPreprocessAvatarCallback,
        IVRCSDKPostprocessAvatarCallback {

        public int callbackOrder => -5000;

        private static readonly Dictionary<GameObject, AvatarIntentSession> _uploadSessions =
            new Dictionary<GameObject, AvatarIntentSession>();
        private static readonly Dictionary<GameObject, AvatarIntentSession> _playModeSessions =
            new Dictionary<GameObject, AvatarIntentSession>();
        private static GameObject _activeUploadAvatar;

        static BoneMergerBuildHook() {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        // ---- Upload path -------------------------------------------------

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            if (avatarGameObject == null) return true;

            // Defensive: if a prior upload exception bypassed OnPostprocessAvatar,
            // restore anything still held for this avatar before we touch its
            // renderers again.
            DisposeSession(_uploadSessions, avatarGameObject);

            var intent = CollectIntent(avatarGameObject, requireUpload: true);
            if (intent == null) return true;

            var animator = avatarGameObject.GetComponentInChildren<Animator>(true);
            if (animator == null) {
                AvatarQolLogger.Instance.Warning(
                    $"BoneMerger upload skipped on '{avatarGameObject.name}': no Animator under the avatar.");
                return true;
            }

            AvatarIntentSessionState.SetUploadActive(true);
            var session = new AvatarIntentSession();
            _activeUploadAvatar = avatarGameObject;
            var plan = EnsurePrecompute(intent, animator, out bool cacheRebuilt);
            var result = BoneMergerOp.ApplyNonDestructive(animator, intent.pairs, session, plan);
            LogResult($"upload ({avatarGameObject.name})", result, intent.verboseLog);
            if (intent.verboseLog && cacheRebuilt) {
                AvatarQolLogger.Instance.Info($"BoneMerger upload ({avatarGameObject.name}): precompute cache rebuilt for {plan.Count} renderer(s).");
            }

            if (!session.HasChanges) {
                session.Dispose();
                _activeUploadAvatar = null;
                AvatarIntentSessionState.SetUploadActive(_uploadSessions.Count > 0);
                return true;
            }
            _uploadSessions[avatarGameObject] = session;
            return true;
        }

        public void OnPostprocessAvatar() {
            // VRChat SDK does not pass the avatar handle here; restore
            // whatever we recorded as the active upload target. Belt-and-
            // braces dispose anything else we still hold in case the SDK
            // invoked us without us having recorded an active avatar.
            if (_activeUploadAvatar != null) {
                DisposeSession(_uploadSessions, _activeUploadAvatar);
                _activeUploadAvatar = null;
            } else {
                DisposeAllSessions(_uploadSessions);
            }
            AvatarIntentSessionState.SetUploadActive(_uploadSessions.Count > 0);
        }

        // ---- Play-mode path ----------------------------------------------

        private static void OnPlayModeStateChanged(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingEditMode) {
                ProcessForPlayMode();
            } else if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.ExitingPlayMode) {
                DisposeAllSessions(_playModeSessions);
                AvatarIntentSessionState.SetPlayModeActive(_playModeSessions.Count > 0);
                DisposeAllSessions(_uploadSessions);
                _activeUploadAvatar = null;
                AvatarIntentSessionState.SetUploadActive(false);
            }
        }

        private static void ProcessForPlayMode() {
            DisposeAllSessions(_playModeSessions);

            // Group intents by avatar root so each avatar gets its own session
            // -- a failure on one avatar shouldn't unwind the others in scene.
            var byRoot = new Dictionary<GameObject, WhyKnotBoneMergerIntent>();
            foreach (var intent in Resources.FindObjectsOfTypeAll<WhyKnotBoneMergerIntent>()) {
                if (intent == null || !intent.enabled || !intent.processInPlayMode) continue;
                if (EditorUtility.IsPersistent(intent)) continue;
                var go = intent.gameObject;
                if (go == null || !go.scene.IsValid() || !go.scene.isLoaded) continue;
                if (AvatarPreviewController.IsAvatarInsidePreview(go)) continue;
                var animator = intent.GetComponentInParent<Animator>(true);
                var root = animator != null ? animator.gameObject : TopLevel(intent.transform).gameObject;
                if (!byRoot.ContainsKey(root)) byRoot[root] = intent;
            }
            if (byRoot.Count == 0) return;

            AvatarIntentSessionState.SetPlayModeActive(true);

            foreach (var kv in byRoot) {
                var root = kv.Key;
                var intent = kv.Value;
                var animator = root.GetComponentInChildren<Animator>(true);
                if (animator == null) {
                    AvatarQolLogger.Instance.Warning(
                        $"BoneMerger play-mode skipped on '{root.name}': no Animator under the avatar.");
                    continue;
                }

                var session = new AvatarIntentSession();
                var plan = EnsurePrecompute(intent, animator, out bool cacheRebuilt);
                var result = BoneMergerOp.ApplyNonDestructive(animator, intent.pairs, session, plan);
                LogResult($"play mode ({root.name})", result, intent.verboseLog);
                if (intent.verboseLog && cacheRebuilt) {
                    AvatarQolLogger.Instance.Info($"BoneMerger play mode ({root.name}): precompute cache rebuilt for {plan.Count} renderer(s).");
                }

                if (!session.HasChanges) {
                    session.Dispose();
                    continue;
                }
                _playModeSessions[root] = session;
                PhysBoneReinitHook.RequestReinit(root);
            }

            if (_playModeSessions.Count == 0) AvatarIntentSessionState.SetPlayModeActive(false);
        }

        // ---- Helpers ------------------------------------------------------

        private static WhyKnotBoneMergerIntent CollectIntent(GameObject avatarRoot, bool requireUpload) {
            if (avatarRoot == null) return null;
            foreach (var intent in avatarRoot.GetComponentsInChildren<WhyKnotBoneMergerIntent>(true)) {
                if (intent == null || !intent.enabled) continue;
                if (requireUpload && !intent.processOnUpload) continue;
                return intent;
            }
            return null;
        }

        private static Transform TopLevel(Transform transform) {
            while (transform != null && transform.parent != null) transform = transform.parent;
            return transform;
        }

        private static List<BoneMergerPrecomputedRenderer> EnsurePrecompute(
                WhyKnotBoneMergerIntent intent,
                Animator animator,
                out bool rebuilt) {

            rebuilt = false;
            string signature = IntentPrecomputeUtility.BuildBoneMergerSignature(intent, animator, intent != null ? intent.pairs : null);
            if (IntentPrecomputeUtility.HasValidBoneMergerCache(intent, signature)) {
                return intent.precomputedRenderers;
            }

            var plan = BoneMergerOp.PrecomputeRenderers(animator, intent != null ? intent.pairs : null, out _);
            if (intent != null) {
                intent.precomputeSignature = signature;
                intent.precomputeVersion = IntentPrecomputeUtility.BoneMergerVersion;
                intent.precomputedRenderers = plan;
                EditorUtility.SetDirty(intent);
            }
            rebuilt = true;
            return plan;
        }

        private static void DisposeSession(Dictionary<GameObject, AvatarIntentSession> map, GameObject avatarRoot) {
            if (avatarRoot == null || !map.TryGetValue(avatarRoot, out var session)) return;
            session?.Dispose();
            map.Remove(avatarRoot);
        }

        private static void DisposeAllSessions(Dictionary<GameObject, AvatarIntentSession> map) {
            foreach (var session in map.Values) session?.Dispose();
            map.Clear();
        }

        private static void LogResult(string context, BoneMergerOp.Result result, bool verbose) {
            if (result == null) return;
            if (!result.DidAnything && !verbose && !result.ConfigurationError) return;
            var prefix = $"BoneMerger {context}: {result.Summary}";
            if (result.ConfigurationError) {
                AvatarQolLogger.Instance.Warning(prefix);
                return;
            }
            if (verbose) {
                var lines = new List<string> { prefix };
                foreach (var d in result.Detail) lines.Add("  " + d);
                AvatarQolLogger.Instance.Info(string.Join("\n", lines));
            } else {
                AvatarQolLogger.Instance.Info(prefix);
            }
        }
    }
}
