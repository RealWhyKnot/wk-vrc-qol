// WeightSanityCheckWindow.Scene.cs

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.WeightFixes;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class WeightSanityCheckWindow {

        // ------ Scene view gizmos -----------------------------------------

        private void OnSceneGui(SceneView sceneView) {
            // Reveal flash - fades out by alpha over the 2s window.
            if (_flashUntil > EditorApplication.timeSinceStartup) {
                float remaining = (float)(_flashUntil - EditorApplication.timeSinceStartup) / 2.0f;
                var prev = Handles.color;
                Handles.color = new Color(1f, 0.85f, 0.20f, Mathf.Clamp01(remaining));
                var size = HandleUtility.GetHandleSize(_flashPos) * 0.18f;
                Handles.DrawWireDisc(_flashPos, sceneView.camera.transform.forward, size);
                Handles.DrawWireDisc(_flashPos, sceneView.camera.transform.forward, size * 0.6f);
                Handles.color = prev;
                sceneView.Repaint();
            }
            if (!_showGizmos || _issues.Count == 0) return;
            var prevColor = Handles.color;
            Handles.color = new Color(1f, 0.25f, 0.25f, 0.95f);
            foreach (var i in _issues) {
                if (i.Renderer == null) continue;
                var size = HandleUtility.GetHandleSize(i.WorldPosition) * 0.04f;
                Handles.SphereHandleCap(0, i.WorldPosition, Quaternion.identity, size, EventType.Repaint);
            }
            Handles.color = prevColor;
        }
    }
}
