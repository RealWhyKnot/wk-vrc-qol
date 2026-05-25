// WeightSanityCheckWindow.cs
//
// Detects the most common kind of weight contamination introduced by
// Blender's Data Transfer / robust weight transfer: vertices on one side of
// the avatar (say, a garter on the LEFT leg) getting non-trivial weight
// from a bone on the OTHER side (Right leg). When the avatar moves, those
// stray weights stretch or follow the wrong limb.
//
// Detection layers (in order of confidence):
//   1. Bone has a Humanoid ancestor on the OPPOSITE side of the vertex.
//      e.g. vertex on Left, bone descended from RightUpperLeg → flagged.
//   2. Bone has NO Humanoid ancestor (custom rig bone, prop bone, etc.) but
//      its OWN world position sits on the OPPOSITE side of the vertex.
//      e.g. vertex on Left, bone is a custom bone whose pivot is on the
//      avatar's right side → flagged with category "spatial".
//
// Center-band coverage: vertices in the centre stripe (between -centerMargin
// and +centerMargin in Hips local X) are also scanned, but only flagged when
// a single weight to a Left or Right bone exceeds a higher threshold
// (_centerCrossSideFloor). This catches stray spine/crotch weights without
// drowning the user in shoulder/clavicle bleed (which is normal).
//
// Vertex world-position derivation: we use proper bind-pose math —
//   bonesPerVertex[v]'s highest-weight bone is the "anchor"; multiply the
//   mesh-local vertex by `mesh.bindposes[boneIdx]` to get bone-local
//   coords, then `bone.TransformPoint(...)` to get world. This is rig-
//   independent and doesn't rely on the renderer's GameObject sitting at
//   any particular place. (An earlier version used renderer.transform or
//   rootBone directly; both produced wrong classifications on real-world
//   rigs where the mesh-local frame doesn't align with where the
//   GameObject sits — symptom: every vertex got bucketed as Center.)
// Caveat: the bone's CURRENT world transform is used, so if the avatar is
// being driven by an animator the result is the deformed position rather
// than the bind-pose position. Pause animator / scrub to T-pose before
// scanning when in doubt.
//
// Wobble / debug:
//   - Per-issue *Wobble* button: rotates the offending bone back and forth
//     so the user can watch the deformation. Click again to stop.
//   - Verbose log: dumps per-renderer scan stats to the console — exactly
//     why each weight was flagged or skipped, so it's possible to tell
//     "we didn't see this issue because the bone is Unknown" vs "the weight
//     was below the floor."
//   - "Dump weights for selection" button: takes the current SkinnedMeshRenderer
//     selection and prints every vertex's bone weights with side classifications.
//     Pinpoint debugging when an issue is missed.
//
// What we deliberately don't do (yet):
//   - Bone-graph distance violations (vertices weighted to bones very far
//     apart in the hierarchy).
//   - Per-island weight variance.
//   - Mutate weights ("fix" them). The tool is a checker — humans review
//     before changing skinning data.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.WeightFixes;
using WhyKnot.Core.Styling;
using WhyKnot.Core.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class WeightSanityCheckWindow : EditorWindow {

        // Persisted across domain reloads.
        [SerializeField] private Animator _animator;
        // Optional: when set, Scan walks only this renderer instead of every
        // SkinnedMeshRenderer under the avatar. Lets the user focus on a
        // specific outfit / mesh while debugging without touching the
        // exclusion list.
        [SerializeField] private SkinnedMeshRenderer _limitToRenderer;
        // Detection defaults are maximum-sensitivity: every cross-side
        // weight surfaces and the user filters by category in the issue
        // list. Earlier defaults (weightFloor 0.001, centerMargin 0.005,
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

        // Vertex inspector: the user types a vertex index (or picks one from
        // a dump) and the tool walks every weight on that vertex with full
        // verdict reasoning, so it's possible to tell *exactly* why a weight
        // wasn't flagged.
        [SerializeField] private SkinnedMeshRenderer _inspectRenderer;
        [SerializeField] private int _inspectVertexIndex = 0;

        [SerializeField] private List<SkinnedMeshRenderer> _excludedRenderers = new List<SkinnedMeshRenderer>();

        // UI state — persisted across reloads so a power user who opened
        // Advanced once doesn't have to re-open it every session.
        [SerializeField] private bool _advancedOpen;
        [SerializeField] private bool _showConsoleNoticeAfterInspect;
        [SerializeField] private bool _showConsoleNoticeAfterDump;
        // Per-renderer collapsed state in the issue list, keyed by RendererPath.
        [SerializeField] private List<string> _collapsedRenderers = new List<string>();
        // Per-issue expansion in the compact issue rows.
        private readonly HashSet<int> _expandedIssueRows = new HashSet<int>();

        private const string WikiUrl = "https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Tools-Overview#weight-sanity-check";

        private readonly List<DetectedIssue> _issues = new List<DetectedIssue>();
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

        // Preview state — at most one bone is animated at a time. Bone is
        // wobbled around its rest rotation; on stop we restore.
        private Transform _previewBone;
        private Quaternion _previewRestRotation;
        private double _previewStart;

        // ------ Public entry points ----------------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<WeightSanityCheckWindow>(false, "Weight Sanity Check", true);
            w.titleContent = new GUIContent("Avatar QoL — Weight Sanity Check");
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
            // Top-of-window banner only when applicable; otherwise it lives
            // inside Advanced. Hoisting it here keeps the user from missing
            // a half-skipped scan.
            if (_nonReadableRenderers.Count > 0) DrawNonReadableBanner();
            DrawTitleBar();
            WkStyles.Notice(NoticeKind.Info,
                "Flow: pick the avatar Animator, scan, review weight rows, then fix or tune what matters. PhysBone clipping has its own window so this scan stays fast.");
            DrawHeader();
            EditorGUILayout.Space(2);
            DrawScanBar();
            EditorGUILayout.Space(2);
            DrawIssues();
            EditorGUILayout.Space(4);
            DrawAdvanced();
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Weight Sanity Check",
                        "Find mesh weights that pull part of the avatar toward the wrong left/right side, then review or fix them."),
                    WkStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawHeader() {
            using (WkStyles.Section("1. Pick avatar",
                    "Choose the Humanoid avatar to scan. Optionally narrow the scan to one renderer while debugging an outfit or mesh.")) {
                WkStyles.LabeledField(
                    new GUIContent("Animator",
                        "The Humanoid Animator at the root of your avatar. The scan walks every SkinnedMeshRenderer underneath it and uses the Humanoid bone bindings (Hips, LeftUpperLeg, RightUpperLeg) to derive the avatar's left/right axis. Generic / non-Humanoid rigs aren't supported."),
                    () => {
                        var newAnim = (Animator)EditorGUILayout.ObjectField(_animator, typeof(Animator), true);
                        if (newAnim != _animator) { _animator = newAnim; _issues.Clear(); _scanSummary = ""; }
                    });
                if (_animator != null && !_animator.isHuman) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "Animator is not Humanoid. The symmetry check needs Humanoid bone bindings (LeftUpperLeg, RightUpperLeg, Hips).");
                }
                WkStyles.LabeledField(
                    new GUIContent("Only scan renderer",
                        "Optional. When set, Scan only walks this single SkinnedMeshRenderer instead of every renderer under the avatar. Useful when debugging one outfit / mesh without touching the exclusion list. Auto-fills the Inspect Vertex renderer below."),
                    () => {
                        var newLimit = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_limitToRenderer, typeof(SkinnedMeshRenderer), true);
                        if (newLimit != _limitToRenderer) {
                            _limitToRenderer = newLimit;
                            if (newLimit != null && _inspectRenderer == null) _inspectRenderer = newLimit;
                        }
                    });
                if (_animator != null && _limitToRenderer != null
                        && !_limitToRenderer.transform.IsChildOf(_animator.transform)) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "The 'Limit scan to' renderer is not a descendant of the picked Animator. The scan will still run on it, but side classification uses the Animator's Hips so it may misbehave for renderers parented elsewhere.");
                }
            }
        }

        private void DrawAdvanced() {
            _advancedOpen = EditorGUILayout.Foldout(_advancedOpen,
                new GUIContent("Advanced",
                    "Detection thresholds, exclusion list, diagnostics (verbose log, gizmos), and the per-vertex inspector. Folded by default — most users only need Animator + Scan + Fix all."),
                true, WkStyles.FoldoutHeader);
            if (!_advancedOpen) return;
            DrawTunables();
            EditorGUILayout.Space(2);
            DrawExclusions();
            EditorGUILayout.Space(2);
            DrawDiagnostics();
        }

        private void DrawTunables() {
            using (WkStyles.Section("Detection thresholds",
                    "Knobs that control how aggressive the scanner is. Sensible defaults; tune only when the issue list is too noisy or too quiet.")) {
                WkStyles.LabeledField(
                    new GUIContent("Weight floor",
                        "Weights below this fraction are ignored as noise. Default 0 surfaces every cross-side weight, however small -- bleed in the 0.0001..0.001 band still stretches the mesh visibly under motion. Raise toward 0.02 if a body mesh is flagging too much. Range 0..0.5."),
                    () => _weightFloor = EditorGUILayout.Slider(_weightFloor, 0f, 0.5f));
                WkStyles.LabeledField(
                    new GUIContent("Center margin",
                        "Half-width of the on-spine centre stripe in metres, in Hips local X. Vertices within +/- this distance of the spine count as Center. Default 0 disables the stripe so every vertex is Left or Right by sign of Hips-local X. Raise toward 0.005 only if bind-pose noise around the spine is producing false positives. Range 0..0.2 m."),
                    () => _centerMargin = EditorGUILayout.Slider(_centerMargin, 0f, 0.2f));
                using (new EditorGUILayout.HorizontalScope()) {
                    _scanCenterBand = EditorGUILayout.ToggleLeft(
                        new GUIContent("Scan centre-band vertices",
                            "When on, centre-stripe vertices are scanned for cross-side weights using the higher centre threshold below (rather than skipped). Off by default; only meaningful once Center margin is raised above 0 so centre-stripe vertices actually exist."),
                        _scanCenterBand, GUILayout.Width(220));
                }
                if (_scanCenterBand) {
                    WkStyles.LabeledField(
                        new GUIContent("Centre threshold",
                            "Minimum weight a centre-stripe vertex must have to a Left or Right bone before it's flagged. Higher than the regular floor because small bleed near the spine is usually fine."),
                        () => _centerCrossSideFloor = EditorGUILayout.Slider(_centerCrossSideFloor, 0f, 0.5f));
                }
            }
        }

        private void DrawDiagnostics() {
            using (WkStyles.Section("Diagnostics",
                    "Visualisation, logging, and the per-vertex Inspect tool. Helpful when the scan isn't flagging something you expected, or when triaging hundreds of issues.")) {
                using (new EditorGUILayout.HorizontalScope()) {
                    _showGizmos = EditorGUILayout.ToggleLeft(
                        new GUIContent("Show gizmos in Scene view",
                            "Draw a red marker at every flagged vertex's bind-pose world position. Helps you see where issues cluster on the avatar."),
                        _showGizmos, GUILayout.Width(220));
                    _verboseLog = EditorGUILayout.ToggleLeft(
                        new GUIContent("Verbose log",
                            "On scan, dump per-renderer stats and per-skipped-weight reasons to the Unity console. Useful for understanding why a weight you expected isn't being flagged."),
                        _verboseLog);
                }
                EditorGUILayout.Space(4);
                DrawVertexInspector();
                if (_showConsoleNoticeAfterInspect) {
                    if (WkStyles.ConsoleResultNotice("Inspect output")) {
                        EditorApplication.ExecuteMenuItem("Window/General/Console");
                        _showConsoleNoticeAfterInspect = false;
                    }
                }
                EditorGUILayout.Space(2);
                if (GUILayout.Button(
                        new GUIContent("Dump weights for selection",
                            "Print every vertex's bone weights for the currently selected SkinnedMeshRenderer to the Unity console. Useful when an issue you expect isn't being flagged."),
                        GUILayout.Height(22))) {
                    DumpSelectedRendererWeights();
                }
                if (_showConsoleNoticeAfterDump) {
                    if (WkStyles.ConsoleResultNotice("Weight dump")) {
                        EditorApplication.ExecuteMenuItem("Window/General/Console");
                        _showConsoleNoticeAfterDump = false;
                    }
                }
            }
        }

        private void DrawExclusions() {
            using (WkStyles.Section("Exclude renderers (legit cross-side)",
                    "Add any SkinnedMeshRenderer that bridges left/right by design (capes, dresses, tails). They won't be scanned.")) {
                if (_excludedRenderers.Count == 0) {
                    EditorGUILayout.LabelField("(none)", EditorStyles.centeredGreyMiniLabel);
                } else {
                    int removeIndex = -1;
                    for (int i = 0; i < _excludedRenderers.Count; i++) {
                        using (new EditorGUILayout.HorizontalScope()) {
                            _excludedRenderers[i] = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                                new GUIContent(GUIContent.none.image, "A SkinnedMeshRenderer the scan should ignore (e.g. capes, dresses, tails that legitimately bridge left and right)."),
                                _excludedRenderers[i], typeof(SkinnedMeshRenderer), true);
                            if (GUILayout.Button(new GUIContent("×", "Remove this renderer from the exclusion list."),
                                    EditorStyles.miniButton, GUILayout.Width(22))) removeIndex = i;
                        }
                    }
                    if (removeIndex >= 0) _excludedRenderers.RemoveAt(removeIndex);
                }
                if (GUILayout.Button(new GUIContent("Add row", "Append an empty slot for a new renderer to exclude. Drop a SkinnedMeshRenderer onto it after."),
                        EditorStyles.miniButton, GUILayout.Width(80))) {
                    _excludedRenderers.Add(null);
                }
            }
        }

        private void DrawScanBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                bool canScan = _animator != null && _animator.isHuman;
                using (new EditorGUI.DisabledScope(!canScan)) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Scan",
                                "Walk every SkinnedMeshRenderer under the Animator (or just the renderer selected above) and flag vertices weighted to a bone on the avatar's opposite side. Run again any time after a fix to refresh."),
                            GUILayout.MinWidth(140))) Scan();
                }
                using (new EditorGUI.DisabledScope(_previewBone == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Stop wobble",
                                "Restore the currently-wobbled bone to its rest rotation."),
                            GUILayout.Height(28), GUILayout.Width(110))) {
                        StopPreview();
                    }
                }
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(_scanSummary)) {
                    EditorGUILayout.LabelField(_scanSummary, WkStyles.Muted);
                }
            }
        }

        private void DrawIssues() {
            // Header bar: "Issues (N)" + Fix all + Clear, attached to the
            // list so the action sits next to what it acts on.
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent(_issues.Count > 0 ? $"Issues ({_issues.Count})" : "Issues",
                        "Step 3. Each row is one suspicious bone weight on one vertex. The bracketed tag shows confidence: [humanoid] = bone is on the wrong Humanoid side, [spatial] = inferred from world position, [center] = mid-line bleed."),
                    WkStyles.SubsectionTitle);
                GUILayout.FlexibleSpace();
                bool isPreviewing = AvatarPreviewController.IsPreviewing
                    && _animator != null
                    && AvatarPreviewController.SourceAvatar == _animator.gameObject;
                using (new EditorGUI.DisabledScope(_issues.Count == 0)) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Save fixes as component",
                                "Recommended for fixes you want to survive a Blender re-import. Adds (or updates) a WhyKnotWeightFixIntent component on each renderer with issues. At play-mode entry and at avatar upload, the fix re-scans the renderer's CURRENT mesh and applies corrections to an in-memory clone -- the source mesh asset is never modified, and the fix follows the mesh through topology changes."),
                            GUILayout.Width(190))) {
                        SaveIssuesAsComponents();
                    }
                    using (new EditorGUI.DisabledScope(_animator == null || isPreviewing)) {
                        if (GUILayout.Button(
                                new GUIContent("Preview",
                                    "Non-destructive. Clone the avatar in place and apply the listed fixes to the clone so you can see the deformation without committing changes."),
                                GUILayout.Height(28), GUILayout.Width(96))) {
                            StartPreview(new List<DetectedIssue>(_issues));
                        }
                    }
                    if (GUILayout.Button(
                            new GUIContent($"Fix all ({_issues.Count})",
                                "Destructive: write corrected weights into a cloned .mesh asset under Assets/AvatarQol Generated/ and rewire the renderer to the clone now. Faster feedback than the component flow but the renderer reference is lost if the FBX subasset is regenerated (Blender re-export). Prefer 'Save fixes as component' when in doubt."),
                            GUILayout.Height(28), GUILayout.Width(110))) {
                        FixIssues(new List<DetectedIssue>(_issues), $"{_issues.Count} issue(s)");
                    }
                    using (new EditorGUI.DisabledScope(!isPreviewing)) {
                        if (GUILayout.Button(
                                new GUIContent("Stop preview", "Destroy the preview clone and un-hide the source avatar."),
                                GUILayout.Height(28), GUILayout.Width(110))) {
                            AvatarPreviewController.StopPreview();
                        }
                    }
                    if (GUILayout.Button(
                            new GUIContent("Clear",
                                "Drop the current issue list and clear the gizmo overlay. Doesn't undo any fixes you've already applied."),
                            GUILayout.Height(28), GUILayout.Width(70))) {
                        _issues.Clear();
                        _scanSummary = "";
                        _expandedIssueRows.Clear();
                        SceneView.RepaintAll();
                    }
                }
            }

            // Inline legend so the bracket tags aren't mystery jargon.
            if (_issues.Count > 0) {
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField("Legend:", WkStyles.Muted, GUILayout.Width(54));
                    WkStyles.BadgePill("humanoid", AvatarQolCategoryColors.Humanoid,
                        "Bone has a Humanoid ancestor on the avatar's opposite side from the vertex. Highest-confidence flag.");
                    WkStyles.BadgePill("spatial", AvatarQolCategoryColors.Spatial,
                        "Bone has no Humanoid ancestor; its world pivot sits on the opposite side of the vertex.");
                    WkStyles.BadgePill("center", AvatarQolCategoryColors.Center,
                        "Vertex is in the centre stripe; a Left or Right bone exceeded the higher centre threshold.");
                    GUILayout.FlexibleSpace();
                }
            }

            // Diagnostic banner for the "scan ran, found nothing, but most
            // of the geometry was filtered as Center" failure mode -- the
            // exact situation that bit the user on the Maid Accessories
            // mesh before the default centerMargin came down. Only fires
            // after a scan has actually run (_scanSummary populated) so
            // first-launch doesn't flash a help box at an empty list.
            if (_issues.Count == 0 && !string.IsNullOrEmpty(_scanSummary)
                    && _lastScanCenterVerts > 0
                    && _lastScanCenterVerts > (_lastScanLeftRightVerts + _lastScanCenterVerts) * 8 / 10) {
                int total = _lastScanLeftRightVerts + _lastScanCenterVerts;
                int pct = total > 0 ? (_lastScanCenterVerts * 100 / total) : 0;
                WkStyles.Notice(NoticeKind.Warning,
                    $"{pct}% of vertices ({_lastScanCenterVerts:N0} of {total:N0}) classified as Center and were filtered. " +
                    "Lower 'Center margin' in Advanced toward 0, or confirm the avatar is in T-pose during the scan (the bind-pose math assumes near-bind orientation).");
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                if (_issues.Count == 0) {
                    EditorGUILayout.LabelField(
                        _scanSummary == "" ? "Pick an Animator, then click Scan." : "No issues found.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    // Pre-bucket counts once per draw — was O(n²) before.
                    var perRendererCount = new Dictionary<SkinnedMeshRenderer, int>();
                    foreach (var i in _issues) {
                        if (i.Renderer == null) continue;
                        perRendererCount.TryGetValue(i.Renderer, out var n);
                        perRendererCount[i.Renderer] = n + 1;
                    }
                    SkinnedMeshRenderer lastRenderer = null;
                    bool currentCollapsed = false;
                    int issueIndex = 0;
                    foreach (var i in _issues) {
                        int captured = issueIndex++;
                        if (i.Renderer != lastRenderer) {
                            int count = i.Renderer != null && perRendererCount.TryGetValue(i.Renderer, out var n) ? n : 0;
                            currentCollapsed = _collapsedRenderers.Contains(i.RendererPath);
                            using (new EditorGUILayout.HorizontalScope()) {
                                bool now = EditorGUILayout.Foldout(!currentCollapsed,
                                    new GUIContent($"{i.RendererPath}  —  {count} issue(s)" + (i.Renderer == null ? "  (renderer destroyed)" : ""),
                                        "Click to collapse all issues from this renderer. Useful when one mesh has hundreds of issues you've already triaged."),
                                    true, WkStyles.FoldoutHeader);
                                bool nowCollapsed = !now;
                                if (nowCollapsed != currentCollapsed) {
                                    if (nowCollapsed) _collapsedRenderers.Add(i.RendererPath);
                                    else _collapsedRenderers.Remove(i.RendererPath);
                                    currentCollapsed = nowCollapsed;
                                }
                                GUILayout.FlexibleSpace();
                            }
                            lastRenderer = i.Renderer;
                        }
                        if (!currentCollapsed) DrawIssueRowCompact(i, captured);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

#if false
        private void DrawPhysBoneIssueRow(PhysBoneClippingAnalyzer.Issue issue) {
            var severityColor = issue.Severity == PhysBoneClippingAnalyzer.Severity.High
                ? AvatarQolCategoryColors.Humanoid
                : WkStyles.ColorWarning;
            var severityText = issue.Severity == PhysBoneClippingAnalyzer.Severity.High ? "high" : "medium";
            string boneName = issue.DrivenBone != null ? issue.DrivenBone.name : "(destroyed)";
            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(6);
                WkStyles.BadgePill(severityText, severityColor,
                    issue.Severity == PhysBoneClippingAnalyzer.Severity.High
                        ? "No effective collider coverage or already-small clearance. This deserves attention."
                        : "Collider coverage exists or the estimated overlap is smaller, but the area is still worth checking.");
                EditorGUILayout.LabelField(
                    new GUIContent(
                        $"{issue.RendererPath}  v#{issue.VertexIndex}  {boneName}  move~{issue.EstimatedMotion * 100f:0.0}cm  clearance {issue.Clearance * 100f:0.0}cm",
                        issue.Reason),
                    WkStyles.Mono);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(issue.Renderer == null)) {
                    if (GUILayout.Button(new GUIContent("P", "Ping the renderer in the hierarchy."),
                            WkStyles.MiniRowButton, GUILayout.Width(22))) {
                        Selection.activeObject = issue.Renderer;
                        EditorGUIUtility.PingObject(issue.Renderer);
                    }
                }
                if (GUILayout.Button(new GUIContent("F", "Frame the risky vertex in the Scene view."),
                        WkStyles.MiniRowButton, GUILayout.Width(22))) {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null) {
                        sv.LookAt(issue.WorldPosition, sv.rotation, 0.18f);
                        sv.Repaint();
                    }
                }
                using (new EditorGUI.DisabledScope(issue.DrivenBone == null)) {
                    if (GUILayout.Button(new GUIContent("R", "Reveal the PhysBone-driven transform."),
                            WkStyles.MiniRowButton, GUILayout.Width(22))) {
                        Selection.activeObject = issue.DrivenBone;
                        EditorGUIUtility.PingObject(issue.DrivenBone);
                        FlashHighlight(issue.WorldPosition);
                    }
                    bool isPreviewing = _previewBone == issue.DrivenBone && issue.DrivenBone != null;
                    if (GUILayout.Button(new GUIContent(isPreviewing ? "Stop" : "Wobble",
                            "Temporarily wobble the driven transform so you can inspect likely clipping. This does not move the Scene camera."),
                            WkStyles.MiniRowButton, GUILayout.Width(58))) {
                        if (isPreviewing) StopPreview();
                        else StartPreview(issue.DrivenBone);
                    }
                }
            }
            EditorGUILayout.LabelField("   " + issue.Reason, WkStyles.Muted);
            EditorGUILayout.LabelField($"   nearest surface: {issue.NearestSurfacePath}", WkStyles.Muted);
        }

#endif
        private void DrawIssueRowCompact(DetectedIssue i, int issueIndex) {
            string boneName = i.OffendingBone != null ? i.OffendingBone.name : "(destroyed)";
            Color tag; string tagText; string tagTooltip;
            switch (i.Category) {
                case IssueCategory.HumanoidCrossSide:
                    tag = AvatarQolCategoryColors.Humanoid; tagText = "humanoid";
                    tagTooltip = "Bone has a Humanoid ancestor on the avatar's opposite side from the vertex. Highest-confidence flag.";
                    break;
                case IssueCategory.SpatialCrossSide:
                    tag = AvatarQolCategoryColors.Spatial; tagText = "spatial";
                    tagTooltip = "Bone has no Humanoid ancestor; its world pivot sits on the opposite side of the vertex.";
                    break;
                case IssueCategory.CenterBandSideBleed:
                    tag = AvatarQolCategoryColors.Center; tagText = "center";
                    tagTooltip = "Vertex is in the centre stripe; a Left or Right bone exceeded the higher centre threshold.";
                    break;
                default:
                    tag = WkStyles.ColorInfo; tagText = "?"; tagTooltip = ""; break;
            }
            bool expanded = _expandedIssueRows.Contains(issueIndex);

            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(6);
                // Foldout caret on the far left toggles the per-row details.
                bool now = EditorGUILayout.Foldout(expanded, GUIContent.none, true);
                if (now != expanded) {
                    if (now) _expandedIssueRows.Add(issueIndex);
                    else _expandedIssueRows.Remove(issueIndex);
                }
                WkStyles.BadgePill(tagText, tag, tagTooltip);
                EditorGUILayout.LabelField(
                    new GUIContent($"v#{i.VertexIndex}  {i.VertexSide} → {boneName} ({i.BoneSide})  w={i.Weight:F3}",
                        $"Vertex #{i.VertexIndex} on the avatar's {i.VertexSide} side has weight {i.Weight:F3} on {boneName}, which is classified {i.BoneSide}. Click ∨ for the world position; use the row buttons to investigate or fix."),
                    WkStyles.Mono);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(i.Renderer == null)) {
                    if (GUILayout.Button(new GUIContent("P", "Ping the renderer in the hierarchy."),
                            WkStyles.MiniRowButton, GUILayout.Width(22))) {
                        if (i.Renderer != null) {
                            Selection.activeObject = i.Renderer;
                            EditorGUIUtility.PingObject(i.Renderer);
                        }
                    }
                }
                if (GUILayout.Button(new GUIContent("F", "Frame: move Scene camera to the vertex."),
                        WkStyles.MiniRowButton, GUILayout.Width(22))) {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null) {
                        sv.LookAt(i.WorldPosition, sv.rotation, 0.15f);
                        sv.Repaint();
                    }
                }
                using (new EditorGUI.DisabledScope(i.OffendingBone == null)) {
                    if (GUILayout.Button(new GUIContent("R",
                            "Reveal: select the offending bone, frame the vertex, and flash a marker disc in the Scene view for two seconds."),
                            WkStyles.MiniRowButton, GUILayout.Width(22))) {
                        Selection.activeObject = i.OffendingBone;
                        EditorGUIUtility.PingObject(i.OffendingBone);
                        var sv = SceneView.lastActiveSceneView;
                        if (sv != null) { sv.LookAt(i.WorldPosition, sv.rotation, 0.15f); sv.Repaint(); }
                        FlashHighlight(i.WorldPosition);
                    }
                    bool isPreviewing = _previewBone == i.OffendingBone && i.OffendingBone != null;
                    if (GUILayout.Button(new GUIContent(isPreviewing ? "Stop" : "Wobble",
                            "Temporarily wobble the offending bone so you can see how the bad weights deform the mesh. This does not move the Scene camera."),
                            WkStyles.MiniRowButton, GUILayout.Width(58))) {
                        if (isPreviewing) StopPreview();
                        else if (i.OffendingBone != null) StartPreview(i.OffendingBone);
                    }
                }
                using (new EditorGUI.DisabledScope(i.Renderer == null || i.OffendingBone == null)) {
                    if (GUILayout.Button(new GUIContent("?",
                            "Why? Send this vertex to the Inspect Vertex panel and run a per-weight verdict — useful for understanding why a related weight didn't flag. Result prints to the Unity console."),
                            WkStyles.MiniRowButton, GUILayout.Width(22))) {
                        _inspectRenderer = i.Renderer;
                        _inspectVertexIndex = i.VertexIndex;
                        InspectVertex();
                    }
                    if (GUILayout.Button(new GUIContent("Fix",
                            "Redirect this offending weight to the bone's Humanoid mirror (e.g. RightUpperLeg → LeftUpperLeg). When no mirror is available, zero the weight and renormalise the rest. FBX-imported meshes are cloned to an editable .mesh in Assets/AvatarQol Generated/ before any change."),
                            WkStyles.MiniRowButton, GUILayout.Width(34))) {
                        var name = i.OffendingBone != null ? i.OffendingBone.name : "(destroyed)";
                        FixIssues(new List<DetectedIssue> { i }, $"weight on {name}");
                    }
                }
            }
            if (expanded) {
                EditorGUILayout.LabelField(
                    $"   world pos:  ({i.WorldPosition.x:F4}, {i.WorldPosition.y:F4}, {i.WorldPosition.z:F4})",
                    WkStyles.Muted);
                EditorGUILayout.LabelField(
                    $"   bone path:  {(i.OffendingBone != null ? PathUtility.GetGameObjectPath(i.OffendingBone.gameObject) : "(destroyed)")}",
                    WkStyles.Muted);
            }
        }

        // Scene-view fade-out marker. Stores a single hit; the gizmo
        // overlay polls _flashUntil and renders a disc until then.
        private Vector3 _flashPos;
        private double _flashUntil;

        private void FlashHighlight(Vector3 worldPos) {
            _flashPos = worldPos;
            _flashUntil = EditorApplication.timeSinceStartup + 2.0;
            SceneView.RepaintAll();
        }

        private void DrawNonReadableBanner() {
            if (_nonReadableRenderers.Count == 0) return;
            if (WkStyles.Notice(NoticeKind.Warning,
                    $"{_nonReadableRenderers.Count} renderer(s) skipped — mesh has Read/Write disabled in importer.",
                    "Enable Read/Write & rescan",
                    "For every skipped renderer, find its source asset, set Read/Write Enabled in the model importer, reimport, then re-run the scan.")) {
                EnableReadWriteOnSkippedAndRescan();
            }
        }

        private void DrawVertexInspector() {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                EditorGUILayout.LabelField(
                    new GUIContent("Inspect specific vertex",
                        "When an issue you expect isn't flagged, drop the renderer here and type the vertex index. The console gets a per-weight verdict explaining exactly which gate every weight passed or failed against the current thresholds."),
                    EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(
                        new GUIContent("Renderer",
                            "The SkinnedMeshRenderer whose vertex you want to inspect. Auto-fills when you change 'Limit scan to' or click 'From selection' below."),
                        GUILayout.Width(64));
                    _inspectRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                        new GUIContent(GUIContent.none.image, "Drop the SkinnedMeshRenderer to inspect."),
                        _inspectRenderer, typeof(SkinnedMeshRenderer), true);
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(
                        new GUIContent("Vertex #",
                            "The mesh vertex index to inspect. Find candidate indices via 'Dump weights for selection' below, or click 'Why?' on any flagged issue to auto-fill this field with that issue's vertex."),
                        GUILayout.Width(64));
                    _inspectVertexIndex = EditorGUILayout.IntField(_inspectVertexIndex);
                    using (new EditorGUI.DisabledScope(_inspectRenderer == null || _animator == null || !_animator.isHuman)) {
                        if (GUILayout.Button(
                                new GUIContent("Inspect",
                                    "Print the verdict for this vertex against current thresholds. Output goes to the Unity console."),
                                GUILayout.Width(80))) {
                            InspectVertex();
                        }
                    }
                    if (GUILayout.Button(
                            new GUIContent("From selection",
                                "Set the renderer to whatever's currently selected in the hierarchy."),
                            GUILayout.Width(110))) {
                        var go = Selection.activeGameObject;
                        if (go != null) _inspectRenderer = go.GetComponent<SkinnedMeshRenderer>();
                    }
                }
            }
        }

        // For each unique mesh referenced by the skipped renderers, find its
        // source asset's ModelImporter and flip Read/Write on. Reimport, then
        // re-run the scan automatically. We only touch ModelImporter assets —
        // procedurally-built or in-memory meshes (where there's no importer)
        // can't be fixed this way and are skipped with a warning.
        private void EnableReadWriteOnSkippedAndRescan() {
            var importersToReimport = new HashSet<string>();
            int unfixable = 0;
            foreach (var r in _nonReadableRenderers) {
                if (r == null || r.sharedMesh == null) continue;
                var path = AssetDatabase.GetAssetPath(r.sharedMesh);
                if (string.IsNullOrEmpty(path)) { unfixable++; continue; }
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null) { unfixable++; continue; }
                if (!importer.isReadable) {
                    importer.isReadable = true;
                    importer.SaveAndReimport();
                }
                importersToReimport.Add(path);
            }
            if (unfixable > 0) {
                AvatarQolLogger.Instance.Warning(
                    $"{unfixable} skipped mesh(es) had no ModelImporter " +
                    $"(procedurally generated, or imported by a different pipeline). " +
                    $"Read/Write couldn't be auto-enabled on those.");
            }
            if (importersToReimport.Count > 0) {
                AvatarQolLogger.Instance.Info($"Enabled Read/Write on {importersToReimport.Count} model asset(s); rescanning.");
            }
            Scan();
        }

        // Walks every weight on a single vertex and prints the verdict each
        // weight got against current thresholds. The most direct answer to
        // "why didn't this get flagged?".
        private void InspectVertex() {
            var smr = _inspectRenderer;
            if (smr == null) {
                EditorUtility.DisplayDialog("Inspect vertex", "Drop a SkinnedMeshRenderer first.", "OK");
                return;
            }
            if (_animator == null || !_animator.isHuman) {
                EditorUtility.DisplayDialog("Inspect vertex",
                    "Pick a Humanoid Animator at the top of the window first; we need it for side classification.", "OK");
                return;
            }
            var mesh = smr.sharedMesh;
            if (mesh == null || !mesh.isReadable) {
                EditorUtility.DisplayDialog("Inspect vertex",
                    "The renderer's mesh is null or not readable. Use 'Enable Read/Write & rescan' above if needed.", "OK");
                return;
            }
            if (_inspectVertexIndex < 0 || _inspectVertexIndex >= mesh.vertexCount) {
                EditorUtility.DisplayDialog("Inspect vertex",
                    $"Vertex index {_inspectVertexIndex} is out of range (mesh has {mesh.vertexCount} vertices).", "OK");
                return;
            }

            var sideMap = new HumanoidSideMap(_animator);
            var bones = smr.bones;
            var verts = mesh.vertices;
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var bindposes = mesh.bindposes;

            // Walk to the weight-cursor for the requested vertex.
            int cursor = 0;
            for (int v = 0; v < _inspectVertexIndex; v++) cursor += bonesPerVertex[v];
            int wCount = bonesPerVertex[_inspectVertexIndex];

            // Same bindpose-based world position as Scan: highest-weight
            // bone is the anchor. Falling back to renderer.transform is only
            // hit when the vertex has no usable weights (rare).
            int primaryIdx = -1;
            float primaryWeight = 0f;
            for (int w = 0; w < wCount; w++) {
                var bw = weights[cursor + w];
                if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                if (bones[bw.boneIndex] == null) continue;
                if (bw.weight > primaryWeight) { primaryWeight = bw.weight; primaryIdx = bw.boneIndex; }
            }
            Vector3 worldPos;
            string anchorDesc;
            if (primaryIdx >= 0 && bindposes != null && primaryIdx < bindposes.Length) {
                var meshLocal = verts[_inspectVertexIndex];
                var boneLocal = bindposes[primaryIdx].MultiplyPoint3x4(meshLocal);
                worldPos = bones[primaryIdx].TransformPoint(boneLocal);
                anchorDesc = $"bindpose anchor={bones[primaryIdx].name} (weight {primaryWeight:F3})";
            } else {
                worldPos = smr.transform.TransformPoint(verts[_inspectVertexIndex]);
                anchorDesc = "fallback=renderer.transform (no usable bone weight)";
            }
            var vertexSide = sideMap.ClassifyWorldPosition(worldPos, _centerMargin);
            bool isCenter = vertexSide == BoneSide.Center;
            float floor = isCenter ? _centerCrossSideFloor : _weightFloor;

            var sb = new StringBuilder();
            sb.AppendLine($"Inspect vertex #{_inspectVertexIndex} of {PathUtility.GetGameObjectPath(smr.gameObject)}");
            sb.AppendLine($"  world pos: ({worldPos.x:F4}, {worldPos.y:F4}, {worldPos.z:F4})  {anchorDesc}");
            sb.AppendLine($"  vertex side: {vertexSide} (isCenter={isCenter}, applicable floor={floor:F4})");
            sb.AppendLine($"  weights ({wCount}):");
            for (int w = 0; w < wCount; w++) {
                var bw = weights[cursor + w];
                Transform bone = bw.boneIndex >= 0 && bw.boneIndex < bones.Length ? bones[bw.boneIndex] : null;
                string boneName = bone != null ? bone.name : $"(invalid index {bw.boneIndex})";
                BoneSide humanoidSide = bone != null ? sideMap.GetSide(bone) : BoneSide.Unknown;
                BoneSide spatialSide = bone != null ? sideMap.ClassifyWorldPosition(bone.position, _centerMargin) : BoneSide.Unknown;
                BoneSide effectiveSide = humanoidSide != BoneSide.Unknown ? humanoidSide : spatialSide;
                string verdict;
                if (bone == null) {
                    verdict = "SKIPPED (invalid bone index)";
                } else if (bw.weight < floor) {
                    verdict = $"SKIPPED (weight {bw.weight:F4} < floor {floor:F4})";
                } else if (effectiveSide == BoneSide.Unknown) {
                    verdict = "SKIPPED (bone has no Humanoid ancestor and pivot is in centre band — Unknown side)";
                } else if (effectiveSide == BoneSide.Center) {
                    verdict = "SKIPPED (bone classified Center — same as central avatar mass)";
                } else if (!isCenter && effectiveSide == vertexSide) {
                    verdict = "SKIPPED (bone same side as vertex)";
                } else {
                    string cat = isCenter ? "center-band" : (humanoidSide != BoneSide.Unknown ? "humanoid" : "spatial");
                    verdict = $"FLAGGED [{cat}]  vertex={vertexSide} bone={effectiveSide}";
                }
                sb.AppendLine($"    {boneName}  weight={bw.weight:F4}  humanoid={humanoidSide}  spatial={spatialSide}  →  {verdict}");
            }
            AvatarQolLogger.Instance.Info(sb.ToString());
            _showConsoleNoticeAfterInspect = true;
        }

        private static void DrawDivider() {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.18f));
        }

        // ------ Scan -------------------------------------------------------

        private void Scan() {
            _issues.Clear();
            _nonReadableRenderers.Clear();
            _scanSummary = "";
            _lastScanLeftRightVerts = 0;
            _lastScanCenterVerts = 0;
            if (_animator == null || !_animator.isHuman) return;

            var sideMap = new HumanoidSideMap(_animator);
            if (!sideMap.IsValid) {
                _scanSummary = "Animator has no usable Humanoid bindings (Hips missing).";
                return;
            }

            // If the user dropped a renderer into "Limit scan to", honour that
            // and skip everything else under the avatar. Otherwise walk every
            // SkinnedMeshRenderer in the hierarchy.
            SkinnedMeshRenderer[] renderers;
            if (_limitToRenderer != null) {
                renderers = new[] { _limitToRenderer };
            } else {
                renderers = _animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            }
            int verticesScanned = 0;
            int renderersScanned = 0;
            var globalLog = _verboseLog ? new StringBuilder() : null;
            globalLog?.AppendLine($"Weight Sanity Check verbose log");
            globalLog?.AppendLine($"  weightFloor={_weightFloor:F4}, centerMargin={_centerMargin:F3}, scanCenterBand={_scanCenterBand}, centerCrossSideFloor={_centerCrossSideFloor:F3}");
            globalLog?.AppendLine($"  avatar={_animator.gameObject.name}, leftSign={sideMap.LeftSignInHipsLocal}");
            if (_limitToRenderer != null) {
                globalLog?.AppendLine($"  filter: limit-to-renderer={PathUtility.GetGameObjectPath(_limitToRenderer.gameObject)}");
            }

            var p = new ScanParameters {
                WeightFloor = _weightFloor,
                CenterMargin = _centerMargin,
                ScanCenterBand = _scanCenterBand,
                CenterCrossSideFloor = _centerCrossSideFloor,
            };

            foreach (var r in renderers) {
                if (r == null || r.sharedMesh == null) continue;
                if (_excludedRenderers.Contains(r)) {
                    globalLog?.AppendLine($"  SKIP renderer (excluded): {PathUtility.GetGameObjectPath(r.gameObject)}");
                    continue;
                }
                var detect = WeightCrossSideDetector.Detect(r, sideMap, p, globalLog);
                verticesScanned += detect.VerticesScanned;
                renderersScanned++;
                if (detect.MeshUnreadable) _nonReadableRenderers.Add(r);
                _issues.AddRange(detect.Issues);
                _lastScanLeftRightVerts += detect.LeftVerts + detect.RightVerts;
                _lastScanCenterVerts += detect.CenterVerts;
            }

            _issues.Sort((a, b) => {
                // RendererPath is cached at scan time, so this survives a
                // renderer being destroyed mid-comparison.
                int rcmp = string.Compare(a.RendererPath, b.RendererPath, System.StringComparison.Ordinal);
                if (rcmp != 0) return rcmp;
                return a.VertexIndex.CompareTo(b.VertexIndex);
            });

            _scanSummary = $"Scanned {verticesScanned} vertices across {renderersScanned} renderer(s); flagged {_issues.Count}.";
            if (globalLog != null) {
                globalLog.AppendLine();
                globalLog.AppendLine($"  total issues flagged: {_issues.Count}");
                AvatarQolLogger.Instance.Info(globalLog.ToString());
            }
            SceneView.RepaintAll();
        }

        // ScanRenderer was moved into WeightFixes/WeightCrossSideDetector
        // (Detect) so the runtime apply hook shares the exact same
        // detection path used by this window. Scan() above now calls into
        // the detector once per renderer.

        // ------ Debug dump for a single renderer ---------------------------

        private void DumpSelectedRendererWeights() {
            var go = Selection.activeGameObject;
            if (go == null) {
                EditorUtility.DisplayDialog("Dump weights", "Select a SkinnedMeshRenderer in the hierarchy first.", "OK");
                return;
            }
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null) {
                EditorUtility.DisplayDialog("Dump weights",
                    $"'{go.name}' has no SkinnedMeshRenderer. Select the actual renderer GameObject.", "OK");
                return;
            }
            if (_animator == null || !_animator.isHuman) {
                EditorUtility.DisplayDialog("Dump weights",
                    "Pick a Humanoid Animator at the top of the window first; we need it for side classification.", "OK");
                return;
            }
            var sideMap = new HumanoidSideMap(_animator);
            var mesh = smr.sharedMesh;
            if (mesh == null || !mesh.isReadable) {
                EditorUtility.DisplayDialog("Dump weights",
                    "The renderer's mesh is null or not readable. Enable Read/Write in the model importer if needed.", "OK");
                return;
            }

            var bones = smr.bones;
            var verts = mesh.vertices;
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var sb = new StringBuilder();
            sb.AppendLine($"Weight dump for {PathUtility.GetGameObjectPath(smr.gameObject)}");
            sb.AppendLine($"  vertices={mesh.vertexCount}, bones={bones.Length}");

            // First, list every bone with its classification — handy when
            // tracking down "why didn't a custom bone get flagged?"
            sb.AppendLine("  bones:");
            for (int b = 0; b < bones.Length; b++) {
                if (bones[b] == null) { sb.AppendLine($"    [{b}] (null)"); continue; }
                var humanoid = sideMap.GetSide(bones[b]);
                var spatial  = sideMap.ClassifyWorldPosition(bones[b].position, _centerMargin);
                sb.AppendLine($"    [{b}] {bones[b].name}  humanoid={humanoid}  spatial={spatial}");
            }

            // Then dump every vertex with its top weights. Limited to the
            // first 200 vertices per renderer to keep the log readable —
            // bump if needed for specific debugging.
            int limit = Mathf.Min(mesh.vertexCount, 200);
            sb.AppendLine($"  first {limit} vertices:");
            int cursor = 0;
            // Same bindpose-based world position as Scan: pick the highest-
            // weight bone, transform mesh-local → bone-local via bindpose,
            // bone-local → world via the bone's current transform.
            var bindposes = mesh.bindposes;
            for (int v = 0; v < mesh.vertexCount; v++) {
                int wCount = bonesPerVertex[v];
                if (v < limit) {
                    int primaryIdx = -1;
                    float primaryWeight = 0f;
                    for (int w = 0; w < wCount; w++) {
                        var bw = weights[cursor + w];
                        if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                        if (bones[bw.boneIndex] == null) continue;
                        if (bw.weight > primaryWeight) { primaryWeight = bw.weight; primaryIdx = bw.boneIndex; }
                    }
                    Vector3 worldPos;
                    if (primaryIdx >= 0 && bindposes != null && primaryIdx < bindposes.Length) {
                        var boneLocal = bindposes[primaryIdx].MultiplyPoint3x4(verts[v]);
                        worldPos = bones[primaryIdx].TransformPoint(boneLocal);
                    } else {
                        worldPos = smr.transform.TransformPoint(verts[v]);
                    }
                    var side = sideMap.ClassifyWorldPosition(worldPos, _centerMargin);
                    sb.Append($"    v#{v} on {side} ({worldPos.x:F3},{worldPos.y:F3},{worldPos.z:F3}): ");
                    for (int w = 0; w < wCount; w++) {
                        var bw = weights[cursor + w];
                        var name = bw.boneIndex < bones.Length && bones[bw.boneIndex] != null
                            ? bones[bw.boneIndex].name : "?";
                        sb.Append($"{name}={bw.weight:F3} ");
                    }
                    sb.AppendLine();
                }
                cursor += wCount;
            }
            AvatarQolLogger.Instance.Info(sb.ToString());
            _showConsoleNoticeAfterDump = true;
        }

        // ------ Preview ----------------------------------------------------

        private void StartPreview(Transform bone) {
            if (bone == null) return;
            if (_previewBone == bone) return;
            // Restore any prior preview before starting a new one.
            StopPreview();
            _previewBone = bone;
            _previewRestRotation = bone.localRotation;
            _previewStart = EditorApplication.timeSinceStartup;
            // Mark the bone undo-recorded so accidental scene save doesn't
            // immortalise the wobbled rotation. We also restore on stop.
            Undo.RegisterCompleteObjectUndo(bone, "Avatar QoL preview");
        }

        private void StopPreview() {
            // The Unity-fake-null check is the right read here: if the bone
            // was destroyed, we can't restore it, but we should still clear
            // our reference so we don't keep wobbling against a dead object
            // every editor update.
            if (_previewBone == null) { _previewBone = null; return; }
            _previewBone.localRotation = _previewRestRotation;
            _previewBone = null;
            SceneView.RepaintAll();
        }

        private void OnEditorUpdate() {
            if (_previewBone == null) {
                // Bone may have been destroyed — drop our reference so the
                // next OnGUI doesn't try to draw a Stop button against it.
                _previewBone = null;
                return;
            }
            // Wobble around the bone's primary swing axes. Most rigs deform
            // legibly when rotated around their local X (forward bend) and
            // local Z (side splay). We combine the two so the mesh moves
            // visibly even when the rig's primary axis happens to be one or
            // the other.
            float t = (float)(EditorApplication.timeSinceStartup - _previewStart);
            float angle = Mathf.Sin(t * Mathf.PI) * 30f;
            float zAngle = Mathf.Cos(t * Mathf.PI) * 18f;
            _previewBone.localRotation = _previewRestRotation * Quaternion.Euler(angle, 0f, zAngle);
            SceneView.RepaintAll();
        }

        // ------ Preview on a clone (nondestructive) ----------------------

        private void StartPreview(List<DetectedIssue> issues) {
            if (_animator == null || issues == null || issues.Count == 0) return;
            var animator = _animator;
            // Group by renderer; remap each renderer to the preview clone via
            // hierarchy path so the in-memory fix lands on the clone instead
            // of the source.
            var byRenderer = new Dictionary<SkinnedMeshRenderer, List<DetectedIssue>>();
            foreach (var i in issues) {
                if (i == null || i.Renderer == null) continue;
                if (!byRenderer.TryGetValue(i.Renderer, out var list)) {
                    list = new List<DetectedIssue>();
                    byRenderer[i.Renderer] = list;
                }
                list.Add(i);
            }
            if (byRenderer.Count == 0) return;

            AvatarPreviewController.StartPreview(animator.gameObject, (cloneRoot, session) => {
                var cloneAnimator = cloneRoot.GetComponentInChildren<Animator>(true);
                if (cloneAnimator == null) return;
                foreach (var kv in byRenderer) {
                    var sourceRenderer = kv.Key;
                    var cloneRenderer = AvatarPreviewController.MapToPreview(sourceRenderer.transform)?.GetComponent<SkinnedMeshRenderer>();
                    if (cloneRenderer == null || cloneRenderer.sharedMesh == null) continue;
                    session.Capture(cloneRenderer);
                    var clone = UnityEngine.Object.Instantiate(cloneRenderer.sharedMesh);
                    clone.name = cloneRenderer.sharedMesh.name + " (WeightFix Preview)";
                    clone.hideFlags = HideFlags.DontSave;
                    session.Adopt(clone);
                    var refs = new List<WeightFixer.IssueRef>(kv.Value.Count);
                    foreach (var issue in kv.Value) {
                        var cloneBone = AvatarPreviewController.MapToPreview(issue.OffendingBone);
                        if (cloneBone == null) continue;
                        refs.Add(new WeightFixer.IssueRef {
                            Renderer = cloneRenderer,
                            VertexIndex = issue.VertexIndex,
                            OffendingBone = cloneBone,
                            Weight = issue.Weight,
                        });
                    }
                    var fixResult = new WeightFixer.FixResult();
                    WeightFixer.ApplyFixesToMeshInPlace(clone, cloneRenderer.bones, refs, cloneAnimator, fixResult);
                    cloneRenderer.sharedMesh = clone;
                }
            });
        }

        // ------ Save as component (nondestructive) ------------------------

        private void SaveIssuesAsComponents() {
            if (_issues.Count == 0) return;

            // Bucket issues by their renderer so each renderer gets one
            // component, parameterised once. We don't store the issue list
            // on the component -- the runtime hook re-scans the renderer's
            // CURRENT mesh at apply time, which is what makes the flow
            // robust to Blender re-imports.
            var renderers = new HashSet<SkinnedMeshRenderer>();
            foreach (var i in _issues) if (i.Renderer != null) renderers.Add(i.Renderer);
            if (renderers.Count == 0) {
                EditorUtility.DisplayDialog("Save fixes as component",
                    "All issues in the list reference destroyed renderers; nothing to save.", "OK");
                return;
            }

            string msg = $"Add (or update) a WhyKnotWeightFixIntent component on " +
                         $"{renderers.Count} renderer(s)?\n\n" +
                         "At play-mode entry and at avatar upload, each intent re-scans its " +
                         "renderer's current mesh and applies cross-side fixes to an in-memory " +
                         "clone. The source mesh asset is never modified.\n\n" +
                         "The component stores your current scan parameters (weight floor, " +
                         "centre margin, scan centre band, centre threshold) so play / build " +
                         "produces the same set of fixes you see in this list right now.";
            if (!EditorUtility.DisplayDialog("Save fixes as component", msg,
                    $"Add to {renderers.Count} renderer(s)", "Cancel")) return;

            int created = 0;
            int updated = 0;
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: Save weight fixes as component");
            foreach (var renderer in renderers) {
                if (renderer == null) continue;
                var existing = renderer.GetComponent<WhyKnotWeightFixIntent>();
                if (existing == null) {
                    existing = Undo.AddComponent<WhyKnotWeightFixIntent>(renderer.gameObject);
                    created++;
                } else {
                    Undo.RecordObject(existing, "Update WeightFix intent");
                    updated++;
                }
                existing.targetRenderer       = renderer;
                existing.weightFloor          = _weightFloor;
                existing.centerMargin         = _centerMargin;
                existing.scanCenterBand       = _scanCenterBand;
                existing.centerCrossSideFloor = _centerCrossSideFloor;
                EditorUtility.SetDirty(existing);
            }
            Undo.CollapseUndoOperations(undoGroup);

            AvatarQolLogger.Instance.Info(
                $"WeightFix intent saved: {created} created, {updated} updated, " +
                $"across {renderers.Count} renderer(s). " +
                $"Fixes will apply at play mode and at upload.");
        }

        // ------ Fix --------------------------------------------------------

        private void FixIssues(List<DetectedIssue> issues, string description) {
            if (issues == null || issues.Count == 0) return;
            // Single confirmation up-front; the apply runs in a single Undo
            // group so Ctrl+Z reverts everything in one step.
            var rendererCount = new HashSet<SkinnedMeshRenderer>();
            foreach (var i in issues) if (i.Renderer != null) rendererCount.Add(i.Renderer);
            string msg = $"Fix {description} across {rendererCount.Count} renderer(s)?\n\n" +
                         "Each offending weight will be redirected to its Humanoid mirror bone " +
                         "(e.g. RightUpperLeg -> LeftUpperLeg). Weights with no mirror are zeroed " +
                         "and the remaining weights on the same vertex are scaled up.\n\n" +
                         "FBX-imported meshes will be cloned to editable .mesh assets in " +
                         $"{WeightFixer.GeneratedFolder}/ - the original FBX is never modified.\n\n" +
                         "Prefer the nondestructive component flow ('Save fixes as component' " +
                         "below the issue list) when you expect to re-import the model from " +
                         "Blender; the destructive path here writes into the clone .mesh and " +
                         "the reference is lost if the FBX subasset is regenerated.\n\n" +
                         "Ctrl+Z reverts the operation.";
            if (!EditorUtility.DisplayDialog("Fix weight contamination", msg, "Fix", "Cancel")) return;

            // Translate detector Issue -> fixer IssueRef so the fixer
            // module stays free of detection-side types.
            var refs = new List<WeightFixer.IssueRef>(issues.Count);
            foreach (var i in issues) {
                refs.Add(new WeightFixer.IssueRef {
                    Renderer       = i.Renderer,
                    VertexIndex    = i.VertexIndex,
                    OffendingBone  = i.OffendingBone,
                    Weight         = i.Weight,
                });
            }
            var result = WeightFixer.ApplyFixes(refs, _animator);

            // Drop the just-fixed issues from the visible list and refresh
            // the gizmo overlay. We don't auto-rescan: the user usually
            // wants to compare before/after themselves, and Ctrl+Z is more
            // useful when the issue list still shows what was done.
            var fixedSet = new HashSet<DetectedIssue>(issues);
            _issues.RemoveAll(fixedSet.Contains);
            SceneView.RepaintAll();

            string clonedNote = result.MeshesCloned > 0
                ? $" Cloned {result.MeshesCloned} mesh(es) to {WeightFixer.GeneratedFolder}/."
                : "";
            string skipNote = result.Skipped > 0
                ? $" Skipped {result.Skipped} (weight no longer present)."
                : "";
            AvatarQolLogger.Instance.Info(
                $"Weight fix: {result.Fixed} weight(s) corrected — " +
                $"{result.Mirrored} mirrored, {result.Zeroed} zeroed + renormalised, " +
                $"across {result.RenderersTouched} renderer(s).{clonedNote}{skipNote}");
            AssetDatabase.SaveAssets();
        }

        // ------ Scene view gizmos -----------------------------------------

        private void OnSceneGui(SceneView sceneView) {
            // Reveal flash — fades out by alpha over the 2s window.
            if (_flashUntil > EditorApplication.timeSinceStartup) {
                float remaining = (float)(_flashUntil - EditorApplication.timeSinceStartup) / 2.0f;
                var prev = Handles.color;
                Handles.color = new Color(1f, 0.85f, 0.20f, Mathf.Clamp01(remaining));
                var size = HandleUtility.GetHandleSize(_flashPos) * 0.18f;
                Handles.DrawWireDisc(_flashPos, sceneView.camera.transform.forward, size);
                Handles.DrawWireDisc(_flashPos, sceneView.camera.transform.forward, size * 0.6f);
                Handles.color = prev;
                sceneView.Repaint();
            }
            if (!_showGizmos || _issues.Count == 0) return;
            var prevColor = Handles.color;
            Handles.color = new Color(1f, 0.25f, 0.25f, 0.95f);
            foreach (var i in _issues) {
                if (i.Renderer == null) continue;
                var size = HandleUtility.GetHandleSize(i.WorldPosition) * 0.04f;
                Handles.SphereHandleCap(0, i.WorldPosition, Quaternion.identity, size, EventType.Repaint);
            }
            Handles.color = prevColor;
        }

        // Issue record + IssueCategory enum moved to
        // WhyKnot.AvatarQol.WeightFixes (DetectedIssue.cs) so the runtime
        // apply hook can speak the same shape without referencing this
        // editor window.
    }
}
