// BoneMergerWindow.cs
//
// Folds a stray duplicate bone into the bone it should have been part of.
// Typical case: Blender / FBX export produced a ".001" sub-bone (Boob_L.001
// under Boob_L, Hair_R.001 under Hair_R) and the user wants those gone --
// the sub-bone's skin weights should belong to the parent, and the extra
// transform shouldn't exist in the rig.
//
// What "merge" means here:
//   - For every SkinnedMeshRenderer under the picked Animator, any vertex
//     weight on the merge-from bone is redirected to the merge-into bone.
//     Duplicate slots collapse, slots sort by weight descending, slots that
//     hit zero get dropped.
//   - If Delete is on, the merge-from GameObject is destroyed. Its children
//     re-parent onto the merge-into bone first so any rig sitting underneath
//     it stays in place (world transforms preserved).
//
// Window-level Apply mutates .mesh assets immediately. "Add as Intent"
// stores the pair list on a WhyKnotBoneMergerIntent on the avatar root --
// the merge then re-runs against whatever mesh the renderer currently
// has at play and at upload, in memory only, so a Blender re-import does
// not silently drop the fix. Preview clones the avatar and applies the
// merge to the clone so the user can sanity-check before committing.
//
// Mathematical caveat: weight-merging is visually identical to the
// original skinning only when the two bones share the same rest pose
// (i.e. the sub-bone sits at local zero under the kept bone). When the
// rest poses differ, vertices that were weighted to the sub-bone will
// snap to the kept bone's frame at the rig's rest pose. That's inherent
// to linear blend skinning, not a tool bug.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.BoneMerger;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class BoneMergerWindow : EditorWindow {

        [SerializeField] private Animator _animator;
        [SerializeField] private List<BoneMergerPair> _pairs = new List<BoneMergerPair>();
        [SerializeField] private bool _deleteMergedBones = true;
        [SerializeField] private bool _reparentChildren  = true;

        private const string WikiUrl = "https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Tools-Overview#bone-merger";

        private string _resultSummary = "";
        private readonly List<string> _resultDetail = new List<string>();
        private Vector2 _scroll;

        // ------ Public entry points ----------------------------------------

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<BoneMergerWindow>(false, "Bone Merger", true);
            w.titleContent = new GUIContent("Avatar QoL -- Bone Merger");
            w.minSize = new Vector2(560, 460);
            if (prefillFromSelection) w.PrefillFromSelection();
            w.Show();
            w.Focus();
        }

        private void PrefillFromSelection() {
            var go = Selection.activeGameObject;
            if (go == null) return;
            var animator = go.GetComponent<Animator>() ??
                           go.GetComponentInParent<Animator>(true) ??
                           go.GetComponentInChildren<Animator>(true);
            if (animator != null) _animator = animator;
            ClearResults();
        }

        // ------ GUI --------------------------------------------------------

        private void OnGUI() {
            using var _wkTheme = WkStyles.Scope(WkTheme.WhyKnot);
            DrawTitleBar();
            WkStyles.Notice(NoticeKind.Info,
                "Folds a stray duplicate bone (e.g. Blender's Boob_L.001) into its kept counterpart. Apply mutates .mesh assets and (optionally) destroys the merged-away bone. Add as Intent stores the pair list on the avatar and applies the merge in memory at play / build instead. Preview clones the avatar and shows the merged result without committing.");
            DrawAvatar();
            EditorGUILayout.Space(2);
            DrawPairs();
            EditorGUILayout.Space(2);
            DrawOptions();
            EditorGUILayout.Space(2);
            DrawApplyBar();
            EditorGUILayout.Space(2);
            DrawResults();
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Bone Merger",
                        "Transfer skin weights from one bone onto another, then optionally delete the now-empty bone. Useful for collapsing Blender ''.001'' duplicate bones back into their parent."),
                    WkStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawAvatar() {
            using (WkStyles.Section("1. Pick avatar",
                    "The Animator at the root of the avatar. Every SkinnedMeshRenderer under it gets scanned for weights on the bones you list below.")) {
                WkStyles.LabeledField(
                    new GUIContent("Animator",
                        "Animator at the avatar root. Doesn't need to be Humanoid -- any rig works."),
                    () => {
                        var next = (Animator)EditorGUILayout.ObjectField(_animator, typeof(Animator), true);
                        if (next != _animator) { _animator = next; ClearResults(); }
                    });
                if (_animator != null) {
                    int smrCount = _animator.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
                    EditorGUILayout.LabelField($"{smrCount} SkinnedMeshRenderer(s) under this Animator.", WkStyles.Muted);
                    var existingIntent = _animator.GetComponent<WhyKnotBoneMergerIntent>();
                    if (existingIntent != null) {
                        EditorGUILayout.LabelField($"Existing intent on this avatar: {existingIntent.pairs?.Count ?? 0} pair(s).", WkStyles.Muted);
                    }
                }
            }
        }

        private void DrawPairs() {
            using (WkStyles.Section("2. Bones to merge",
                    "Drag two bones onto each row. The LEFT bone's weights move onto the RIGHT bone, and the LEFT bone is then removed.")) {
                EditorGUILayout.LabelField(
                    "Read each row left to right: \"merge THIS bone into THIS one\". Drag the duplicate / extra bone on the left, the keeper on the right.",
                    WkStyles.Muted);

                EditorGUILayout.Space(2);

                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(
                        new GUIContent("Merge this bone",
                            "The bone you want to get rid of. Its weights are transferred away, and (if Delete is on) its GameObject is destroyed."),
                        EditorStyles.miniBoldLabel);
                    GUILayout.Space(20);
                    EditorGUILayout.LabelField(
                        new GUIContent("into this bone",
                            "The bone you want to keep. It receives the weights from the merged-away bone."),
                        EditorStyles.miniBoldLabel);
                    GUILayout.Space(24);
                }

                if (_pairs.Count == 0) {
                    EditorGUILayout.LabelField("(no pairs yet -- click Add row to start)", EditorStyles.centeredGreyMiniLabel);
                } else {
                    int removeAt = -1;
                    for (int i = 0; i < _pairs.Count; i++) {
                        var pair = _pairs[i] ?? (_pairs[i] = new BoneMergerPair());
                        using (new EditorGUILayout.HorizontalScope()) {
                            var nextFrom = (Transform)EditorGUILayout.ObjectField(
                                new GUIContent(GUIContent.none.image, "The bone whose weights are being transferred away. This is the bone that disappears."),
                                pair.mergeFrom, typeof(Transform), true);
                            EditorGUILayout.LabelField(
                                new GUIContent("into", "Weights move from the LEFT bone onto the RIGHT bone."),
                                GUILayout.Width(28));
                            var nextInto = (Transform)EditorGUILayout.ObjectField(
                                new GUIContent(GUIContent.none.image, "The bone that picks up the weights. This bone stays."),
                                pair.mergeInto, typeof(Transform), true);
                            if (nextFrom != pair.mergeFrom || nextInto != pair.mergeInto) {
                                pair.mergeFrom = nextFrom;
                                pair.mergeInto = nextInto;
                                ClearResults();
                            }
                            if (GUILayout.Button(
                                    new GUIContent("X", "Remove this row."),
                                    EditorStyles.miniButton, GUILayout.Width(22))) {
                                removeAt = i;
                            }
                        }
                        if (pair.mergeFrom != null && pair.mergeInto != null && pair.mergeFrom == pair.mergeInto) {
                            EditorGUILayout.LabelField("   Both fields point at the same bone -- nothing to merge.", WkStyles.Muted);
                        }
                    }
                    if (removeAt >= 0) { _pairs.RemoveAt(removeAt); ClearResults(); }
                }

                using (new EditorGUILayout.HorizontalScope()) {
                    if (GUILayout.Button(
                            new GUIContent("Add row", "Append an empty pair slot. Drag two bones onto it after."),
                            GUILayout.Width(80))) {
                        _pairs.Add(new BoneMergerPair());
                        ClearResults();
                    }
                    using (new EditorGUI.DisabledScope(_pairs.Count == 0)) {
                        if (GUILayout.Button(
                                new GUIContent("Clear list", "Remove every pair from the list."),
                                GUILayout.Width(80))) {
                            _pairs.Clear();
                            ClearResults();
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
            }
        }

        private void DrawOptions() {
            using (WkStyles.Section("3. Options (destructive Apply only)",
                    "These options only affect the destructive Apply button. Add as Intent never touches the bone hierarchy.")) {
                _deleteMergedBones = EditorGUILayout.ToggleLeft(
                    new GUIContent("Delete the merged-away bone after applying",
                        "When on, the LEFT bone in each row gets removed from the hierarchy after its weights are transferred. Turn off if you just want to re-weight without changing the rig."),
                    _deleteMergedBones);
                using (new EditorGUI.DisabledScope(!_deleteMergedBones)) {
                    _reparentChildren = EditorGUILayout.ToggleLeft(
                        new GUIContent("Re-parent its children onto the kept bone",
                            "When on, any GameObjects parented under the merged-away bone get moved onto the kept bone (world transform preserved) before deletion. Recommended -- otherwise the children get destroyed with the bone."),
                        _reparentChildren);
                }
            }
        }

        private void DrawApplyBar() {
            bool canApply = CanApply();
            var avatarRoot = _animator != null ? _animator.gameObject : null;
            bool isPreviewing = AvatarPreviewController.IsPreviewing
                && AvatarPreviewController.SourceAvatar == avatarRoot;

            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!canApply)) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Apply merge",
                                "Destructive. Clone the affected meshes to .mesh assets under Assets/AvatarQol Generated/, rewire renderers, and (if enabled) delete merged-away bones. Wrapped in a single Undo step."),
                            GUILayout.MinWidth(140))) {
                        ApplyDestructive();
                    }
                }
                using (new EditorGUI.DisabledScope(!canApply || avatarRoot == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Add as Intent",
                                "Non-destructive. Save the pair list to a WhyKnotBoneMergerIntent on the avatar root. The merge then re-runs in memory at play mode and at upload, never touching mesh assets or bone GameObjects."),
                            GUILayout.Height(28), GUILayout.Width(118))) {
                        AddAsIntent(avatarRoot);
                    }
                }
                using (new EditorGUI.DisabledScope(!canApply || avatarRoot == null || isPreviewing)) {
                    if (GUILayout.Button(
                            new GUIContent("Preview",
                                "Non-destructive. Clone the avatar in place and apply the merge to the clone so you can see the deformation without committing changes."),
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
                using (new EditorGUI.DisabledScope(_resultSummary == "" && _resultDetail.Count == 0)) {
                    if (GUILayout.Button(
                            new GUIContent("Clear result",
                                "Drop the last result summary. Doesn't undo any merge you've already applied."),
                            GUILayout.Height(28), GUILayout.Width(94))) {
                        ClearResults();
                    }
                }
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(_resultSummary)) {
                    EditorGUILayout.LabelField(_resultSummary, WkStyles.Muted);
                }
            }

            if (_deleteMergedBones) {
                EditorGUILayout.HelpBox(
                    "Heads-up: 'Delete merged-away bones' is on, so Apply will mutate the scene hierarchy. 'Add as Intent' and 'Preview' ignore this flag -- they never delete bones.",
                    MessageType.Info);
            }
        }

        private void DrawResults() {
            if (_resultDetail.Count == 0) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                EditorGUILayout.LabelField("Last run", WkStyles.SubsectionTitle);
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                foreach (var line in _resultDetail) {
                    EditorGUILayout.LabelField(line, WkStyles.Mono);
                }
                EditorGUILayout.EndScrollView();
            }
        }

        // ------ Validation -------------------------------------------------

        private bool CanApply() {
            if (_animator == null) return false;
            foreach (var p in _pairs) {
                if (p != null && p.mergeFrom != null && p.mergeInto != null && p.mergeFrom != p.mergeInto) {
                    return true;
                }
            }
            return false;
        }

        // ------ Action entry points ---------------------------------------

        private void ApplyDestructive() {
            ClearResults();
            var result = BoneMergerOp.ApplyDestructive(_animator, _pairs, _deleteMergedBones, _reparentChildren);
            _resultSummary = result.Summary;
            _resultDetail.AddRange(result.Detail);
            if (result.ClonedPaths.Count > 0) {
                _resultDetail.Add("");
                _resultDetail.Add("Cloned meshes (your FBX was untouched):");
                foreach (var p in result.ClonedPaths) _resultDetail.Add($"  {p}");
            }
        }

        private void AddAsIntent(GameObject avatarRoot) {
            ClearResults();
            if (avatarRoot == null) return;
            var validPairs = _pairs
                .Where(p => p != null && p.mergeFrom != null && p.mergeInto != null && p.mergeFrom != p.mergeInto)
                .ToList();
            if (validPairs.Count == 0) {
                _resultSummary = "Add at least one pair before saving as intent.";
                return;
            }

            Undo.SetCurrentGroupName("Avatar QoL: add BoneMerger intent");
            int group = Undo.GetCurrentGroup();

            var intent = avatarRoot.GetComponent<WhyKnotBoneMergerIntent>();
            if (intent == null) {
                intent = Undo.AddComponent<WhyKnotBoneMergerIntent>(avatarRoot);
            } else {
                Undo.RecordObject(intent, "Update BoneMerger intent");
            }
            intent.pairs = validPairs.Select(p => new BoneMergerPair {
                mergeFrom = p.mergeFrom,
                mergeInto = p.mergeInto,
            }).ToList();
            intent.deleteMergedBones = _deleteMergedBones;
            intent.reparentChildren = _reparentChildren;
            EditorUtility.SetDirty(intent);

            Undo.CollapseUndoOperations(group);

            _resultSummary = $"Saved {intent.pairs.Count} pair(s) to BoneMerger intent on {avatarRoot.name}.";
            if (_deleteMergedBones) {
                _resultDetail.Add("Note: 'Delete merged-away bones' is recorded on the intent but is ignored by the build / play hooks -- it only matters when the Bone Merger window's Apply button runs.");
            }
            Selection.activeGameObject = avatarRoot;
            EditorGUIUtility.PingObject(intent);
        }

        private void StartPreview(GameObject avatarRoot) {
            ClearResults();
            if (avatarRoot == null) return;
            var capturedPairs = _pairs
                .Where(p => p != null && p.mergeFrom != null && p.mergeInto != null && p.mergeFrom != p.mergeInto)
                .Select(p => new BoneMergerPair { mergeFrom = p.mergeFrom, mergeInto = p.mergeInto })
                .ToList();
            if (capturedPairs.Count == 0) {
                _resultSummary = "Add at least one valid pair before previewing.";
                return;
            }
            var result = AvatarPreviewController.StartPreview(avatarRoot, (cloneRoot, session) => {
                var cloneAnimator = cloneRoot.GetComponentInChildren<Animator>(true);
                if (cloneAnimator == null) return;
                // Pair entries reference SOURCE-avatar transforms. The runner
                // operates against the clone, so remap each transform to its
                // clone equivalent via hierarchy path before applying.
                var remapped = capturedPairs
                    .Select(p => new BoneMergerPair {
                        mergeFrom = AvatarPreviewController.MapToPreview(p.mergeFrom),
                        mergeInto = AvatarPreviewController.MapToPreview(p.mergeInto),
                    })
                    .Where(p => p.mergeFrom != null && p.mergeInto != null)
                    .ToList();
                if (remapped.Count == 0) return;
                BoneMergerOp.ApplyNonDestructive(cloneAnimator, remapped, session);
            });
            if (result.Errors.Count > 0) {
                _resultSummary = "Preview failed: " + result.Errors[0];
                _resultDetail.AddRange(result.Errors);
            } else if (!AvatarPreviewController.IsPreviewing) {
                _resultSummary = "Preview ran but produced no changes (no pair matched any renderer's bones[]).";
            } else {
                _resultSummary = $"Previewing merge on {avatarRoot.name}. Source hidden in Scene view; Stop Preview reverts.";
            }
        }

        private void ClearResults() {
            _resultSummary = "";
            _resultDetail.Clear();
        }
    }
}
