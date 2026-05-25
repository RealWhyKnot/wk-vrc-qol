// WeightFixSession.cs
//
// Per-pipeline-run lifetime for in-memory mesh clones created by
// WeightFixApplyHook. Same shape as AvatarIntentSession (per-renderer
// captured originals, deterministic Dispose) but kept private so a
// weight-fix run can fail without disturbing whatever generic intent
// runners have captured against the same avatar.
//
// Why HideFlags.DontSave on the clones: the documented compound
// (DontSaveInBuild | DontSaveInEditor | DontUnloadUnusedAsset) prevents
// the clone from being persisted into the scene file or being GC'd by
// the Editor while a play-mode session has it pinned on a renderer.
// Paired with explicit DestroyImmediate in Dispose, this gives a closed
// memory loop -- no clones leak to disk, none survive past session end.
//
// Why NOT Undo.RegisterCreatedObjectUndo: this hook fires at play-mode
// transition and at build, not in response to a user action. Pinning
// clones to the undo stack would balloon memory and let Ctrl+Z leave
// the editor in a half-restored state.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.WeightFixes {

    internal sealed class WeightFixSession : System.IDisposable {

        private readonly Dictionary<SkinnedMeshRenderer, RendererState> _states =
            new Dictionary<SkinnedMeshRenderer, RendererState>();
        private readonly List<Mesh> _clones = new List<Mesh>();

        public int CapturedRendererCount => _states.Count;
        public int CloneCount => _clones.Count;
        public bool HasChanges => _states.Count > 0;

        /// <summary>
        /// Snapshot the renderer's current sharedMesh so Dispose can put
        /// it back. No-op if the renderer is already captured.
        /// </summary>
        public void Capture(SkinnedMeshRenderer renderer) {
            if (renderer == null || _states.ContainsKey(renderer)) return;
            _states[renderer] = new RendererState(renderer);
        }

        /// <summary>
        /// Clone the renderer's current mesh, mark the clone DontSave, and
        /// track it for disposal. Caller is responsible for assigning it
        /// to the renderer (after weight fixes are applied).
        /// </summary>
        public Mesh CloneAndTrack(Mesh source) {
            if (source == null) return null;
            var clone = Object.Instantiate(source);
            clone.name = source.name + " (WeightFixed)";
            clone.hideFlags = HideFlags.DontSave;
            _clones.Add(clone);
            return clone;
        }

        public void Dispose() {
            foreach (var state in _states.Values) state.Restore();
            _states.Clear();

            for (int i = _clones.Count - 1; i >= 0; i--) {
                var clone = _clones[i];
                if (clone != null) Object.DestroyImmediate(clone);
            }
            _clones.Clear();
        }

        private sealed class RendererState {
            private readonly SkinnedMeshRenderer _renderer;
            private readonly Mesh _originalMesh;

            public RendererState(SkinnedMeshRenderer renderer) {
                _renderer = renderer;
                _originalMesh = renderer != null ? renderer.sharedMesh : null;
            }

            public void Restore() {
                if (_renderer == null) return;
                _renderer.sharedMesh = _originalMesh;
            }
        }
    }
}
