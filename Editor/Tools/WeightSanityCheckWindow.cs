// WeightSanityCheckWindow.cs
//
// Detects cross-side weight contamination introduced by Blender data transfer
// and similar weight-transfer workflows. A vertex on one side of the avatar
// should not carry meaningful weight from a bone on the opposite side.
//
// Detection layers (in order of confidence):
//   1. Bone has a Humanoid ancestor on the OPPOSITE side of the vertex.
//      e.g. vertex on Left, bone descended from RightUpperLeg -> flagged.
//   2. Bone has NO Humanoid ancestor (custom rig bone, prop bone, etc.) but
//      its OWN world position sits on the OPPOSITE side of the vertex.
//      e.g. vertex on Left, bone is a custom bone whose pivot is on the
//      avatar's right side -> flagged with category "spatial".
//
// Center-band coverage: vertices in the centre stripe (between -centerMargin
// and +centerMargin in Hips local X) are also scanned, but only flagged when
// a single weight to a Left or Right bone exceeds a higher threshold
// (_centerCrossSideFloor). This catches stray spine/crotch weights without
// drowning the issue list in shoulder/clavicle bleed, which is normal.
//
// Vertex world-position derivation uses bind-pose math:
//   bonesPerVertex[v]'s highest-weight bone is the "anchor"; multiply the
//   mesh-local vertex by `mesh.bindposes[boneIdx]` to get bone-local
//   coords, then `bone.TransformPoint(...)` to get world. This is rig-
//   independent and doesn't rely on the renderer's GameObject sitting at
//   any particular place. An earlier version used renderer.transform or
//   rootBone directly; both produced wrong classifications on real-world
//   rigs where the mesh-local frame doesn't align with where the
//   GameObject sits; every vertex was bucketed as Center.
// Caveat: the bone's CURRENT world transform is used, so if the avatar is
// being driven by an animator the result is the deformed position rather
// than the bind-pose position. Pause animator / scrub to T-pose before
// scanning when in doubt.
//
// Wobble / debug:
//   - Per-issue *Wobble* button: rotates the offending bone back and forth
//     so deformation is visible. Click again to stop.
//   - Verbose log: dumps per-renderer scan stats to the console so skipped
//     weights are distinguishable by gate: unknown bone, weight floor, etc.
//   - "Dump weights for selection" button: takes the current SkinnedMeshRenderer
//     selection and prints every vertex's bone weights with side classifications.
//     Pinpoint debugging when an issue is missed.
//
// Not covered:
//   - Bone-graph distance violations (vertices weighted to bones very far
//     apart in the hierarchy).
//   - Per-island weight variance.
//   - Mutate weights ("fix" them). The tool is a checker; humans review
//     before changing skinning data.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.WeightFixes;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class WeightSanityCheckWindow : EditorWindow {

        // Persisted across domain reloads.
        [SerializeField] private Animator _animator;
        // Optional: when set, Scan walks only this renderer instead of every
        // SkinnedMeshRenderer under the avatar. Useful for focusing on a
        // specific outfit or mesh while debugging without touching the
        // exclusion list.
        [SerializeField] private SkinnedMeshRenderer _limitToRenderer;
        // Detection defaults are maximum-sensitivity: every cross-side
        // weight surfaces, with category filtering handled in the issue list.
        // Earlier defaults (weightFloor 0.001, centerMargin 0.005,
        // scanCenterBand true) skipped real bleed that mattered on real
        // garments -- weights in the 0.0001..0.001 band still stretch the
        // mesh visibly under motion, and the centre stripe swallowed
        // accessories sitting on the centerline.
        //   weightFloor 0 -- no floor; every non-zero cross-side weight
        //     is flagged. Tune up if a body mesh produces too much noise.
        //   centerMargin 0 -- no centre stripe; every vertex is Left or
        //     Right by sign of Hips-local X. Tune up only when bind-pose
        //     noise around the spine produces false positives.
        //   scanCenterBand false -- moot at margin 0 (no Center vertices
        //     exist), kept off so toggling centerMargin up later does not
        //     silently re-enable the higher centre threshold path.
        [SerializeField] private float _weightFloor   = 0f;
        [SerializeField] private float _centerMargin  = 0f;
        [SerializeField] private bool  _scanCenterBand = false;
        [SerializeField] private float _centerCrossSideFloor = 0.10f;
        [SerializeField] private bool  _showGizmos    = true;
        [SerializeField] private bool  _verboseLog    = false;

        // Vertex inspector: accepts a vertex index from manual entry or a dump,
        // then walks every weight on that vertex with full
        // verdict reasoning, so it's possible to tell *exactly* why a weight
        // wasn't flagged.
        [SerializeField] private SkinnedMeshRenderer _inspectRenderer;
        [SerializeField] private int _inspectVertexIndex = 0;

        [SerializeField] private List<SkinnedMeshRenderer> _excludedRenderers = new List<SkinnedMeshRenderer>();

        // UI state - persisted across reloads so a power user who opened
        // Advanced once doesn't have to re-open it every session.
        [SerializeField] private bool _advancedOpen;
        [SerializeField] private bool _showConsoleNoticeAfterInspect;
        [SerializeField] private bool _showConsoleNoticeAfterDump;
        // Per-renderer collapsed state in the issue list, keyed by RendererPath.
        [SerializeField] private List<string> _collapsedRenderers = new List<string>();
        // Per-issue expansion in the compact issue rows.
        private readonly HashSet<int> _expandedIssueRows = new HashSet<int>();

        private const string WikiUrl = "https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview#weight-sanity-check";

        private readonly List<DetectedIssue> _issues = new List<DetectedIssue>();
        private readonly HashSet<int> _selectedIssueIndices = new HashSet<int>();
        // Tracked per scan so we can offer a "Enable Read/Write on these N
        // meshes" button below the scan output.
        private readonly List<SkinnedMeshRenderer> _nonReadableRenderers = new List<SkinnedMeshRenderer>();
        private string _scanSummary = "";
        // Aggregate of vertex-side classification across the last scan,
        // used by DrawSanityBanner when the issue list is empty despite
        // the mesh having plenty of geometry.
        private int _lastScanLeftRightVerts;
        private int _lastScanCenterVerts;
        private Vector2 _scroll;
        private Vector2 _pageScroll;
        private string _autoSizeSignature;

        // Preview state - at most one bone is animated at a time. Bone is
        // wobbled around its rest rotation; on stop we restore.
        private Transform _previewBone;
        private Quaternion _previewRestRotation;
        private double _previewStart;

        // ------ Public entry points ----------------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<WeightSanityCheckWindow>(false, "Weight Sanity Check", true);
            w.titleContent = WkStyles.TitleContent("Avatar QoL - Weight Sanity Check");
            w.minSize = new Vector2(600, 460);
            if (prefillFromSelection) {
                var sel = Selection.activeGameObject;
                if (sel != null) {
                    var anim = sel.GetComponent<Animator>() ?? sel.GetComponentInChildren<Animator>(true);
                    if (anim != null && anim.isHuman) w._animator = anim;
                }
            }
            w.Show();
            w.Focus();
        }

        // ------ Lifecycle --------------------------------------------------

        private void OnEnable() {
            SceneView.duringSceneGui += OnSceneGui;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable() {
            SceneView.duringSceneGui -= OnSceneGui;
            EditorApplication.update -= OnEditorUpdate;
            StopPreview();
        }

        private void OnDestroy() {
            StopPreview();
        }

        // ------ GUI --------------------------------------------------------

        private void OnGUI() {
            using var _wkTheme = WkStyles.Scope(WkTheme.WhyKnot);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true))) {
                DrawTitleBar();

                using (var s = new EditorGUILayout.ScrollViewScope(
                        _pageScroll, false, false,
                        GUILayout.ExpandWidth(true),
                        GUILayout.ExpandHeight(true))) {
                    _pageScroll = s.scrollPosition;
                    // Top-of-window banner only when applicable; otherwise it lives
                    // inside Advanced. Hoisting it here keeps half-skipped scans visible.
                    if (_nonReadableRenderers.Count > 0) DrawNonReadableBanner();
                    WkStyles.Notice(NoticeKind.Info,
                        "Flow: pick the avatar Animator, scan, review weight rows, then fix or tune what matters. Mesh clipping has its own window so this scan stays fast.");
                    DrawHeader();
                    EditorGUILayout.Space(2);
                    DrawScanBar();
                    EditorGUILayout.Space(2);
                    DrawIssues();
                    EditorGUILayout.Space(4);
                    DrawAdvanced();
                }

                WkStyles.WindowFooter();
            }
            RequestAutoSize();
        }

        private void RequestAutoSize() {
            var animatorId = _animator != null ? _animator.GetInstanceID() : 0;
            var limitId = _limitToRenderer != null ? _limitToRenderer.GetInstanceID() : 0;
            var signature = $"{animatorId}|{limitId}|{_issues.Count}|{SelectedIssueCount()}|{_scanSummary}|{_advancedOpen}|{_excludedRenderers.Count}|{_nonReadableRenderers.Count}";
            var preferred = new Vector2(
                _issues.Count > 0 ? 900f : 680f,
                390f + WkStyles.CappedListHeight(_issues.Count, 24f, 120f, 280f) + (_advancedOpen ? 120f : 0f));
            WkStyles.AutoSizeWindow(
                this,
                ref _autoSizeSignature,
                signature,
                new Vector2(600f, 460f),
                preferred,
                new Vector2(1040f, 780f));
        }






        // Issue record + IssueCategory enum moved to
        // WhyKnot.AvatarQol.WeightFixes (DetectedIssue.cs) so the runtime
        // apply hook can speak the same shape without referencing this
        // editor window.
    }
}
