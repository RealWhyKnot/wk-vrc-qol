// LoomEmitter.cs
//
// Side-effecting consumer of a LoomPlan: builds a temp AnimatorController,
// VRCExpressionParameters, and VRCExpressionsMenu, then substitutes them
// into the avatar descriptor for the duration of the upload. Returns an
// EmitterReceipt the LoomBuildSession uses to restore the originals on
// IVRCSDKPostprocessAvatarCallback.
//
// Why temp project assets instead of in-memory ScriptableObjects: the SDK
// AssetBundle build resolves references transitively from the descriptor
// at upload time. In-memory ScriptableObjects without an AssetDatabase
// identity have historically been flaky for AssetBundle inclusion on some
// Unity versions; the temp-asset path is unambiguous and matches what
// VRCFury / NDMF have shipped against for years.
//
// Temp asset location: Assets/_LoomTemp/<guid>/{FX.controller, Parameters.asset, Menu.asset}
// Cleanup is owned by LoomBuildSession.Dispose. A SessionState marker
// catches the editor-crashed-mid-build case so the temp folder is reaped
// on next startup.
//
// Scope at M1: Loom OWNS the FX layer / expression params / expression
// menu slots during the build. If the user has authored content in those
// slots (or VRCFury / NDMF is also generating into them), Loom overwrites
// for this upload; the originals are restored after. Layering Loom on top
// of an existing FX controller is an M2 feature.

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace WhyKnot.AvatarQol.Loom.Pipeline {

    internal sealed class LoomEmitterReceipt {
        public string TempAssetFolder;
        public AnimatorController EmittedController;
        public VRCExpressionParameters EmittedParameters;
        public VRCExpressionsMenu EmittedMenu;

        public bool DescriptorPatched;
        public int FxLayerIndex = -1;
        public RuntimeAnimatorController OriginalController;
        public bool OriginalIsDefault;
        public VRCExpressionParameters OriginalParameters;
        public VRCExpressionsMenu OriginalMenu;
        public bool OriginalCustomizeAnimationLayers;
    }

    internal static class LoomEmitter {

        private const string TempRoot = "Assets/_LoomTemp";

        public static LoomEmitterReceipt Emit(LoomPlan plan, VRCAvatarDescriptor descriptor) {
            if (plan == null || descriptor == null) return null;

            var folder = AllocateTempFolder(descriptor.gameObject.name);
            var receipt = new LoomEmitterReceipt { TempAssetFolder = folder };

            receipt.EmittedController = BuildController(plan, folder);
            receipt.EmittedParameters = BuildParameters(plan, folder);
            receipt.EmittedMenu       = BuildMenu(plan, folder);

            PatchDescriptor(descriptor, receipt);
            AssetDatabase.SaveAssets();
            return receipt;
        }

        // -----------------------------------------------------------------
        // AnimatorController emission
        // -----------------------------------------------------------------

        private static AnimatorController BuildController(LoomPlan plan, string folder) {
            var controller = AnimatorController.CreateAnimatorControllerAtPath($"{folder}/FX.controller");
            // CreateAnimatorControllerAtPath seeds a "Base Layer" we don't
            // need; clear so Loom layers don't end up offset by an empty.
            controller.layers = new AnimatorControllerLayer[0];

            BuildAnimatorParameters(controller, plan);
            foreach (var plannedLayer in plan.Layers) {
                AddLayer(controller, plannedLayer);
            }
            return controller;
        }

        private static void BuildAnimatorParameters(AnimatorController controller, LoomPlan plan) {
            var list = new List<AnimatorControllerParameter>(controller.parameters);
            foreach (var p in plan.Parameters) {
                list.Add(new AnimatorControllerParameter {
                    name = p.Name,
                    type = ToAnimatorParameterType(p.Type),
                    defaultBool  = p.Type == PlannedParameterType.Bool  && p.DefaultValue >= 0.5f,
                    defaultFloat = p.Type == PlannedParameterType.Float ? p.DefaultValue : 0f,
                    defaultInt   = p.Type == PlannedParameterType.Int   ? Mathf.RoundToInt(p.DefaultValue) : 0,
                });
            }
            controller.parameters = list.ToArray();
        }

        private static AnimatorControllerParameterType ToAnimatorParameterType(PlannedParameterType t) {
            switch (t) {
                case PlannedParameterType.Float: return AnimatorControllerParameterType.Float;
                case PlannedParameterType.Int:   return AnimatorControllerParameterType.Int;
                default: return AnimatorControllerParameterType.Bool;
            }
        }

        private static void AddLayer(AnimatorController controller, PlannedLayer plannedLayer) {
            var stateMachine = new AnimatorStateMachine {
                name = plannedLayer.Name,
                hideFlags = HideFlags.HideInHierarchy,
            };
            AssetDatabase.AddObjectToAsset(stateMachine, controller);

            var stateNodes = new Dictionary<string, AnimatorState>();
            foreach (var s in plannedLayer.States) {
                var node = stateMachine.AddState(s.Name);
                node.writeDefaultValues = false;
                node.motion = BuildClip(s, controller);
                stateNodes[s.Name] = node;
            }

            if (!string.IsNullOrEmpty(plannedLayer.DefaultStateName)
                && stateNodes.TryGetValue(plannedLayer.DefaultStateName, out var defaultNode)) {
                stateMachine.defaultState = defaultNode;
            }

            foreach (var trans in plannedLayer.Transitions) {
                if (!stateNodes.TryGetValue(trans.FromState, out var fromNode)) continue;
                if (!stateNodes.TryGetValue(trans.ToState,   out var toNode))   continue;

                var transition = fromNode.AddTransition(toNode);
                transition.hasExitTime = false;
                transition.duration = 0f;
                transition.AddCondition(
                    trans.Mode == PlannedTransitionMode.If ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot,
                    0f,
                    trans.ConditionParameter);
            }

            var layer = new AnimatorControllerLayer {
                name = plannedLayer.Name,
                defaultWeight = 1f,
                stateMachine = stateMachine,
            };

            var layers = new List<AnimatorControllerLayer>(controller.layers) { layer };
            controller.layers = layers.ToArray();
        }

        private static AnimationClip BuildClip(PlannedState state, AnimatorController parentAsset) {
            var clip = new AnimationClip { name = state.Name };
            foreach (var b in state.Bindings) {
                var binding = new EditorCurveBinding {
                    path = b.RelativePath,
                    type = b.BindingType,
                    propertyName = b.PropertyName,
                };
                // Single-keyframe constant curve. Tangent values 0 keep
                // the value flat across the clip duration regardless of
                // sampling time -- the Animator never interpolates.
                var curve = new AnimationCurve(new Keyframe(0f, b.ConstantValue, 0f, 0f));
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }
            AssetDatabase.AddObjectToAsset(clip, parentAsset);
            return clip;
        }

        // -----------------------------------------------------------------
        // VRCExpressionParameters emission
        // -----------------------------------------------------------------

        private static VRCExpressionParameters BuildParameters(LoomPlan plan, string folder) {
            var asset = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            var entries = new List<VRCExpressionParameters.Parameter>();
            foreach (var p in plan.Parameters) {
                entries.Add(new VRCExpressionParameters.Parameter {
                    name = p.Name,
                    valueType = ToExpressionParamType(p.Type),
                    defaultValue = p.DefaultValue,
                    saved = p.PersistAcrossSessions,
                    networkSynced = p.NetworkSynced,
                });
            }
            asset.parameters = entries.ToArray();
            AssetDatabase.CreateAsset(asset, $"{folder}/Parameters.asset");
            return asset;
        }

        private static VRCExpressionParameters.ValueType ToExpressionParamType(PlannedParameterType t) {
            switch (t) {
                case PlannedParameterType.Float: return VRCExpressionParameters.ValueType.Float;
                case PlannedParameterType.Int:   return VRCExpressionParameters.ValueType.Int;
                default: return VRCExpressionParameters.ValueType.Bool;
            }
        }

        // -----------------------------------------------------------------
        // VRCExpressionsMenu emission
        // -----------------------------------------------------------------

        private static VRCExpressionsMenu BuildMenu(LoomPlan plan, string folder) {
            var root = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            AssetDatabase.CreateAsset(root, $"{folder}/Menu.asset");

            foreach (var item in plan.MenuItems) {
                var segments = SplitMenuPath(item.Path);
                if (segments.Count == 0) continue;
                var leafName = segments[segments.Count - 1];
                var parent = NavigateOrCreateSubmenu(root, segments, segments.Count - 1, folder);
                parent.controls.Add(new VRCExpressionsMenu.Control {
                    name = leafName,
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = item.ParameterName },
                    value = item.Value,
                    icon = item.Icon,
                });
                EditorUtility.SetDirty(parent);
            }
            return root;
        }

        private static List<string> SplitMenuPath(string path) {
            var output = new List<string>();
            if (string.IsNullOrEmpty(path)) return output;
            foreach (var segment in path.Split('/')) {
                if (!string.IsNullOrEmpty(segment)) output.Add(segment);
            }
            return output;
        }

        private static VRCExpressionsMenu NavigateOrCreateSubmenu(
            VRCExpressionsMenu root,
            List<string> segments,
            int segmentCount,
            string folder) {
            var current = root;
            for (int i = 0; i < segmentCount; i++) {
                var folderName = segments[i];
                var existing = FindSubmenuByName(current, folderName);
                if (existing != null) {
                    current = existing;
                    continue;
                }
                var child = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                child.name = folderName;
                AssetDatabase.AddObjectToAsset(child, root);
                current.controls.Add(new VRCExpressionsMenu.Control {
                    name = folderName,
                    type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                    subMenu = child,
                });
                EditorUtility.SetDirty(current);
                current = child;
            }
            return current;
        }

        private static VRCExpressionsMenu FindSubmenuByName(VRCExpressionsMenu parent, string name) {
            foreach (var c in parent.controls) {
                if (c.type == VRCExpressionsMenu.Control.ControlType.SubMenu
                    && c.subMenu != null
                    && c.name == name) {
                    return c.subMenu;
                }
            }
            return null;
        }

        // -----------------------------------------------------------------
        // Descriptor substitution
        // -----------------------------------------------------------------

        private static void PatchDescriptor(VRCAvatarDescriptor descriptor, LoomEmitterReceipt receipt) {
            receipt.OriginalCustomizeAnimationLayers = descriptor.customizeAnimationLayers;
            receipt.OriginalParameters = descriptor.expressionParameters;
            receipt.OriginalMenu       = descriptor.expressionsMenu;

            descriptor.customizeAnimationLayers = true;
            descriptor.expressionParameters = receipt.EmittedParameters;
            descriptor.expressionsMenu      = receipt.EmittedMenu;

            // CustomAnimLayer is a struct in VRC.SDK3 -- mutate a local
            // copy then write back through the array index.
            var layers = descriptor.baseAnimationLayers;
            for (int i = 0; i < layers.Length; i++) {
                if (layers[i].type != VRCAvatarDescriptor.AnimLayerType.FX) continue;
                receipt.FxLayerIndex = i;
                receipt.OriginalController = layers[i].animatorController;
                receipt.OriginalIsDefault  = layers[i].isDefault;

                var slot = layers[i];
                slot.animatorController = receipt.EmittedController;
                slot.isDefault = false;
                layers[i] = slot;
                break;
            }
            descriptor.baseAnimationLayers = layers;
            receipt.DescriptorPatched = true;
            EditorUtility.SetDirty(descriptor);
        }

        // -----------------------------------------------------------------
        // Temp folder allocation + restoration
        // -----------------------------------------------------------------

        private static string AllocateTempFolder(string avatarName) {
            EnsureFolder(TempRoot);
            var safeName = MakeSafeFolderName(avatarName);
            var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var subfolder = $"{TempRoot}/{safeName}_{stamp}";
            int suffix = 0;
            var candidate = subfolder;
            while (AssetDatabase.IsValidFolder(candidate)) {
                suffix++;
                candidate = $"{subfolder}_{suffix}";
            }
            Directory.CreateDirectory(candidate);
            AssetDatabase.Refresh();
            return candidate;
        }

        private static void EnsureFolder(string path) {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            var leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf)) {
                AssetDatabase.CreateFolder(parent, leaf);
            }
        }

        private static string MakeSafeFolderName(string raw) {
            if (string.IsNullOrEmpty(raw)) return "avatar";
            var chars = raw.ToCharArray();
            for (int i = 0; i < chars.Length; i++) {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_') chars[i] = '_';
            }
            return new string(chars);
        }

        // -----------------------------------------------------------------
        // Restoration (called from LoomBuildSession.Dispose)
        // -----------------------------------------------------------------

        public static void Restore(VRCAvatarDescriptor descriptor, LoomEmitterReceipt receipt) {
            if (descriptor == null || receipt == null || !receipt.DescriptorPatched) return;

            descriptor.customizeAnimationLayers = receipt.OriginalCustomizeAnimationLayers;
            descriptor.expressionParameters     = receipt.OriginalParameters;
            descriptor.expressionsMenu          = receipt.OriginalMenu;

            if (receipt.FxLayerIndex >= 0) {
                var layers = descriptor.baseAnimationLayers;
                if (receipt.FxLayerIndex < layers.Length) {
                    var slot = layers[receipt.FxLayerIndex];
                    slot.animatorController = receipt.OriginalController;
                    slot.isDefault = receipt.OriginalIsDefault;
                    layers[receipt.FxLayerIndex] = slot;
                    descriptor.baseAnimationLayers = layers;
                }
            }
            EditorUtility.SetDirty(descriptor);
        }

        public static void DeleteTempFolder(string folder) {
            if (string.IsNullOrEmpty(folder)) return;
            if (!AssetDatabase.IsValidFolder(folder)) return;
            AssetDatabase.DeleteAsset(folder);
        }
    }
}
