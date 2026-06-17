// BlendShapeTransferTool.cs

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class BlendShapeTransferTool {

        private const string ToolsMenuPath = "Tools/WhyKnot/wk-vrc-qol/BlendShape Transfer...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/wk-vrc-qol/Transfer blendshape from this...";

        static BlendShapeTransferTool() { }

        [MenuItem(ToolsMenuPath, false, 2007)]
        private static void OpenFromToolsMenu() {
            BlendShapeTransferWindow.Open(prefillFromSelection: false);
        }

        [MenuItem(GameObjectMenuPath, false, 55)]
        private static void OpenFromHierarchy(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return;
            BlendShapeTransferWindow.Open(prefillFromSelection: true);
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            var go = command.context as GameObject;
            if (go == null) return false;
            var renderer = go.GetComponent<SkinnedMeshRenderer>();
            return renderer != null && renderer.sharedMesh != null && renderer.sharedMesh.blendShapeCount > 0;
        }
    }
}
