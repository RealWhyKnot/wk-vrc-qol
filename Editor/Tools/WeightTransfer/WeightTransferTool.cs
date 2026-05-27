// WeightTransferTool.cs
//
// Retired public menu entries for the weight transfer window. The window
// and transfer backend stay in source.

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal static class WeightTransferTool {

        // private const string ToolsMenuPath = "Tools/WhyKnot/wk-vrc-qol/Weight Transfer...";
        // private const string GameObjectMenuPath = "GameObject/WhyKnot/wk-vrc-qol/Weight Transfer (target this)...";

        // [MenuItem(ToolsMenuPath, false, 2006)]
        private static void OpenFromToolsMenu() {
            WeightTransferWindow.Open(null);
        }

        // [MenuItem(GameObjectMenuPath, false, 54)]
        private static void OpenFromHierarchy(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return;
            var go = command.context as GameObject;
            var renderer = go != null ? go.GetComponent<SkinnedMeshRenderer>() : null;
            WeightTransferWindow.Open(renderer);
        }

        // [MenuItem(GameObjectMenuPath, true)]
        private static bool OpenFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            var go = command.context as GameObject;
            return go != null && go.GetComponent<SkinnedMeshRenderer>() != null;
        }
    }
}
