// MeshSculptTool.cs
//
// Menu entry points for the Mesh Sculpt window. The window owns the
// SceneView interaction and generated-mesh edit session.

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class MeshSculptTool {

        private const string ToolsMenuPath = "Tools/WhyKnot/wk-vrc-qol/Mesh Sculpt...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/wk-vrc-qol/Mesh Sculpt...";

        static MeshSculptTool() { /* MenuItem registration is enough. */ }

        [MenuItem(ToolsMenuPath, false, 2005)]
        private static void OpenFromToolsMenu() {
            MeshSculptWindow.Open(prefillRenderer: null);
        }

        [MenuItem(GameObjectMenuPath, false, 53)]
        private static void OpenFromHierarchy(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return;
            var go = command.context as GameObject;
            var smr = go != null ? go.GetComponent<SkinnedMeshRenderer>() : null;
            MeshSculptWindow.Open(prefillRenderer: smr);
        }

        [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            var go = command.context as GameObject;
            if (go == null) return false;
            return go.GetComponent<SkinnedMeshRenderer>() != null;
        }
    }
}
