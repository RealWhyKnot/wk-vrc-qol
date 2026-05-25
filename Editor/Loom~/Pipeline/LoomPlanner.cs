// LoomPlanner.cs
//
// Compose a LoomPlan from a discovery result. One Thread becomes one
// parameter, one menu item, and one layer with Off/On states + reciprocal
// transitions. The lowering is mechanical and per-Thread; cross-Thread
// rules ship in M2.
//
// Why the planner produces relative-path bindings instead of resolved
// AnimationClip objects: the Emitter owns asset lifetime (clips need to
// be parented under the cloned FX controller asset to ship in the upload).
// The Planner stays a pure function of (discovery -> plan) so it tests
// without touching the AssetDatabase.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.Loom.Pipeline {

    internal static class LoomPlanner {

        private const string OffStateName = "Off";
        private const string OnStateName  = "On";

        public static LoomPlan Plan(LoomDiscoveryResult discovery) {
            var plan = new LoomPlan();
            if (discovery == null || discovery.AvatarRoot == null) return plan;

            var avatarRoot = discovery.AvatarRoot.transform;

            foreach (var thread in discovery.Threads) {
                if (thread == null) continue;
                var paramName = thread.ResolvedParameterName;
                if (string.IsNullOrEmpty(paramName)) continue;

                plan.Parameters.Add(BuildParameter(thread, paramName));
                plan.MenuItems.Add(BuildMenuItem(thread, paramName));
                plan.Layers.Add(BuildLayer(thread, paramName, avatarRoot));
            }

            return plan;
        }

        private static PlannedParameter BuildParameter(WkLoomThread thread, string paramName) {
            return new PlannedParameter {
                Name = paramName,
                Type = PlannedParameterType.Bool,
                DefaultValue = thread.defaultOn ? 1f : 0f,
                NetworkSynced = thread.networkSynced,
                PersistAcrossSessions = thread.persistAcrossSessions,
            };
        }

        private static PlannedMenuItem BuildMenuItem(WkLoomThread thread, string paramName) {
            // Menu path defaults to the parameter name when the user only
            // set Explicit Parameter Name. That lands the menu item at the
            // top level rather than failing the build over an empty path.
            var menuPath = !string.IsNullOrEmpty(thread.menuPath) ? thread.menuPath : paramName;
            return new PlannedMenuItem {
                Path = menuPath,
                Type = PlannedMenuItemType.Toggle,
                ParameterName = paramName,
                Value = 1f,
                Icon = thread.icon,
            };
        }

        private static PlannedLayer BuildLayer(WkLoomThread thread, string paramName, Transform avatarRoot) {
            var layer = new PlannedLayer {
                Name = $"[Loom] {paramName}",
                DefaultStateName = thread.defaultOn ? OnStateName : OffStateName,
            };

            var offState = new PlannedState { Name = OffStateName };
            var onState  = new PlannedState { Name = OnStateName };
            BuildActionBindings(thread, avatarRoot, offState, onState);
            layer.States.Add(offState);
            layer.States.Add(onState);

            layer.Transitions.Add(new PlannedTransition {
                FromState = OffStateName,
                ToState   = OnStateName,
                ConditionParameter = paramName,
                Mode = PlannedTransitionMode.If,
            });
            layer.Transitions.Add(new PlannedTransition {
                FromState = OnStateName,
                ToState   = OffStateName,
                ConditionParameter = paramName,
                Mode = PlannedTransitionMode.IfNot,
            });

            return layer;
        }

        private static void BuildActionBindings(
            WkLoomThread thread,
            Transform avatarRoot,
            PlannedState offState,
            PlannedState onState) {
            foreach (var action in thread.actions) {
                if (action == null) continue;
                switch (action) {
                    case ObjectToggleAction obj: AddObjectToggleBindings(obj, avatarRoot, offState, onState); break;
                    // Other action types arrive in M2.
                }
            }
        }

        private static void AddObjectToggleBindings(
            ObjectToggleAction action,
            Transform avatarRoot,
            PlannedState offState,
            PlannedState onState) {
            if (action.target == null) return;
            var relativePath = GetRelativePath(avatarRoot, action.target.transform);
            if (relativePath == null) return;

            float onValue = action.mode == ObjectToggleMode.TurnOff ? 0f : 1f;
            float offValue = action.mode == ObjectToggleMode.TurnOff ? 1f : 0f;

            onState.Bindings.Add(MakeActiveBinding(relativePath, onValue));
            offState.Bindings.Add(MakeActiveBinding(relativePath, offValue));
        }

        private static PlannedClipBinding MakeActiveBinding(string relativePath, float value) {
            return new PlannedClipBinding {
                RelativePath = relativePath,
                BindingType = typeof(GameObject),
                PropertyName = "m_IsActive",
                ConstantValue = value,
            };
        }

        /// <summary>
        /// Return the dot-free, forward-slash path from <paramref name="avatarRoot"/>
        /// to <paramref name="target"/>. Returns null when the target is
        /// not under the root (caller skips such bindings; the validator
        /// will have surfaced the off-root reference separately when that
        /// check lands in M2).
        /// </summary>
        private static string GetRelativePath(Transform avatarRoot, Transform target) {
            if (target == null || avatarRoot == null) return null;
            if (target == avatarRoot) return string.Empty;

            var segments = new List<string>();
            var t = target;
            while (t != null && t != avatarRoot) {
                segments.Add(t.name);
                t = t.parent;
            }
            if (t != avatarRoot) return null;

            segments.Reverse();
            return string.Join("/", segments);
        }
    }
}
