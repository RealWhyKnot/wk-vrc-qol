// PhysBonePresetWindow.cs
//
// Three-panel window:
//   1. Bone selection (drop zone + list).
//   2. Preset picker - preset cards. The auto-suggested preset has a
//      ribbon and accent border; the selected preset gets a fill tint.
//      Each card shows the preset name, suggestion strength, description,
//      and a tooltip on the score bar that explains *why* the preset
//      matched.
//   3. Plan preview - chain-grouped foldouts. Each PhysBone is rendered
//      with a parameter table; values that differ from the VRChat SDK
//      defaults are bold-labelled and marked with `**` for quick review.
//
// After Apply, the bottom of the window briefly hosts a tweak strip:
// multiplicative sliders for Pull / Spring / Stiffness / Gravity / Radius
// that scale every PhysBone the apply just created. Disappears when the
// selection or preset changes; Ctrl+Z reverts the apply itself.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

#if VRC_SDK_VRCSDK3
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class PhysBonePresetWindow : EditorWindow {

        [SerializeField] private List<Transform> _selection = new List<Transform>();
        [SerializeField] private string _selectedPresetId;
        [SerializeField] private bool _advancedOpen;

        // Per-chain foldout state, stable across reloads via a parallel list of chain-root paths.
        [SerializeField] private List<string> _collapsedChainRoots = new List<string>();

        private List<IPhysBonePreset> _presets = new List<IPhysBonePreset>();
        private BoneSelectionAnalysis _analysis;
        private PhysBonePlan _plan;
        private Dictionary<string, float> _suggestionScores = new Dictionary<string, float>();
        private Dictionary<string, List<ScoringSignal>> _suggestionExplanations = new Dictionary<string, List<ScoringSignal>>();
        private Vector2 _selectionScroll;
        private Vector2 _planScroll;
        private Vector2 _pageScroll;
        private string _autoSizeSignature;

        // Post-apply tweak state. After Apply, we cache the just-created
        // components and a snapshot of their original parameters so the
        // tweak sliders can scale relative to a stable baseline (dragging
        // back to 1.0 fully restores).
        private List<TweakSnapshot> _tweakSnapshots;
        private float _tweakPull = 1f, _tweakSpring = 1f, _tweakStiff = 1f, _tweakGravity = 1f, _tweakRadius = 1f;

        private const string WikiUrl = "https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview#physbone-preset";

        // ------ Public entry -----------------------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<PhysBonePresetWindow>(false, "PhysBone Preset", true);
            w.titleContent = WkStyles.TitleContent("Avatar QoL - PhysBone Preset");
            w.minSize = new Vector2(640, 580);
            if (prefillFromSelection) {
                w._selection = Selection.gameObjects
                    .Where(g => g != null)
                    .Select(g => g.transform)
                    .Distinct()
                    .ToList();
            }
            w.RebuildAnalysis();
            w.Show();
            w.Focus();
        }

        // ------ Lifecycle --------------------------------------------------

        private void OnEnable() {
            DiscoverPresets();
            if (_analysis == null) RebuildAnalysis();
        }

        private void DiscoverPresets() {
            _presets = new List<IPhysBonePreset>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types) {
                    if (t.IsAbstract || t.IsInterface) continue;
                    if (!typeof(IPhysBonePreset).IsAssignableFrom(t)) continue;
                    var ctor = t.GetConstructor(Type.EmptyTypes);
                    if (ctor == null) continue;
                    try { _presets.Add((IPhysBonePreset)ctor.Invoke(null)); }
                    catch { /* skip: constructor threw */ }
                }
            }
            _presets.Sort((a, b) => {
                if (a.Id == "generic") return 1;
                if (b.Id == "generic") return -1;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
            });
        }

        // ------ GUI --------------------------------------------------------

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
                    DrawSdkBanner();
                    WkStyles.Notice(NoticeKind.Info,
                        "Flow: choose the bones, pick the suggested preset or another card, review the plan, then apply.");
                    DrawSelection();
                    EditorGUILayout.Space(2);
                    DrawAnalysisSummary();
                    EditorGUILayout.Space(2);
                    DrawPresetPicker();
                    EditorGUILayout.Space(2);
                    DrawPlanPreview();
                    EditorGUILayout.Space(2);
                    if (_tweakSnapshots != null && _tweakSnapshots.Count > 0) {
                        DrawTweakStrip();
                    } else {
                        DrawApplyBar();
                    }
                    DrawAdvanced();
                }

                WkStyles.WindowFooter();
            }
            RequestAutoSize();
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("PhysBone Preset",
                        "Set up VRChat PhysBones on selected bones using plain presets such as ears, tail, hair, or dress."),
                    WkStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawSdkBanner() {
            if (PhysBonePlanApplier.SdkAvailable) return;
            WkStyles.Notice(NoticeKind.Warning,
                "VRChat SDK 3 (PhysBone) is not installed in this project. The window will analyse selections and preview plans, but Apply is disabled.");
        }

        private void RequestAutoSize() {
            var planPhysBones = _plan != null && _plan.PhysBones != null ? _plan.PhysBones.Count : 0;
            var planColliders = _plan != null && _plan.Colliders != null ? _plan.Colliders.Count : 0;
            var tweakCount = _tweakSnapshots != null ? _tweakSnapshots.Count : 0;
            var signature = $"{_selection.Count}|{_presets.Count}|{_selectedPresetId}|{planPhysBones}|{planColliders}|{_advancedOpen}|{tweakCount}";
            var preferred = new Vector2(
                760f,
                500f
                    + WkStyles.CappedListHeight(_selection.Count, 22f, 70f, 150f)
                    + WkStyles.CappedListHeight(planPhysBones + planColliders, 20f, 120f, 280f)
                    + (_advancedOpen ? 80f : 0f));
            WkStyles.AutoSizeWindow(
                this,
                ref _autoSizeSignature,
                signature,
                new Vector2(640f, 580f),
                preferred,
                new Vector2(980f, 800f));
        }




    }
}
