// ClippingFixerWindow.cs
//
// Standalone UI for the mesh clipping scan and mesh fixer. It is
// scoped to one SkinnedMeshRenderer at a time so the regular Weight Sanity
// Check stays fast and this heavier scan is explicit.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Clipping;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class ClippingFixerWindow : EditorWindow {

        [SerializeField] private Animator _animator;
        [SerializeField] private SkinnedMeshRenderer _targetRenderer;
        [SerializeField] private List<SkinnedMeshRenderer> _comparisonRenderers =
            new List<SkinnedMeshRenderer>();
        [SerializeField] private bool _showGizmos = true;
        [SerializeField] private bool _verboseLog;
        [SerializeField] private bool _checkSelf = true;
        [SerializeField] private bool _includePhysBoneMotion = true;
        [SerializeField] private float _insideTolerance = 0.001f;
        [SerializeField] private float _surfacePadding = 0.005f;
        [SerializeField] private float _physBoneWeightFloor = 0.03f;
        [SerializeField] private float _physBoneClearanceMargin = 0.025f;
        [SerializeField] private int _maxFixPasses = 4;
        [SerializeField] private int _maxWarnings = 250;
        [SerializeField] private int _maxIssuesPerPhysBone = 8;

        private const string WikiUrl = "https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview#physbone-clipping-fixer";

        private readonly List<ClippingFixer.Issue> _issues =
            new List<ClippingFixer.Issue>();
        private readonly HashSet<int> _selectedIssueIndices = new HashSet<int>();
        private Vector2 _pageScroll;
        private string _scanSummary = "";
        private int _lastSurfaceRendererCount;

        private Transform _previewBone;
        private Quaternion _previewRestRotation;
        private double _previewStart;

        private Vector3 _flashPos;
        private double _flashUntil;

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<ClippingFixerWindow>(false, "Clipping Fixer", true);
            w.titleContent = WkStyles.TitleContent("Avatar QoL - Clipping Fixer");
            w.minSize = new Vector2(620, 460);
            if (prefillFromSelection) w.PrefillFromSelection();
            w.Show();
            w.Focus();
        }

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

        private void OnGUI() {
            using var _wkTheme = WkStyles.Scope(WkTheme.WhyKnot);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true))) {
                DrawTitleBar();
                WkStyles.AnimatedAccentLine();

                using (var s = new EditorGUILayout.ScrollViewScope(
                        _pageScroll, false, false,
                        GUILayout.ExpandWidth(true),
                        GUILayout.ExpandHeight(true))) {
                    _pageScroll = s.scrollPosition;
                    WkStyles.Notice(NoticeKind.Info,
                        "Pick the mesh to fix, add the body or other meshes it should stay outside, then scan and apply a component or generated mesh fix.");
                    DrawSetup();
                    EditorGUILayout.Space(2);
                    DrawTuning();
                    EditorGUILayout.Space(2);
                    DrawScanBar();
                    EditorGUILayout.Space(2);
                    DrawIssues();
                }

                WkStyles.WindowFooter();
            }
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Clipping Fixer",
                        "Find mesh intersections and PhysBone motion risks between one mesh, itself, and nearby body/comparison meshes, then generate a build-time or destructive mesh fix."),
                    WkStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

    }
}
