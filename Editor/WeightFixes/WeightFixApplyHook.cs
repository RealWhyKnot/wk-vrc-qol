// WeightFixApplyHook.cs
//
// Runs the cross-side weight detector + fixer for every
// WhyKnotWeightFixIntent component at two lifecycle points:
//
//   - EditorApplication.playModeStateChanged.ExitingEditMode (Play-mode entry)
//   - IVRCSDKPreprocessAvatarCallback                       (Build & Publish upload)
//
// On exit / post-build, captured originals are restored from each session
// so the source mesh asset is never modified -- edit-time the user sees
// the un-fixed mesh, play / build see the fixed clone.
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
// Why a private WeightFixSession rather than the shared
// AvatarIntentSession: weight fixes predate the unified session and
// keep their own per-renderer state so a failure in this hook does not
// disturb anything BoneMerger / PhysBonePreset has captured against the
// same avatar. The two sessions never share clones.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Tools;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.WeightFixes {

    [InitializeOnLoad]
    internal sealed class WeightFixApplyHook :
        IVRCSDKPreprocessAvatarCallback,
        IVRCSDKPostprocessAvatarCallback {

        public int callbackOrder => -5000;

        private static readonly Dictionary<GameObject, WeightFixSession> _uploadSessions =
            new Dictionary<GameObject, WeightFixSession>();
        private static readonly Dictionary<GameObject, WeightFixSession> _playModeSessions =
            new Dictionary<GameObject, WeightFixSession>();
        private static GameObject _activeUploadAvatar;

        static WeightFixApplyHook() {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        // ---- Upload path -------------------------------------------------

        public bool OnPreprocessAvatar(GameObject avatarGameObject) {
            if (avatarGameObject == null) return true;

            // Defensive: if a prior upload exception bypassed
            // OnPostprocessAvatar, restore anything still held for this
            // avatar before we touch its renderers again.
            DisposeSession(_uploadSessions, avatarGameObject);

            var intents = CollectIntents(avatarGameObject, requireUpload: true);
            if (intents.Count == 0) return true;

            var animator = avatarGameObject.GetComponentInChildren<Animator>(true);
            if (animator == null || !animator.isHuman) {
                AvatarQolLogger.Instance.Warning(
                    $"WeightFix upload skipped on '{avatarGameObject.name}': no Humanoid Animator. " +
                    $"Cross-side detection needs Humanoid bone bindings.");
                return true;
            }

            var session = new WeightFixSession();
            _activeUploadAvatar = avatarGameObject;
            var summary = RunIntents(intents, animator, session, contextLabel: $"upload ({avatarGameObject.name})");
            if (!session.HasChanges) {
                session.Dispose();
                _activeUploadAvatar = null;
                return true;
            }
            _uploadSessions[avatarGameObject] = session;
            LogSummary(summary);
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
        }

        // ---- Play-mode path ----------------------------------------------

        private static void OnPlayModeStateChanged(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingEditMode) {
                ProcessForPlayMode();
            } else if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.ExitingPlayMode) {
                DisposeAllSessions(_playModeSessions);
                // Defensively unwind any leaked upload sessions: an aborted
                // Build & Publish that never reached OnPostprocessAvatar
                // would otherwise pin clones in memory until the next
                // domain reload.
                DisposeAllSessions(_uploadSessions);
                _activeUploadAvatar = null;
            }
        }

        private static void ProcessForPlayMode() {
            DisposeAllSessions(_playModeSessions);

            // Group intents by avatar root so each avatar gets its own
            // session -- a failure on one avatar shouldn't unwind the
            // others in the scene.
            var byRoot = new Dictionary<GameObject, List<WhyKnotWeightFixIntent>>();
            foreach (var intent in Resources.FindObjectsOfTypeAll<WhyKnotWeightFixIntent>()) {
                if (intent == null || !intent.enabled || !intent.processInPlayMode) continue;
                if (EditorUtility.IsPersistent(intent)) continue;
                var go = intent.gameObject;
                if (go == null || !go.scene.IsValid() || !go.scene.isLoaded) continue;
                var animator = intent.GetComponentInParent<Animator>(true);
                var root = animator != null ? animator.gameObject : TopLevel(intent.transform).gameObject;
                if (!byRoot.TryGetValue(root, out var list)) {
                    list = new List<WhyKnotWeightFixIntent>();
                    byRoot[root] = list;
                }
                list.Add(intent);
            }
            if (byRoot.Count == 0) return;

            foreach (var kv in byRoot) {
                var root = kv.Key;
                var animator = root.GetComponentInChildren<Animator>(true);
                if (animator == null || !animator.isHuman) {
                    AvatarQolLogger.Instance.Warning(
                        $"WeightFix play-mode skipped on '{root.name}': no Humanoid Animator. " +
                        $"Cross-side detection needs Humanoid bone bindings.");
                    continue;
                }

                var session = new WeightFixSession();
                var summary = RunIntents(kv.Value, animator, session, contextLabel: $"play mode ({root.name})");
                if (!session.HasChanges) {
                    session.Dispose();
                    continue;
                }
                _playModeSessions[root] = session;
                LogSummary(summary);
            }
        }

        private static Transform TopLevel(Transform transform) {
            while (transform != null && transform.parent != null) transform = transform.parent;
            return transform;
        }

        // ---- Shared apply --------------------------------------------------

        internal struct RunSummary {
            public string Context;
            public int IntentsProcessed;
            public int IntentsSkipped;
            public int RenderersTouched;
            public int IssuesFound;
            public int FixesApplied;
        }

        private static List<WhyKnotWeightFixIntent> CollectIntents(GameObject avatarRoot, bool requireUpload) {
            var list = new List<WhyKnotWeightFixIntent>();
            if (avatarRoot == null) return list;
            foreach (var intent in avatarRoot.GetComponentsInChildren<WhyKnotWeightFixIntent>(true)) {
                if (intent == null || !intent.enabled) continue;
                if (requireUpload && !intent.processOnUpload) continue;
                list.Add(intent);
            }
            return list;
        }

        private static RunSummary RunIntents(
                List<WhyKnotWeightFixIntent> intents,
                Animator animator,
                WeightFixSession session,
                string contextLabel) {
            return RunIntentsWith(intents, animator, session.Capture, session.CloneAndTrack, contextLabel);
        }

        /// <summary>
        /// Run the weight-fix detection + fixer for the supplied intents,
        /// recording every captured renderer through <paramref name="capture"/>
        /// and every cloned mesh through <paramref name="cloneAndTrack"/> so
        /// either WeightFixSession or AvatarIntentSession can own the cleanup.
        /// Used by the apply hook (WeightFixSession) and by the preview path
        /// (AvatarIntentSession wrapper in the inspector and the window).
        /// </summary>
        internal static RunSummary RunIntentsWith(
                List<WhyKnotWeightFixIntent> intents,
                Animator animator,
                System.Action<SkinnedMeshRenderer> capture,
                System.Func<Mesh, Mesh> cloneAndTrack,
                string contextLabel) {
            var summary = new RunSummary { Context = contextLabel };

            var sideMap = new HumanoidSideMap(animator);
            if (!sideMap.IsValid) {
                AvatarQolLogger.Instance.Warning(
                    $"WeightFix {contextLabel}: HumanoidSideMap invalid (Hips missing). All intents skipped.");
                summary.IntentsSkipped = intents.Count;
                return summary;
            }

            foreach (var intent in intents) {
                var renderer = intent.targetRenderer != null
                    ? intent.targetRenderer
                    : intent.GetComponent<SkinnedMeshRenderer>();
                if (renderer == null) {
                    summary.IntentsSkipped++;
                    AvatarQolLogger.Instance.Warning(
                        $"WeightFix {contextLabel}: intent on '{intent.name}' has no targetRenderer and no SkinnedMeshRenderer on the same GameObject; skipped.");
                    continue;
                }
                var sourceMesh = renderer.sharedMesh;
                if (sourceMesh == null) {
                    summary.IntentsSkipped++;
                    continue;
                }

                var p = new ScanParameters {
                    WeightFloor = intent.weightFloor,
                    CenterMargin = intent.centerMargin,
                    ScanCenterBand = intent.scanCenterBand,
                    CenterCrossSideFloor = intent.centerCrossSideFloor,
                };

                StringBuilder log = intent.verboseLog ? new StringBuilder() : null;
                log?.AppendLine($"WeightFix verbose ({contextLabel})");
                log?.AppendLine($"  weightFloor={p.WeightFloor:F4}, centerMargin={p.CenterMargin:F3}, scanCenterBand={p.ScanCenterBand}, centerCrossSideFloor={p.CenterCrossSideFloor:F3}");

                string signature = IntentPrecomputeUtility.BuildWeightFixSignature(
                    intent,
                    animator,
                    renderer,
                    p);
                if (!WeightFixPrecomputeCache.TryLoad(intent, signature, out var detectedIssues)) {
                    var detect = WeightCrossSideDetector.Detect(renderer, sideMap, p, log);
                    detectedIssues = detect.Issues;
                    WeightFixPrecomputeCache.Store(intent, signature, detectedIssues);
                    log?.AppendLine($"  precompute cache rebuilt: {detectedIssues.Count} issue(s).");
                } else {
                    log?.AppendLine($"  precompute cache reused: {detectedIssues.Count} issue(s).");
                }

                if (detectedIssues.Count == 0) {
                    if (log != null) {
                        log.AppendLine($"  no cross-side weights found; mesh left untouched.");
                        AvatarQolLogger.Instance.Info(log.ToString());
                    }
                    summary.IntentsProcessed++;
                    continue;
                }
                summary.IssuesFound += detectedIssues.Count;

                // Clone now so the session owns the original. Capture the
                // renderer before assigning so Dispose can put back the
                // exact mesh reference the renderer had before we touched
                // it (handles the case where some other tool had already
                // assigned a custom mesh asset to this renderer).
                capture(renderer);
                var clone = cloneAndTrack(sourceMesh);
                if (clone == null) {
                    summary.IntentsSkipped++;
                    continue;
                }

                var fixResult = new WeightFixer.FixResult();
                var fixerIssues = detectedIssues
                    .Select(i => new WeightFixer.IssueRef {
                        Renderer = i.Renderer,
                        VertexIndex = i.VertexIndex,
                        OffendingBone = i.OffendingBone,
                        Weight = i.Weight,
                    })
                    .ToList();
                WeightFixer.ApplyFixesToMeshInPlace(clone, renderer.bones, fixerIssues, animator, fixResult);
                renderer.sharedMesh = clone;

                summary.RenderersTouched++;
                summary.FixesApplied += fixResult.Fixed;
                summary.IntentsProcessed++;

                if (log != null) {
                    log.AppendLine($"  applied: fixed={fixResult.Fixed} (mirrored={fixResult.Mirrored}, zeroed={fixResult.Zeroed}), skipped={fixResult.Skipped}");
                    AvatarQolLogger.Instance.Info(log.ToString());
                }
            }

            return summary;
        }

        private static void DisposeSession(Dictionary<GameObject, WeightFixSession> map, GameObject avatarRoot) {
            if (avatarRoot == null || !map.TryGetValue(avatarRoot, out var session)) return;
            session?.Dispose();
            map.Remove(avatarRoot);
        }

        private static void DisposeAllSessions(Dictionary<GameObject, WeightFixSession> map) {
            foreach (var session in map.Values) session?.Dispose();
            map.Clear();
        }

        private static void LogSummary(RunSummary s) {
            if (s.IntentsProcessed == 0 && s.IntentsSkipped == 0) return;
            AvatarQolLogger.Instance.Info(
                $"WeightFix {s.Context}: processed {s.IntentsProcessed} intent(s) " +
                $"(skipped {s.IntentsSkipped}), touched {s.RenderersTouched} renderer(s), " +
                $"applied {s.FixesApplied} fix(es) from {s.IssuesFound} detected issue(s).");
        }
    }
}
