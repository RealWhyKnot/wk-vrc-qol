// WhyKnotPhysBonePresetIntentEditor.cs
//
// Inspector for WhyKnotPhysBonePresetIntent. Mirrors the BoneMerger
// intent inspector shape: serialized fields + preview triad (Preview,
// Stop preview, Open window). The pair-list / preset-picker UI lives in
// the PhysBone Preset window; the inspector is intentionally minimal so
// the destructive flow stays the entry point for choosing a preset.

using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Tools;
using WhyKnot.Core.Styling;

namespace WhyKnot.AvatarQol.PhysBonePreset.UI {

    [CustomEditor(typeof(WhyKnotPhysBonePresetIntent))]
    [CanEditMultipleObjects]
    internal sealed class WhyKnotPhysBonePresetIntentEditor : Editor {

        private SerializedProperty _bones;
        private SerializedProperty _presetId;
        private SerializedProperty _tweakPull;
        private SerializedProperty _tweakSpring;
        private SerializedProperty _tweakStiff;
        private SerializedProperty _tweakGravity;
        private SerializedProperty _tweakRadius;
        private SerializedProperty _processInPlayMode;
        private SerializedProperty _processOnUpload;
        private SerializedProperty _verboseLog;

        private void OnEnable() {
            _bones              = serializedObject.FindProperty(nameof(WhyKnotPhysBonePresetIntent.bones));
            _presetId           = serializedObject.FindProperty(nameof(WhyKnotPhysBonePresetIntent.presetId));
            _tweakPull          = serializedObject.FindProperty(nameof(WhyKnotPhysBonePresetIntent.tweakPull));
            _tweakSpring        = serializedObject.FindProperty(nameof(WhyKnotPhysBonePresetIntent.tweakSpring));
            _tweakStiff         = serializedObject.FindProperty(nameof(WhyKnotPhysBonePresetIntent.tweakStiff));
            _tweakGravity       = serializedObject.FindProperty(nameof(WhyKnotPhysBonePresetIntent.tweakGravity));
            _tweakRadius        = serializedObject.FindProperty(nameof(WhyKnotPhysBonePresetIntent.tweakRadius));
            _processInPlayMode  = serializedObject.FindProperty(nameof(WhyKnotPhysBonePresetIntent.processInPlayMode));
            _processOnUpload    = serializedObject.FindProperty(nameof(WhyKnotPhysBonePresetIntent.processOnUpload));
            _verboseLog         = serializedObject.FindProperty(nameof(WhyKnotPhysBonePresetIntent.verboseLog));
        }

        public override void OnInspectorGUI() {
            using var _wkTheme = WkStyles.Scope(WkTheme.WhyKnot);
            serializedObject.Update();

            EditorGUILayout.PropertyField(_bones,
                new GUIContent("Bones", "Chain roots the preset will set up. Each top-level Transform spawns a chain; descendants are walked automatically."),
                includeChildren: true);
            EditorGUILayout.PropertyField(_presetId,
                new GUIContent("Preset id", "Stable identifier (e.g. \"tail\", \"hair\"). Use the PhysBone Preset window's Add as Intent button to fill this with the correct value."));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Post-apply tweaks (multiplicative)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_tweakPull,    new GUIContent("Pull ×",      "Multiplier on the Pull parameter for every spawned PhysBone."));
            EditorGUILayout.PropertyField(_tweakSpring,  new GUIContent("Spring ×",    "Multiplier on the Spring parameter for every spawned PhysBone."));
            EditorGUILayout.PropertyField(_tweakStiff,   new GUIContent("Stiffness ×", "Multiplier on the Stiffness parameter for every spawned PhysBone."));
            EditorGUILayout.PropertyField(_tweakGravity, new GUIContent("Gravity ×",   "Multiplier on the Gravity parameter for every spawned PhysBone."));
            EditorGUILayout.PropertyField(_tweakRadius,  new GUIContent("Radius ×",    "Multiplier on the Radius parameter for every spawned PhysBone and every preset-defined collider."));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("When to run", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_processInPlayMode,
                new GUIContent("Process in Play mode", "Spawn the PhysBones when entering Play mode. Components live in memory only."));
            EditorGUILayout.PropertyField(_processOnUpload,
                new GUIContent("Process on Upload", "Spawn the PhysBones during avatar Build & Publish. Components live in memory only."));
            EditorGUILayout.PropertyField(_verboseLog,
                new GUIContent("Verbose log", "Write per-apply spawn stats to the WhyKnot log when this intent runs."));

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(6);
            DrawActionButtons();
        }

        private void DrawActionButtons() {
            var intent = (WhyKnotPhysBonePresetIntent)target;
            if (intent == null) return;
            var animator = intent.GetComponentInParent<Animator>(true);
            var avatarRoot = animator != null ? animator.gameObject : intent.gameObject;
            bool canPreview = intent.bones != null && intent.bones.Count > 0
                              && !string.IsNullOrEmpty(intent.presetId)
                              && PhysBonePresetApplier.SdkAvailable;
            bool isPreviewing = AvatarPreviewController.IsPreviewing
                && AvatarPreviewController.SourceAvatar == avatarRoot;

            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!canPreview || isPreviewing)) {
                    if (GUILayout.Button(
                            new GUIContent("Preview spawn",
                                "Clone the avatar in place and spawn the preset's PhysBones + colliders on the clone so you can verify behaviour without committing components to the source."),
                            GUILayout.Height(26))) {
                        StartPreview(avatarRoot, intent);
                    }
                }
                using (new EditorGUI.DisabledScope(!isPreviewing)) {
                    if (GUILayout.Button(
                            new GUIContent("Stop preview",
                                "Destroy the preview clone and un-hide the source avatar."),
                            GUILayout.Height(26), GUILayout.Width(110))) {
                        AvatarPreviewController.StopPreview();
                    }
                }
                if (GUILayout.Button(
                        new GUIContent("Open window",
                            "Open the PhysBone Preset window for the destructive flow (spawns components directly on the avatar with Undo support)."),
                        GUILayout.Height(26), GUILayout.Width(110))) {
                    PhysBonePresetWindow.Open(prefillFromSelection: false);
                }
            }

            if (!PhysBonePresetApplier.SdkAvailable) {
                EditorGUILayout.HelpBox("VRChat SDK 3 (PhysBone) is not installed; the build hook will skip this intent.", MessageType.Warning);
            }
        }

        private static void StartPreview(GameObject avatarRoot, WhyKnotPhysBonePresetIntent intent) {
            if (avatarRoot == null) return;
            var bones = intent.bones;
            var presetId = intent.presetId;
            var pull = intent.tweakPull;
            var spring = intent.tweakSpring;
            var stiff = intent.tweakStiff;
            var gravity = intent.tweakGravity;
            var radius = intent.tweakRadius;
            bool verbose = intent.verboseLog;

            var result = AvatarPreviewController.StartPreview(avatarRoot, (cloneRoot, session) => {
                // Bone list references SOURCE-avatar transforms; remap to the
                // clone so the preset builds its plan against the cloned
                // hierarchy.
                var remapped = new System.Collections.Generic.List<Transform>(bones.Count);
                foreach (var b in bones) {
                    var mapped = AvatarPreviewController.MapToPreview(b);
                    if (mapped != null) remapped.Add(mapped);
                }
                if (remapped.Count == 0) return;
                var op = PhysBonePresetApplier.ApplyNonDestructive(
                    remapped, presetId, pull, spring, stiff, gravity, radius, session);
                if (verbose) {
                    AvatarQolLogger.Instance.Info($"PhysBonePreset preview: {op.Summary}");
                }
            });
            if (result.Errors.Count > 0) {
                foreach (var e in result.Errors) AvatarQolLogger.Instance.Warning("PhysBonePreset preview: " + e);
            }
        }
    }
}
