// PhysBonePresetBuildHook.cs
//
// Runs PhysBonePresetApplier.ApplyNonDestructive for every
// WhyKnotPhysBonePresetIntent on an avatar at two lifecycle points:
//
//   - EditorApplication.playModeStateChanged.ExitingEditMode (Play-mode entry)
//   - IVRCSDKPreprocessAvatarCallback                       (Build & Publish upload)
//
// On exit / post-build the session disposes, destroying every spawned
// VRCPhysBone component and every collider holder GameObject so the
// edit-time scene is untouched after the cycle.
//
// callbackOrder = -5000 lands us before NDMF (-1025) and before VRCSDK's
// RemoveAvatarEditorOnly. Spawning before strip is what makes the
// PhysBones survive into the uploaded avatar even though the intent
// component is IEditorOnly.
//
// Why ExitingEditMode and NOT EnteredPlayMode: Unity docs note that
// EnteredPlayMode "may occur after the game's update loop has already
// executed one or more times." PhysBones snapshot their bone transforms
// at Awake/Start; spawning them later than ExitingEditMode misses the
// first frame.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal sealed class PhysBonePresetBuildHook :
        IVRCSDKPreprocessAvatarCallback,
        IVRCSDKPostprocessAvatarCallback {

        public int callbackOrder => -5000;

        private static readonly Dictionary<GameObject, AvatarIntentSession> _uploadSessions =
            new Dictionary<GameObject, AvatarIntentSession>();
        private static readonly Dictionary<GameObject, AvatarIntentSession> _playModeSessions =
            new Dictionary<GameObject, AvatarIntentSession>();
        private static GameObject _activeUploadAvatar;

        static PhysBonePresetBuildHook() {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        // ---- Upload path -------------------------------------------------

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            if (avatarGameObject == null) return true;

            DisposeSession(_uploadSessions, avatarGameObject);

            var intents = CollectIntents(avatarGameObject, requireUpload: true);
            if (intents.Count == 0) return true;

            AvatarIntentSessionState.SetUploadActive(true);
            var session = new AvatarIntentSession();
            _activeUploadAvatar = avatarGameObject;

            foreach (var intent in intents) {
                var result = PhysBonePresetApplier.ApplyNonDestructive(
                    intent.bones, intent.presetId,
                    intent.tweakPull, intent.tweakSpring, intent.tweakStiff,
                    intent.tweakGravity, intent.tweakRadius,
                    session);
                LogResult($"upload ({avatarGameObject.name}, {intent.name})", result, intent.verboseLog);
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

            var byRoot = new Dictionary<GameObject, List<WhyKnotPhysBonePresetIntent>>();
            foreach (var intent in Resources.FindObjectsOfTypeAll<WhyKnotPhysBonePresetIntent>()) {
                if (intent == null || !intent.enabled || !intent.processInPlayMode) continue;
                if (EditorUtility.IsPersistent(intent)) continue;
                var go = intent.gameObject;
                if (go == null || !go.scene.IsValid() || !go.scene.isLoaded) continue;
                if (AvatarPreviewController.IsAvatarInsidePreview(go)) continue;
                var animator = intent.GetComponentInParent<Animator>(true);
                var root = animator != null ? animator.gameObject : TopLevel(intent.transform).gameObject;
                if (!byRoot.TryGetValue(root, out var list)) {
                    list = new List<WhyKnotPhysBonePresetIntent>();
                    byRoot[root] = list;
                }
                list.Add(intent);
            }
            if (byRoot.Count == 0) return;

            AvatarIntentSessionState.SetPlayModeActive(true);

            foreach (var kv in byRoot) {
                var root = kv.Key;
                var session = new AvatarIntentSession();
                foreach (var intent in kv.Value) {
                    var result = PhysBonePresetApplier.ApplyNonDestructive(
                        intent.bones, intent.presetId,
                        intent.tweakPull, intent.tweakSpring, intent.tweakStiff,
                        intent.tweakGravity, intent.tweakRadius,
                        session);
                    LogResult($"play mode ({root.name}, {intent.name})", result, intent.verboseLog);
                }
                if (!session.HasChanges) {
                    session.Dispose();
                    continue;
                }
                _playModeSessions[root] = session;
            }

            if (_playModeSessions.Count == 0) AvatarIntentSessionState.SetPlayModeActive(false);
        }

        // ---- Helpers ------------------------------------------------------

        private static List<WhyKnotPhysBonePresetIntent> CollectIntents(GameObject avatarRoot, bool requireUpload) {
            var list = new List<WhyKnotPhysBonePresetIntent>();
            if (avatarRoot == null) return list;
            foreach (var intent in avatarRoot.GetComponentsInChildren<WhyKnotPhysBonePresetIntent>(true)) {
                if (intent == null || !intent.enabled) continue;
                if (requireUpload && !intent.processOnUpload) continue;
                list.Add(intent);
            }
            return list;
        }

        private static Transform TopLevel(Transform transform) {
            while (transform != null && transform.parent != null) transform = transform.parent;
            return transform;
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

        private static void LogResult(string context, PhysBonePresetApplier.Result result, bool verbose) {
            if (result == null) return;
            var prefix = $"PhysBonePreset {context}: {result.Summary}";
            if (result.ConfigurationError) {
                AvatarQolLogger.Instance.Warning(prefix);
                return;
            }
            if (!result.DidAnything && !verbose) return;
            AvatarQolLogger.Instance.Info(prefix);
        }
    }
}
