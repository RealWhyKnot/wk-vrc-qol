// ClippingFixApplyHook.cs
//
// Runs WhyKnotClippingFixIntent components at play-mode entry and
// avatar upload. The hook clones target meshes in memory, rewrites skin
// weights, and restores the original renderer references after the
// lifecycle event finishes.

using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;

namespace WhyKnot.AvatarQol.Clipping {

    [InitializeOnLoad]
    internal sealed class ClippingFixApplyHook :
        IVRCSDKPreprocessAvatarCallback,
        IVRCSDKPostprocessAvatarCallback {

        public int callbackOrder => -4990;

        private static readonly Dictionary<GameObject, AvatarIntentSession> _uploadSessions =
            new Dictionary<GameObject, AvatarIntentSession>();
        private static readonly Dictionary<GameObject, AvatarIntentSession> _playModeSessions =
            new Dictionary<GameObject, AvatarIntentSession>();
        private static GameObject _activeUploadAvatar;

        static ClippingFixApplyHook() {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            if (avatarGameObject == null) return true;
            DisposeSession(_uploadSessions, avatarGameObject);

            var intents = CollectIntents(avatarGameObject, requireUpload: true);
            if (intents.Count == 0) return true;

            AvatarIntentSessionState.SetUploadActive(true);
            var session = new AvatarIntentSession();
            _activeUploadAvatar = avatarGameObject;
            var summary = RunIntents(intents, session, $"upload ({avatarGameObject.name})");
            if (!session.HasChanges) {
                session.Dispose();
                _activeUploadAvatar = null;
                AvatarIntentSessionState.SetUploadActive(_uploadSessions.Count > 0);
                LogSummary(summary);
                return true;
            }

            _uploadSessions[avatarGameObject] = session;
            LogSummary(summary);
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

            var byRoot = new Dictionary<GameObject, List<WhyKnotClippingFixIntent>>();
            foreach (var intent in Resources.FindObjectsOfTypeAll<WhyKnotClippingFixIntent>()) {
                if (intent == null || !intent.enabled || !intent.processInPlayMode) continue;
                if (EditorUtility.IsPersistent(intent)) continue;
                var go = intent.gameObject;
                if (go == null || !go.scene.IsValid() || !go.scene.isLoaded) continue;
                if (AvatarPreviewController.IsAvatarInsidePreview(go)) continue;
                var animator = intent.GetComponentInParent<Animator>(true);
                var root = animator != null ? animator.gameObject : TopLevel(intent.transform).gameObject;
                if (!byRoot.TryGetValue(root, out var list)) {
                    list = new List<WhyKnotClippingFixIntent>();
                    byRoot[root] = list;
                }
                list.Add(intent);
            }
            if (byRoot.Count == 0) return;

            AvatarIntentSessionState.SetPlayModeActive(true);
            foreach (var kv in byRoot) {
                var session = new AvatarIntentSession();
                var summary = RunIntents(kv.Value, session, $"play mode ({kv.Key.name})");
                if (!session.HasChanges) {
                    session.Dispose();
                    LogSummary(summary);
                    continue;
                }
                _playModeSessions[kv.Key] = session;
                LogSummary(summary);
            }

            if (_playModeSessions.Count == 0) AvatarIntentSessionState.SetPlayModeActive(false);
        }

        private static List<WhyKnotClippingFixIntent> CollectIntents(GameObject avatarRoot, bool requireUpload) {
            var list = new List<WhyKnotClippingFixIntent>();
            if (avatarRoot == null) return list;
            foreach (var intent in avatarRoot.GetComponentsInChildren<WhyKnotClippingFixIntent>(true)) {
                if (intent == null || !intent.enabled) continue;
                if (requireUpload && !intent.processOnUpload) continue;
                list.Add(intent);
            }
            return list;
        }

        private static RunSummary RunIntents(
                List<WhyKnotClippingFixIntent> intents,
                AvatarIntentSession session,
                string contextLabel) {

            var summary = new RunSummary { Context = contextLabel };
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            foreach (var intent in intents) {
                var renderer = intent.targetRenderer != null
                    ? intent.targetRenderer
                    : intent.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null || renderer.sharedMesh == null) {
                    summary.IntentsSkipped++;
                    continue;
                }

                var settings = SettingsFromIntent(intent);
                settings.Animator = ResolveAnimator(intent, renderer);
                StringBuilder log = intent.verboseLog ? new StringBuilder() : null;
                log?.AppendLine($"clipping fix verbose ({contextLabel})");
                log?.AppendLine($"  target={renderer.name}, comparisons={intent.comparisonRenderers?.Count ?? 0}, includePhysBoneMotion={settings.IncludePhysBoneMotion}");
                string signature = IntentPrecomputeUtility.BuildClippingSignature(
                    intent,
                    renderer,
                    intent.comparisonRenderers,
                    settings);
                var cacheElapsed = System.Diagnostics.Stopwatch.StartNew();
                if (!ClippingFixPrecomputeCache.TryLoad(intent, signature, out var initialIssues)) {
                    initialIssues = ClippingFixer.Scan(renderer, intent.comparisonRenderers, settings, log);
                    ClippingFixPrecomputeCache.Store(intent, signature, initialIssues);
                    cacheElapsed.Stop();
                    summary.CacheRebuilt++;
                    log?.AppendLine($"  precompute cache rebuilt: {initialIssues.Count} warning(s) in {cacheElapsed.Elapsed.TotalSeconds:0.00}s.");
                } else {
                    cacheElapsed.Stop();
                    summary.CacheReused++;
                    log?.AppendLine($"  precompute cache reused: {initialIssues.Count} warning(s) in {cacheElapsed.Elapsed.TotalSeconds:0.00}s.");
                }
                var result = ClippingFixer.ApplyNonDestructive(
                    renderer,
                    intent.comparisonRenderers,
                    settings,
                    session,
                    initialIssues);
                summary.IntentsProcessed++;
                summary.WarningsFound += result.IssuesFound;
                summary.VerticesReweighted += result.VerticesReweighted;
                summary.RenderersTouched += result.RenderersTouched;
                if (result.ConfigurationError) summary.IntentsSkipped++;

                if (log != null) {
                    log.AppendLine($"  {result.Summary}");
                    AvatarQolLogger.Instance.Info(log.ToString());
                }
            }
            elapsed.Stop();
            summary.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
            return summary;
        }

        internal static ClippingFixer.Settings SettingsFromIntent(WhyKnotClippingFixIntent intent) {
            if (intent == null) return new ClippingFixer.Settings();
            return new ClippingFixer.Settings {
                Animator = intent.animator,
                CheckSelf = intent.checkSelf,
                IncludePhysBoneMotion = intent.includePhysBoneMotion,
                InsideTolerance = intent.insideTolerance,
                SurfacePadding = intent.surfacePadding,
                PhysBoneWeightFloor = intent.physBoneWeightFloor,
                PhysBoneClearanceMargin = intent.physBoneClearanceMargin,
                PhysBoneMotionPinStrength = intent.physBoneMotionPinStrength,
                PhysBoneMotionBrushRadius = intent.physBoneMotionBrushRadius,
                MaxWarnings = 0,
                MaxIssuesPerPhysBone = intent.maxIssuesPerPhysBone,
            };
        }

        private static Animator ResolveAnimator(WhyKnotClippingFixIntent intent, SkinnedMeshRenderer renderer) {
            if (intent != null && intent.animator != null) return intent.animator;
            if (renderer != null) {
                var animator = renderer.GetComponentInParent<Animator>(true);
                if (animator != null) return animator;
            }
            return intent != null ? intent.GetComponentInParent<Animator>(true) : null;
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

        private static void LogSummary(RunSummary s) {
            if (s.IntentsProcessed == 0 && s.IntentsSkipped == 0) return;
            if (s.WarningsFound == 0 && s.VerticesReweighted == 0) return;
            AvatarQolLogger.Instance.Info(
                $"ClippingFix {s.Context}: processed {s.IntentsProcessed} intent(s) " +
                $"(skipped {s.IntentsSkipped}), touched {s.RenderersTouched} renderer(s), " +
                $"reweighted {s.VerticesReweighted} vertices from {s.WarningsFound} warning(s), " +
                $"cache reused {s.CacheReused}, rebuilt {s.CacheRebuilt}, elapsed {s.ElapsedSeconds:0.00}s.");
        }

        private struct RunSummary {
            public string Context;
            public int IntentsProcessed;
            public int IntentsSkipped;
            public int RenderersTouched;
            public int WarningsFound;
            public int VerticesReweighted;
            public int CacheReused;
            public int CacheRebuilt;
            public double ElapsedSeconds;
        }
    }
}
