// OrphanedBoneWeightCleanerTool.cs

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class OrphanedBoneWeightCleanerTool {

        private const string ToolsMenuPath = "Tools/WhyKnot/wk-vrc-qol/Orphaned Bone Weight Cleaner...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/wk-vrc-qol/Clean orphaned bone weights...";

        static OrphanedBoneWeightCleanerTool() { }

        [MenuItem(ToolsMenuPath, false, 2005)]
        private static void OpenFromToolsMenu() {
            OrphanedBoneWeightCleanerWindow.Open(prefillFromSelection: false);
        }

        [MenuItem(GameObjectMenuPath, false, 53)]
        private static void OpenFromHierarchy(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return;
            OrphanedBoneWeightCleanerWindow.Open(prefillFromSelection: true);
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            var go = command.context as GameObject;
            if (go == null) return false;
            return go.GetComponentInParent<Animator>(true) != null
                || go.GetComponentInChildren<Animator>(true) != null
                || go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }
    }
}
