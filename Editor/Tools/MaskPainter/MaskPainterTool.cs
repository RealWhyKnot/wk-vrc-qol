// MaskPainterTool.cs
//
// Registers the entry points for the Mask Painter window. The window
// itself lives in MaskPainterWindow.cs.
//
// Two ways to open it:
//   1. Tools/WhyKnot/vrc-avatar-qol/Paint Mask...
//   2. Right-click a GameObject with a SkinnedMeshRenderer in the
//      hierarchy -> "WhyKnot/vrc-avatar-qol/Paint mask..." -- pre-fills
//      the window with that renderer.

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    [InitializeOnLoad]
    internal static class MaskPainterTool {

        private const string ToolsMenuPath      = "Tools/WhyKnot/vrc-avatar-qol/Paint Mask...";
        private const string GameObjectMenuPath = "GameObject/WhyKnot/vrc-avatar-qol/Paint mask...";

        static MaskPainterTool() { /* registration happens via [MenuItem] below */ }

        [MenuItem(ToolsMenuPath, false, 2003)]
        private static void OpenFromToolsMenu() {
            MaskPainterWindow.Open(prefillRenderer: null);
        }

        [MenuItem(GameObjectMenuPath, false, 51)]
        private static void OpenFromHierarchy(MenuCommand command) {
            // Hierarchy menu callbacks fire once per selected GameObject;
            // bail for all but the first so we don't open N windows.
            if (command.context != Selection.activeGameObject) return;
            var go = command.context as GameObject;
            var smr = go != null ? go.GetComponent<SkinnedMeshRenderer>() : null;
            MaskPainterWindow.Open(prefillRenderer: smr);
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
