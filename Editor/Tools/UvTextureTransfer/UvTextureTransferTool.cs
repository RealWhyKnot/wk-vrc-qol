// UvTextureTransferTool.cs
//
// Registers the entry points for the UV Texture Transfer window. The
// window itself lives in UvTextureTransferWindow.cs.
//
// Open via Tools/WhyKnot/wk-vrc-qol/UV Texture Transfer... or right-click
// a GameObject carrying a SkinnedMeshRenderer in the hierarchy -- the
// hierarchy entry pre-fills the target renderer.

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class UvTextureTransferTool {

        private const string ToolsMenuPath      = "Tools/WhyKnot/wk-vrc-qol/UV Texture Transfer...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/wk-vrc-qol/UV Texture Transfer (target this)...";

        static UvTextureTransferTool() { /* MenuItem registration is enough */ }

        [MenuItem(ToolsMenuPath, false, 2004)]
        private static void OpenFromToolsMenu() {
            UvTextureTransferWindow.Open(prefillTargetRenderer: null);
        }

        [MenuItem(GameObjectMenuPath, false, 52)]
        private static void OpenFromHierarchy(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return;
            var go = command.context as GameObject;
            var smr = go != null ? go.GetComponent<SkinnedMeshRenderer>() : null;
            UvTextureTransferWindow.Open(prefillTargetRenderer: smr);
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
