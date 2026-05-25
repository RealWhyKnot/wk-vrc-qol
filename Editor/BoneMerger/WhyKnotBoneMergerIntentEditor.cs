// WhyKnotBoneMergerIntentEditor.cs
//
// Inspector for WhyKnotBoneMergerIntent. The component is the durable
// storage for "at play / build, merge these bone pairs into the mesh in
// memory"; the inspector lets the user edit the pair list, toggle when
// the intent fires, and either jump to the destructive window or
// preview the merged result against an avatar clone.

using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.BoneMerger;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Tools;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.BoneMerger.UI {

    [CustomEditor(typeof(WhyKnotBoneMergerIntent))]
    [CanEditMultipleObjects]
    internal sealed class WhyKnotBoneMergerIntentEditor : Editor {

        private SerializedProperty _pairs;
        private SerializedProperty _deleteMergedBones;
        private SerializedProperty _reparentChildren;
        private SerializedProperty _processInPlayMode;
        private SerializedProperty _processOnUpload;
        private SerializedProperty _verboseLog;

        private void OnEnable() {
            _pairs              = serializedObject.FindProperty(nameof(WhyKnotBoneMergerIntent.pairs));
            _deleteMergedBones  = serializedObject.FindProperty(nameof(WhyKnotBoneMergerIntent.deleteMergedBones));
            _reparentChildren   = serializedObject.FindProperty(nameof(WhyKnotBoneMergerIntent.reparentChildren));
            _processInPlayMode  = serializedObject.FindProperty(nameof(WhyKnotBoneMergerIntent.processInPlayMode));
            _processOnUpload    = serializedObject.FindProperty(nameof(WhyKnotBoneMergerIntent.processOnUpload));
            _verboseLog         = serializedObject.FindProperty(nameof(WhyKnotBoneMergerIntent.verboseLog));
        }

        public override void OnInspectorGUI() {
            using var _wkTheme = WkStyles.Scope(WkTheme.WhyKnot);
            serializedObject.Update();

            EditorGUILayout.PropertyField(_pairs,
                new GUIContent("Pairs", "Each row: weights move from the FIRST bone onto the SECOND bone, across every SkinnedMeshRenderer under the avatar."),
                includeChildren: true);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                new GUIContent("Destructive-only options",
                    "Bone-GameObject mutations only happen when the Bone Merger window's Apply button is pressed. The build / play hooks ignore these flags -- they never touch the hierarchy."),
                EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_deleteMergedBones,
                new GUIContent("Delete merged-away bones (window Apply only)",
                    "When the Bone Merger window's Apply button runs against this intent, also destroy each merged-away bone's GameObject."));
            using (new EditorGUI.DisabledScope(!_deleteMergedBones.boolValue)) {
                EditorGUILayout.PropertyField(_reparentChildren,
                    new GUIContent("Re-parent children (window Apply only)",
                        "When deleting bones, move any GameObjects parented under them onto the kept bone first so they aren't destroyed too."));
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("When to run", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_processInPlayMode,
                new GUIContent("Process in Play mode", "Apply the merge when entering Play mode. Mesh is cloned in memory; source asset stays untouched."));
            EditorGUILayout.PropertyField(_processOnUpload,
                new GUIContent("Process on Upload", "Apply the merge during avatar Build & Publish. Mesh is cloned in memory; source asset stays untouched."));
            EditorGUILayout.PropertyField(_verboseLog,
                new GUIContent("Verbose log", "Write per-renderer merge stats to the WhyKnot log when this intent runs."));

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(6);
            DrawActionButtons();
        }

        private void DrawActionButtons() {
            var intent = (WhyKnotBoneMergerIntent)target;
            if (intent == null) return;
            var animator = intent.GetComponentInParent<Animator>(true);
            var avatarRoot = animator != null ? animator.gameObject : intent.gameObject;
            bool canPreview = animator != null && intent.pairs != null && intent.pairs.Count > 0;
            bool isPreviewing = AvatarPreviewController.IsPreviewing
                && AvatarPreviewController.SourceAvatar == avatarRoot;

            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!canPreview || isPreviewing)) {
                    if (GUILayout.Button(
                            new GUIContent("Preview merge",
                                "Clone the avatar in place and apply the merge against the clone so you can see the deformation without committing changes. Stop Preview reverts."),
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
                            "Open the Bone Merger window for the destructive flow (writes a .mesh asset and optionally deletes the merged-away bones)."),
                        GUILayout.Height(26), GUILayout.Width(110))) {
                    BoneMergerWindow.Open(prefillFromSelection: false);
                }
            }

            if (animator == null) {
                EditorGUILayout.HelpBox("No Animator found above this component; preview and the build hook both need one to find the renderers.", MessageType.Warning);
            }
        }

        private static void StartPreview(GameObject avatarRoot, WhyKnotBoneMergerIntent intent) {
            if (avatarRoot == null) return;
            var pairs = intent.pairs;
            bool verbose = intent.verboseLog;
            var result = AvatarPreviewController.StartPreview(avatarRoot, (cloneRoot, session) => {
                var cloneAnimator = cloneRoot.GetComponentInChildren<Animator>(true);
                if (cloneAnimator == null) return;
                // Pairs reference SOURCE-avatar transforms; remap to the clone
                // so the runner finds them in the cloned renderers' bones[].
                var remapped = new System.Collections.Generic.List<BoneMergerPair>(pairs.Count);
                foreach (var p in pairs) {
                    if (p == null || p.mergeFrom == null || p.mergeInto == null) continue;
                    var from = AvatarPreviewController.MapToPreview(p.mergeFrom);
                    var into = AvatarPreviewController.MapToPreview(p.mergeInto);
                    if (from != null && into != null) {
                        remapped.Add(new BoneMergerPair { mergeFrom = from, mergeInto = into });
                    }
                }
                if (remapped.Count == 0) return;
                var op = BoneMergerOp.ApplyNonDestructive(cloneAnimator, remapped, session);
                if (verbose) {
                    AvatarQolLogger.Instance.Info($"BoneMerger preview: {op.Summary}");
                }
            });
            if (result.Errors.Count > 0) {
                foreach (var e in result.Errors) AvatarQolLogger.Instance.Warning("BoneMerger preview: " + e);
            }
        }
    }
}
