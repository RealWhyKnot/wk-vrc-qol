// PhysBonePresetWindow.Actions.cs

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

    internal sealed partial class PhysBonePresetWindow {

        // -------- Apply / tweak / advanced --------

        private void DrawApplyBar() {
            bool canApply = PhysBonePlanApplier.SdkAvailable
                            && _plan != null
                            && _plan.PhysBones.Count > 0;
            var avatarRoot = _analysis?.HostAnimator != null ? _analysis.HostAnimator.gameObject : null;
            bool isPreviewing = AvatarPreviewController.IsPreviewing
                && AvatarPreviewController.SourceAvatar == avatarRoot;

            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!canApply)) {
                    string label = canApply
                        ? $"4. Apply plan ({_plan.PhysBones.Count} PhysBone(s), {_plan.Colliders.Count} collider(s))"
                        : "4. Apply plan";
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent(label,
                                "Destructive. Create the listed components on the listed bones in one Undo group. Ctrl+Z reverts."),
                            GUILayout.MinWidth(260))) {
                        ApplyPlan();
                    }
                }
                using (new EditorGUI.DisabledScope(!canApply || avatarRoot == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Add as Intent",
                                "Non-destructive. Save the bone list + preset choice + tweak values to a WhyKnotPhysBonePresetIntent on the avatar root. The preset then re-spawns at play and at upload, in memory only."),
                            GUILayout.Height(28), GUILayout.Width(118))) {
                        AddAsIntent(avatarRoot);
                    }
                }
                using (new EditorGUI.DisabledScope(!canApply || avatarRoot == null || isPreviewing)) {
                    if (GUILayout.Button(
                            new GUIContent("Preview",
                                "Non-destructive. Clone the avatar in place and spawn the preset on the clone so you can verify behaviour without committing components."),
                            GUILayout.Height(28), GUILayout.Width(92))) {
                        StartPreview(avatarRoot);
                    }
                }
                using (new EditorGUI.DisabledScope(!isPreviewing)) {
                    if (GUILayout.Button(
                            new GUIContent("Stop preview", "Destroy the preview clone and un-hide the source avatar."),
                            GUILayout.Height(28), GUILayout.Width(110))) {
                        AvatarPreviewController.StopPreview();
                    }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Close", "Close this window. Plan is discarded; nothing is written."),
                        GUILayout.Height(28), GUILayout.Width(80))) Close();
            }
        }

        private void AddAsIntent(GameObject avatarRoot) {
            if (avatarRoot == null || _plan == null || string.IsNullOrEmpty(_selectedPresetId)) return;
            var liveBones = _selection.Where(b => b != null).ToList();
            if (liveBones.Count == 0) return;

            Undo.SetCurrentGroupName("Avatar QoL: add PhysBonePreset intent");
            int group = Undo.GetCurrentGroup();

            var intent = avatarRoot.GetComponent<WhyKnotPhysBonePresetIntent>();
            if (intent == null) {
                intent = Undo.AddComponent<WhyKnotPhysBonePresetIntent>(avatarRoot);
            } else {
                Undo.RecordObject(intent, "Update PhysBonePreset intent");
            }
            intent.bones = new List<Transform>(liveBones);
            intent.presetId = _selectedPresetId;
            intent.tweakPull = _tweakPull;
            intent.tweakSpring = _tweakSpring;
            intent.tweakStiff = _tweakStiff;
            intent.tweakGravity = _tweakGravity;
            intent.tweakRadius = _tweakRadius;
            PhysBonePresetApplier.TryRefreshPrecompute(intent, out _);
            EditorUtility.SetDirty(intent);

            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = avatarRoot;
            EditorGUIUtility.PingObject(intent);
        }

        private void StartPreview(GameObject avatarRoot) {
            if (avatarRoot == null || string.IsNullOrEmpty(_selectedPresetId)) return;
            var liveBones = _selection.Where(b => b != null).ToList();
            if (liveBones.Count == 0) return;
            var pull = _tweakPull; var spring = _tweakSpring; var stiff = _tweakStiff;
            var gravity = _tweakGravity; var radius = _tweakRadius;
            var presetId = _selectedPresetId;

            AvatarPreviewController.StartPreview(avatarRoot, (cloneRoot, session) => {
                var remapped = new List<Transform>(liveBones.Count);
                foreach (var b in liveBones) {
                    var mapped = AvatarPreviewController.MapToPreview(b);
                    if (mapped != null) remapped.Add(mapped);
                }
                if (remapped.Count == 0) return;
                PhysBonePresetApplier.ApplyNonDestructive(
                    remapped, presetId, pull, spring, stiff, gravity, radius, session);
            });
        }

        private void DrawTweakStrip() {
            using (WkStyles.Section($"Just applied ({_tweakSnapshots.Count} PhysBone(s)) — tweak",
                    "Multiplicative scalars applied on top of the original preset values. Drag back to 1.0× to restore exactly. Disappears when you change the selection or pick a different preset.")) {
                using (new EditorGUILayout.HorizontalScope()) {
                    if (GUILayout.Button(new GUIContent("Reset all to 1×",
                            "Restore every just-applied PhysBone to its original preset values."),
                            GUILayout.Width(140))) {
                        _tweakPull = _tweakSpring = _tweakStiff = _tweakGravity = _tweakRadius = 1f;
                        ApplyTweaks();
                    }
                    if (GUILayout.Button(new GUIContent("Dismiss",
                            "Hide the tweak strip. The applied values stay; Ctrl+Z still reverts the original Apply."),
                            GUILayout.Width(80))) {
                        _tweakSnapshots = null;
                    }
                }
                WkStyles.LabeledField(new GUIContent("Spring ×",  "Scale the spring parameter on every just-applied PhysBone by this factor."),
                    () => { var v = EditorGUILayout.Slider(_tweakSpring, 0.5f, 2f); if (!Mathf.Approximately(v, _tweakSpring)) { _tweakSpring = v; ApplyTweaks(); } });
                WkStyles.LabeledField(new GUIContent("Pull ×",    "Scale the pull parameter on every just-applied PhysBone by this factor."),
                    () => { var v = EditorGUILayout.Slider(_tweakPull, 0.5f, 2f); if (!Mathf.Approximately(v, _tweakPull)) { _tweakPull = v; ApplyTweaks(); } });
                WkStyles.LabeledField(new GUIContent("Stiffness ×", "Scale the stiffness parameter on every just-applied PhysBone by this factor."),
                    () => { var v = EditorGUILayout.Slider(_tweakStiff, 0.5f, 2f); if (!Mathf.Approximately(v, _tweakStiff)) { _tweakStiff = v; ApplyTweaks(); } });
                WkStyles.LabeledField(new GUIContent("Gravity ×", "Scale the gravity parameter on every just-applied PhysBone by this factor."),
                    () => { var v = EditorGUILayout.Slider(_tweakGravity, 0.5f, 2f); if (!Mathf.Approximately(v, _tweakGravity)) { _tweakGravity = v; ApplyTweaks(); } });
                WkStyles.LabeledField(new GUIContent("Radius ×",  "Scale the radius parameter on every just-applied PhysBone by this factor."),
                    () => { var v = EditorGUILayout.Slider(_tweakRadius, 0.5f, 2f); if (!Mathf.Approximately(v, _tweakRadius)) { _tweakRadius = v; ApplyTweaks(); } });
            }
        }

        private void DrawAdvanced() {
            _advancedOpen = EditorGUILayout.Foldout(_advancedOpen,
                new GUIContent("Advanced",
                    "Re-run analysis manually, plus the raw plan dump for debugging."),
                true, WkStyles.FoldoutHeader);
            if (!_advancedOpen) return;
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(_selection.Count == 0)) {
                    if (GUILayout.Button(new GUIContent("Refresh analysis",
                            "Re-walk the selection and rebuild the suggestion scores + plan."))) RebuildAnalysis();
                }
            }
        }
    }
}
