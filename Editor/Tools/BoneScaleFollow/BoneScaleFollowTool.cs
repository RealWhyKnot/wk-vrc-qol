// BoneScaleFollowTool.cs

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class BoneScaleFollowTool {

        private const string ToolsMenuPath = "Tools/WhyKnot/wk-vrc-qol/Bone Scale Follow...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/wk-vrc-qol/Generate bone-scale follow blendshape...";

        static BoneScaleFollowTool() { }

        [MenuItem(ToolsMenuPath, false, 2008)]
        private static void OpenFromToolsMenu() {
            BoneScaleFollowWindow.Open(prefillFromSelection: false);
        }

        [MenuItem(GameObjectMenuPath, false, 56)]
        private static void OpenFromHierarchy(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return;
            BoneScaleFollowWindow.Open(prefillFromSelection: true);
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            var go = command.context as GameObject;
            if (go == null) return false;
            var renderer = go.GetComponent<SkinnedMeshRenderer>();
            return renderer != null && renderer.sharedMesh != null;
        }
    }
}
