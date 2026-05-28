// PhysBonePresetWindow.PlanPreview.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

#if VRC_SDK_VRCSDK3
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class PhysBonePresetWindow {

        // -------- Plan preview - chain-grouped --------

        private static readonly SdkDefaults Defaults = new SdkDefaults();

        private void DrawPlanPreview() {
            EditorGUILayout.LabelField(
                new GUIContent("3. Review plan",
                    "What will be created if you click Apply. Nothing is written to the scene until then."),
                WkStyles.SubsectionTitle);
            var planRows = _plan != null
                ? _plan.PhysBones.Count + _plan.Colliders.Count + _plan.Notes.Count
                : 0;
            using (new EditorGUILayout.VerticalScope(
                    EditorStyles.helpBox,
                    GUILayout.Height(WkStyles.CappedListHeight(planRows, 20f, 120f, 280f)))) {
                _planScroll = EditorGUILayout.BeginScrollView(_planScroll);
                if (_plan == null || (_plan.PhysBones.Count == 0 && _plan.Colliders.Count == 0)) {
                    EditorGUILayout.LabelField(
                        SelectedPreset() == null ? "Pick a preset above." : "Selected preset produced no plan for this selection.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    EditorGUILayout.LabelField(
                        new GUIContent($"{_plan.PhysBones.Count} PhysBone(s)  •  {_plan.Colliders.Count} collider(s)",
                            "Summary of the entire plan. Each Chain section below shows its PhysBone parameters and the colliders it references."),
                        WkStyles.Muted);

                    if (_plan.Notes.Count > 0) {
                        foreach (var n in _plan.Notes)
                            EditorGUILayout.LabelField("• " + n, WkStyles.Muted);
                        EditorGUILayout.Space(2);
                    }

                    // Group plan PhysBones by chain (root Transform).
                    var pbByRoot = new Dictionary<Transform, PhysBoneSpec>();
                    foreach (var pb in _plan.PhysBones) if (pb.Root != null) pbByRoot[pb.Root] = pb;
                    foreach (var chain in _analysis.Chains) {
                        if (!pbByRoot.TryGetValue(chain.Root, out var pb)) continue;
                        DrawChainBlock(chain, pb);
                    }

                    // Orphan colliders (not referenced by any PhysBone in the plan).
                    var refdIndices = new HashSet<int>();
                    foreach (var pb in _plan.PhysBones) foreach (var idx in pb.ColliderRefs) refdIndices.Add(idx);
                    var orphans = new List<int>();
                    for (int i = 0; i < _plan.Colliders.Count; i++) if (!refdIndices.Contains(i)) orphans.Add(i);
                    if (orphans.Count > 0) {
                        EditorGUILayout.LabelField(
                            new GUIContent($"Orphan colliders ({orphans.Count})",
                                "Colliders the plan creates but doesn't attach to any PhysBone. Usually a preset bug; the colliders will exist in the scene but not collide with anything."),
                            EditorStyles.boldLabel);
                        foreach (var idx in orphans) {
                            var c = _plan.Colliders[idx];
                            EditorGUILayout.LabelField($"  [{idx}] {c.Name} on {PathUtility.GetGameObjectPath(c.AttachTo?.gameObject)}",
                                WkStyles.Mono);
                        }
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

        private void DrawChainBlock(BoneChain chain, PhysBoneSpec pb) {
            string rootPath = PathUtility.GetGameObjectPath(chain.Root?.gameObject);
            bool collapsed = _collapsedChainRoots.Contains(rootPath);
            string title = $"Chain — {chain.Root?.name} → {chain.Tip?.name}  ({chain.Bones.Count} bones, {chain.LengthMetres:F3} m)";
            bool now = EditorGUILayout.Foldout(!collapsed,
                new GUIContent(title, "Click to collapse / expand this chain's PhysBone details."),
                true, WkStyles.FoldoutHeader);
            if (now == collapsed) {
                if (now) _collapsedChainRoots.Remove(rootPath);
                else _collapsedChainRoots.Add(rootPath);
            }
            if (collapsed) return;

            using (new EditorGUILayout.VerticalScope()) {
                GUILayout.Space(2);
                EditorGUILayout.LabelField(
                    new GUIContent($"   PhysBone on {PathUtility.GetGameObjectPath(pb.Root?.gameObject)}",
                        "The GameObject that will receive a VRCPhysBone component."),
                    WkStyles.Mono);

                // Parameter table - value + bold "**" mark when not SDK default.
                DrawParamRow("pull",       pb.Pull,           Defaults.Pull,       PullHint(pb.Pull));
                DrawParamRow("spring",     pb.Spring,         Defaults.Spring,     SpringHint(pb.Spring));
                DrawParamRow("stiffness",  pb.Stiffness,      Defaults.Stiffness,  StiffHint(pb.Stiffness));
                DrawParamRow("gravity",    pb.Gravity,        Defaults.Gravity,    GravityHint(pb.Gravity));
                DrawParamRow("gravityFalloff", pb.GravityFalloff, Defaults.GravityFalloff,
                    "0–1; how concentrated gravity is at the chain tip vs the root. Higher = tip droops more, root stays put.");
                DrawParamRowEnum("immobileType", pb.ImmobileType.ToString(), "None",
                    "None / AllMotion / WorldRotation. WorldRotation makes the bone resist motion only when the avatar rotates — typical for ears so head turns don't whip them.");
                DrawParamRow("immobile",   pb.Immobile,       0f,
                    "0–1; strength of the immobile constraint above. 0.5 ≈ half-resist.");
                DrawParamRow("radius",     pb.Radius,         0f,
                    $"Capsule radius in metres (current: {pb.Radius:F3} m). Wider = more solid feel, more clipping with body.");
                DrawParamRowEnum("allowCollision", pb.AllowCollision.ToString(), "True",
                    "True / False / Other. Whether this PhysBone responds to PhysBoneColliders.");
                DrawParamRowEnum("allowGrabbing", pb.AllowGrabbing.ToString(), "True",
                    "True / False / Other. Whether VRChat users in-world can grab this bone.");
                DrawParamRowEnum("allowPosing", pb.AllowPosing.ToString(), "True",
                    "True / False / Other. Whether grabbed bones stay where you leave them when released.");

                if (pb.ColliderRefs.Count > 0) {
                    EditorGUILayout.LabelField(
                        new GUIContent("   Colliders attached:",
                            "Colliders this PhysBone will reference. Each row matches an entry from the plan's collider list."),
                        WkStyles.Mono);
                    foreach (var idx in pb.ColliderRefs) {
                        if (idx < 0 || idx >= _plan.Colliders.Count) continue;
                        var c = _plan.Colliders[idx];
                        EditorGUILayout.LabelField(
                            $"     [{idx}] {c.Name} on {PathUtility.GetGameObjectPath(c.AttachTo?.gameObject)}  ({c.Shape}, r={c.Radius:F3} h={c.Height:F3})",
                            WkStyles.Mono);
                    }
                }
                if (!string.IsNullOrEmpty(pb.Note)) {
                    EditorGUILayout.LabelField("   • " + pb.Note, WkStyles.Muted);
                }
                GUILayout.Space(4);
            }
        }

        private static void DrawParamRow(string name, float value, float sdkDefault, string hint) {
            bool diverges = !Mathf.Approximately(value, sdkDefault);
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField($"     {name}", WkStyles.Mono, GUILayout.Width(140));
                var style = diverges ? new GUIStyle(WkStyles.Mono) { fontStyle = FontStyle.Bold } : WkStyles.Mono;
                EditorGUILayout.LabelField(
                    new GUIContent($"{value:F3}{(diverges ? " **" : "")}",
                        diverges
                            ? $"Preset overrides the SDK default ({sdkDefault:F3}) → {value:F3}.\n\n{hint}"
                            : $"Matches the SDK default ({sdkDefault:F3}).\n\n{hint}"),
                    style, GUILayout.Width(80));
                EditorGUILayout.LabelField(new GUIContent(hint, hint), WkStyles.Muted);
            }
        }

        private static void DrawParamRowEnum(string name, string value, string sdkDefault, string hint) {
            bool diverges = value != sdkDefault;
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField($"     {name}", WkStyles.Mono, GUILayout.Width(140));
                var style = diverges ? new GUIStyle(WkStyles.Mono) { fontStyle = FontStyle.Bold } : WkStyles.Mono;
                EditorGUILayout.LabelField(
                    new GUIContent($"{value}{(diverges ? " **" : "")}",
                        diverges
                            ? $"Preset overrides the SDK default ({sdkDefault}) → {value}.\n\n{hint}"
                            : $"Matches the SDK default ({sdkDefault}).\n\n{hint}"),
                    style, GUILayout.Width(120));
                EditorGUILayout.LabelField(new GUIContent(hint, hint), WkStyles.Muted);
            }
        }

        // SDK default values - referenced by DrawParamRow for the bold-vs-default mark.
        private sealed class SdkDefaults {
            public readonly float Pull            = 0.2f;
            public readonly float Spring          = 0.5f;
            public readonly float Stiffness       = 0.4f;
            public readonly float Gravity         = 0f;
            public readonly float GravityFalloff  = 0f;
        }

        // Per-value hint text - translated to plain language for tooltips.
        private static string PullHint(float v) =>
            v < 0.15f ? $"pull = {v:F2}: low — chain swings freely"
            : v < 0.35f ? $"pull = {v:F2}: moderate — gentle return to rest"
            : $"pull = {v:F2}: stiff — snaps back quickly";
        private static string SpringHint(float v) =>
            v < 0.25f ? $"spring = {v:F2}: low oscillation"
            : v < 0.55f ? $"spring = {v:F2}: moderate bounce"
            : $"spring = {v:F2}: high bounce, springy feel";
        private static string StiffHint(float v) =>
            v < 0.25f ? $"stiffness = {v:F2}: floppy mid-chain"
            : v < 0.55f ? $"stiffness = {v:F2}: balanced"
            : $"stiffness = {v:F2}: rigid mid-chain";
        private static string GravityHint(float v) =>
            v < 0.05f ? $"gravity = {v:F2}: nearly weightless"
            : v < 0.20f ? $"gravity = {v:F2}: light droop"
            : $"gravity = {v:F2}: heavy droop";
    }
}
