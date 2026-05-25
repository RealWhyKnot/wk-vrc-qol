// LoomValidator.cs
//
// Run before the Planner. Catches the failure modes that the design brief
// called out as silently-broken in VRCFury today:
//
//   * Missing VRCAvatarDescriptor on the avatar root (build can't proceed
//     without somewhere to attach the FX layer / params / menu).
//   * Thread with an empty ResolvedParameterName -- means the user left
//     both menuPath AND explicitParamName blank, which yields an unnamed
//     animator parameter.
//   * Two Threads compiling to the same parameter name -- silent value
//     collisions in the animator, very hard to diagnose live.
//   * ObjectToggleAction.target == null -- the "(NO VALID ANIMATIONS)"
//     layer that shows up in Ume's compiled controller today.
//   * ObjectToggleAction.mode == Toggle -- not implemented at M1, error
//     out instead of silently lowering to TurnOn.
//
// Validation is a list of diagnostics, not a thrown exception. The build
// hook decides whether any Error stops the upload; the Loom window's
// Validate Avatar action surfaces the full list so the user can fix
// multiple issues per pass.

using System.Collections.Generic;

namespace WhyKnot.AvatarQol.Loom.Pipeline {

    internal static class LoomValidator {

        public static List<LoomDiagnostic> Validate(LoomDiscoveryResult discovery) {
            var diagnostics = new List<LoomDiagnostic>();
            if (discovery == null || discovery.AvatarRoot == null) {
                diagnostics.Add(LoomDiagnostic.Error("No avatar selected for Loom validation."));
                return diagnostics;
            }

            if (discovery.Descriptor == null) {
                diagnostics.Add(LoomDiagnostic.Error(
                    $"GameObject '{discovery.AvatarRoot.name}' has no VRCAvatarDescriptor. " +
                    "Loom needs an avatar descriptor to generate FX layer / parameters / menu.",
                    discovery.AvatarRoot));
            }

            var seenParamNames = new Dictionary<string, WkLoomThread>();
            foreach (var thread in discovery.Threads) {
                ValidateThread(thread, seenParamNames, diagnostics);
            }

            return diagnostics;
        }

        private static void ValidateThread(
            WkLoomThread thread,
            Dictionary<string, WkLoomThread> seenParamNames,
            List<LoomDiagnostic> diagnostics) {
            if (thread == null) return;

            var paramName = thread.ResolvedParameterName;
            if (string.IsNullOrEmpty(paramName)) {
                diagnostics.Add(LoomDiagnostic.Error(
                    $"Thread on '{thread.gameObject.name}' has no menu path or explicit parameter name. " +
                    "Set Menu Path or Explicit Parameter Name so the build pipeline can generate a unique " +
                    "synced parameter.",
                    thread));
            } else if (seenParamNames.TryGetValue(paramName, out var prior)) {
                diagnostics.Add(LoomDiagnostic.Error(
                    $"Two Threads compile to the same parameter name '{paramName}': " +
                    $"'{prior.gameObject.name}' and '{thread.gameObject.name}'. " +
                    "Set Explicit Parameter Name on one of them to disambiguate, or rename a Menu Path.",
                    thread));
            } else {
                seenParamNames[paramName] = thread;
            }

            if (thread.kind != ThreadKind.Bool) {
                diagnostics.Add(LoomDiagnostic.Error(
                    $"Thread '{paramName}' uses ThreadKind.{thread.kind}; only Bool is implemented at M1. " +
                    "Float (slider) and Int (n-state radio) arrive in M2.",
                    thread));
            }

            for (int i = 0; i < thread.actions.Count; i++) {
                ValidateAction(thread, i, thread.actions[i], diagnostics);
            }
        }

        private static void ValidateAction(
            WkLoomThread thread,
            int index,
            LoomAction action,
            List<LoomDiagnostic> diagnostics) {
            if (action == null) {
                diagnostics.Add(LoomDiagnostic.Warning(
                    $"Thread '{thread.ResolvedParameterName}' has a null entry at action index {index}. " +
                    "Remove the row in the inspector.",
                    thread));
                return;
            }

            switch (action) {
                case ObjectToggleAction obj: ValidateObjectToggle(thread, index, obj, diagnostics); break;
                default:
                    diagnostics.Add(LoomDiagnostic.Error(
                        $"Thread '{thread.ResolvedParameterName}' action {index} is type " +
                        $"{action.GetType().Name}, which is not implemented at M1. " +
                        "Only ObjectToggleAction is supported in the M1 vertical slice.",
                        thread));
                    break;
            }
        }

        private static void ValidateObjectToggle(
            WkLoomThread thread,
            int index,
            ObjectToggleAction action,
            List<LoomDiagnostic> diagnostics) {
            if (action.target == null) {
                diagnostics.Add(LoomDiagnostic.Error(
                    $"Thread '{thread.ResolvedParameterName}' ObjectToggleAction {index} has no target. " +
                    "Set the GameObject the toggle should activate/deactivate.",
                    thread));
            }

            if (action.mode == ObjectToggleMode.Toggle) {
                diagnostics.Add(LoomDiagnostic.Error(
                    $"Thread '{thread.ResolvedParameterName}' ObjectToggleAction {index} uses mode " +
                    "Toggle, which is not implemented at M1. Use TurnOn or TurnOff. " +
                    "Toggle mode arrives in M2.",
                    thread));
            }
        }
    }
}
