// PhysBoneClippingRiskWindow.Preview.cs

using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class PhysBoneClippingRiskWindow {

        private void StartPreview(Transform bone) {
            if (bone == null) return;
            if (_previewBone == bone) return;
            StopPreview();
            _previewBone = bone;
            _previewRestRotation = bone.localRotation;
            _previewStart = EditorApplication.timeSinceStartup;
            Undo.RegisterCompleteObjectUndo(bone, "Avatar QoL PhysBone preview");
        }

        private void StopPreview() {
            if (_previewBone == null) {
                _previewBone = null;
                return;
            }
            _previewBone.localRotation = _previewRestRotation;
            _previewBone = null;
            SceneView.RepaintAll();
        }

        private void OnEditorUpdate() {
            if (_previewBone == null) {
                _previewBone = null;
                return;
            }
            float t = (float)(EditorApplication.timeSinceStartup - _previewStart);
            float angle = Mathf.Sin(t * Mathf.PI) * 30f;
            float zAngle = Mathf.Cos(t * Mathf.PI) * 18f;
            _previewBone.localRotation = _previewRestRotation * Quaternion.Euler(angle, 0f, zAngle);
            SceneView.RepaintAll();
        }

        private void OnSceneGui(SceneView sceneView) {
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
            Handles.color = new Color(1f, 0.62f, 0.15f, 0.95f);
            foreach (var issue in _issues) {
                if (issue.Renderer == null) continue;
                var size = HandleUtility.GetHandleSize(issue.WorldPosition) * 0.055f;
                Handles.SphereHandleCap(0, issue.WorldPosition, Quaternion.identity, size, EventType.Repaint);
                Handles.DrawLine(issue.WorldPosition, issue.NearestSurfacePosition);
            }
            Handles.color = prevColor;
        }
    }
}
