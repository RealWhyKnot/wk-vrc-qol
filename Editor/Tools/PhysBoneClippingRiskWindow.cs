// PhysBoneClippingRiskWindow.cs
//
// Standalone UI for the conservative PhysBone clipping-risk estimate. It is
// scoped to one SkinnedMeshRenderer at a time so the regular Weight Sanity
// Check stays fast and this heavier scan is explicit.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class PhysBoneClippingRiskWindow : EditorWindow {

        [SerializeField] private Animator _animator;
        [SerializeField] private SkinnedMeshRenderer _targetRenderer;
        [SerializeField] private List<SkinnedMeshRenderer> _comparisonRenderers =
            new List<SkinnedMeshRenderer>();
        [SerializeField] private bool _showGizmos = true;
        [SerializeField] private bool _verboseLog;
        [SerializeField] private float _weightFloor = 0.03f;
        [SerializeField] private float _clearanceMargin = 0.025f;
        [SerializeField] private int _maxIssuesPerPhysBone = 8;

        private const string WikiUrl = "https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview#physbone-clipping-risks";

        private readonly List<PhysBoneClippingAnalyzer.Issue> _issues =
            new List<PhysBoneClippingAnalyzer.Issue>();
        private Vector2 _scroll;
        private string _scanSummary = "";
        private int _lastSurfaceRendererCount;

        private Transform _previewBone;
        private Quaternion _previewRestRotation;
        private double _previewStart;

        private Vector3 _flashPos;
        private double _flashUntil;

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<PhysBoneClippingRiskWindow>(false, "PhysBone Clipping Risks", true);
            w.titleContent = new GUIContent("Avatar QoL - PhysBone Clipping Risks");
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
            DrawTitleBar();
            WkStyles.Notice(NoticeKind.Info,
                "This is a heavier physics-risk scan, so it checks one mesh at a time. Pick the mesh that actually moves from PhysBones, then scan.");
            DrawSetup();
            EditorGUILayout.Space(2);
            DrawTuning();
            EditorGUILayout.Space(2);
            DrawScanBar();
            EditorGUILayout.Space(2);
            DrawIssues();
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("PhysBone Clipping Risks",
                        "Estimate whether one PhysBone-driven mesh has enough motion range to clip into itself or nearby surfaces."),
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
