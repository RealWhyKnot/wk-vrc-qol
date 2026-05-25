// LoomMenuItems.cs
//
// Entry points for the Loom subsystem. At M1 the surface is intentionally
// minimal: "Validate Selected Avatar" exercises the full discover ->
// validate pass without mutating anything, and the hierarchy add-component
// shortcut adds a WkLoomThread to the selected GameObject.
//
// The full authoring window (Overview / Tapestry / Build tabs) lands in
// M3 after M2 brings the rule + group machinery online.

using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using WhyKnot.AvatarQol.Loom.Pipeline;

namespace WhyKnot.AvatarQol.Loom.UI {

    internal static class LoomMenuItems {

        private const string ValidateMenuPath  = "Tools/WhyKnot/vrc-avatar-qol/Loom/Validate Selected Avatar";
        private const string AddThreadMenuPath = "GameObject/WhyKnot/vrc-avatar-qol/Add Loom Thread";

        [MenuItem(ValidateMenuPath, false, 2200)]
        private static void ValidateSelectedAvatar() {
            var selection = Selection.activeGameObject;
            if (selection == null) {
                AvatarQolLogger.Instance.Warning("Select an avatar GameObject before running Loom validation.");
                return;
            }
            var avatarRoot = FindAvatarRoot(selection);
            if (avatarRoot == null) {
                AvatarQolLogger.Instance.Warning(
                    "Loom validation: the selected GameObject is not under a VRCAvatarDescriptor. " +
                    "Select an avatar root (or any child of one) and try again.");
                return;
            }

            var discovery = LoomDiscovery.Discover(avatarRoot);
            var diagnostics = LoomValidator.Validate(discovery);

            int errors = 0, warnings = 0;
            foreach (var d in diagnostics) {
                switch (d.Severity) {
                    case LoomDiagnosticSeverity.Error:
                        AvatarQolLogger.Instance.Error($"Loom validation: {d.Message}");
                        errors++;
                        break;
                    case LoomDiagnosticSeverity.Warning:
                        AvatarQolLogger.Instance.Warning($"Loom validation: {d.Message}");
                        warnings++;
                        break;
                    default:
                        AvatarQolLogger.Instance.Info($"Loom validation: {d.Message}");
                        break;
                }
            }

            AvatarQolLogger.Instance.Info(
                $"Loom validation done for '{avatarRoot.name}': " +
                $"{discovery.Threads.Count} thread(s), {errors} error(s), {warnings} warning(s).");
        }

        [MenuItem(AddThreadMenuPath, false, 51)]
        private static void AddThreadFromHierarchy(MenuCommand command) {
            // GameObject menu commands fire once per selected GameObject;
            // bail for all but the first so we don't add N components when
            // the user multi-selects.
            if (command.context != Selection.activeGameObject) return;
            var go = command.context as GameObject;
            if (go == null) return;
            Undo.AddComponent<WkLoomThread>(go);
        }

        [MenuItem(AddThreadMenuPath, true)]
        private static bool AddThreadFromHierarchyValidate(MenuCommand command) {
            if (command.context != Selection.activeGameObject) return false;
            var go = command.context as GameObject;
            return go != null;
        }

        private static GameObject FindAvatarRoot(GameObject candidate) {
            var descriptor = candidate.GetComponentInParent<VRCAvatarDescriptor>(true);
            return descriptor != null ? descriptor.gameObject : null;
        }
    }
}
