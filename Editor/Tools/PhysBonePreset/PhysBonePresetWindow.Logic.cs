// PhysBonePresetWindow.Logic.cs

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

        // ------ Logic --------------------------------------------------------

        private void RebuildAnalysis() {
            _analysis = BoneSelectionAnalysis.Build(_selection);
            _suggestionScores.Clear();
            _suggestionExplanations.Clear();
            foreach (var p in _presets) {
                try { _suggestionScores[p.Id] = p.SuggestionScore(_analysis); }
                catch { _suggestionScores[p.Id] = 0f; }
                try { _suggestionExplanations[p.Id] = new List<ScoringSignal>(p.ExplainScore(_analysis) ?? Array.Empty<ScoringSignal>()); }
                catch { _suggestionExplanations[p.Id] = new List<ScoringSignal>(); }
            }
            var suggestion = SuggestedPreset();
            if (string.IsNullOrEmpty(_selectedPresetId) || SelectedPreset() == null) {
                _selectedPresetId = suggestion?.Id;
            }
            // Selection changed -> drop any stale tweak snapshot.
            _tweakSnapshots = null;
            RebuildPlan();
        }

        private void RebuildPlan() {
            var preset = SelectedPreset();
            if (preset == null || _analysis == null) { _plan = null; return; }
            try { _plan = preset.BuildPlan(_analysis); }
            catch (Exception ex) {
                AvatarQolLogger.Instance.Exception(ex);
                _plan = null;
            }
        }

        private IPhysBonePreset SelectedPreset() {
            foreach (var p in _presets) if (p.Id == _selectedPresetId) return p;
            return null;
        }

        private IPhysBonePreset SuggestedPreset() {
            IPhysBonePreset best = null;
            float bestScore = -1f;
            foreach (var p in _presets) {
                if (!_suggestionScores.TryGetValue(p.Id, out var s)) s = 0;
                if (s > bestScore) { best = p; bestScore = s; }
            }
            return best;
        }

        private void ApplyPlan() {
            if (_plan == null) return;
            int created = PhysBonePlanApplier.Apply(_plan, out var error);
            if (created < 0) {
                EditorUtility.DisplayDialog("Apply PhysBone Preset",
                    "Apply failed; changes reverted.\n\n" + (error ?? "Unknown error."),
                    "OK");
                return;
            }
            AvatarQolLogger.Instance.Info($"Applied {_plan.PresetDisplayName} — {created} PhysBone(s), {_plan.Colliders.Count} collider(s).");
            // Capture the just-created components for the tweak strip.
            CaptureTweakSnapshots();
            if (_plan.PhysBones.Count > 0 && _plan.PhysBones[0].Root != null) {
                Selection.activeGameObject = _plan.PhysBones[0].Root.gameObject;
            }
            // Reset slider scalars; we're at 1.0x of the original values.
            _tweakPull = _tweakSpring = _tweakStiff = _tweakGravity = _tweakRadius = 1f;
            RebuildAnalysis();
        }

        private void CaptureTweakSnapshots() {
            _tweakSnapshots = new List<TweakSnapshot>();
#if VRC_SDK_VRCSDK3
            foreach (var spec in _plan.PhysBones) {
                if (spec.Root == null) continue;
                var components = spec.Root.GetComponents<VRCPhysBone>();
                if (components.Length == 0) continue;
                // Take the most recently added - last in the array.
                var pb = components[components.Length - 1];
                _tweakSnapshots.Add(new TweakSnapshot {
                    PhysBone = pb,
                    OriginalPull = pb.pull,
                    OriginalSpring = pb.spring,
                    OriginalStiffness = pb.stiffness,
                    OriginalGravity = pb.gravity,
                    OriginalRadius = pb.radius,
                });
            }
#endif
        }

        private void ApplyTweaks() {
#if VRC_SDK_VRCSDK3
            if (_tweakSnapshots == null) return;
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: tweak just-applied PhysBones");
            foreach (var snap in _tweakSnapshots) {
                if (snap.PhysBone == null) continue;
                Undo.RegisterCompleteObjectUndo(snap.PhysBone, "Tweak PhysBone");
                snap.PhysBone.pull      = snap.OriginalPull      * _tweakPull;
                snap.PhysBone.spring    = snap.OriginalSpring    * _tweakSpring;
                snap.PhysBone.stiffness = snap.OriginalStiffness * _tweakStiff;
                snap.PhysBone.gravity   = snap.OriginalGravity   * _tweakGravity;
                snap.PhysBone.radius    = snap.OriginalRadius    * _tweakRadius;
                EditorUtility.SetDirty(snap.PhysBone);
            }
            Undo.CollapseUndoOperations(undoGroup);
#endif
        }

        private sealed class TweakSnapshot {
#if VRC_SDK_VRCSDK3
            public VRCPhysBone PhysBone;
#else
            public UnityEngine.Object PhysBone;
#endif
            public float OriginalPull;
            public float OriginalSpring;
            public float OriginalStiffness;
            public float OriginalGravity;
            public float OriginalRadius;
        }
    }
}
