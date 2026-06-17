// MaterialPolygonRemoverTool.cs

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class MaterialPolygonRemoverTool {

        private const string ToolsMenuPath = "Tools/WhyKnot/wk-vrc-qol/Material Polygon Remover...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/wk-vrc-qol/Remove polygons by material...";

        static MaterialPolygonRemoverTool() { }

        [MenuItem(ToolsMenuPath, false, 2006)]
        private static void OpenFromToolsMenu() {
            MaterialPolygonRemoverWindow.Open(prefillFromSelection: false);
        }

        [MenuItem(GameObjectMenuPath, false, 54)]
        private static void OpenFromHierarchy(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return;
            MaterialPolygonRemoverWindow.Open(prefillFromSelection: true);
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            var go = command.context as GameObject;
            if (go == null) return false;
            return go.GetComponent<SkinnedMeshRenderer>() != null
                || go.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }
    }
}
