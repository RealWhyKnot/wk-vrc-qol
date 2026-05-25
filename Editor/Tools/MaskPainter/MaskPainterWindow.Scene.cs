// MaskPainterWindow.Scene.cs

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class MaskPainterWindow {

        // ---- Scene view ----

        private void OnSceneGui(SceneView sv) {
            if (!_painting) return;
            if (_renderer == null) {
                Diag(LogLevel.Warn, "Renderer was destroyed during paint session; stopping.");
                StopPainting(prompt: false);
                return;
            }
            // Belt-and-braces: newly-opened scene views default
            // wantsMouseMove=false. Keep both flags asserted while painting.
            sv.wantsMouseMove = true;
            sv.wantsMouseEnterLeaveWindow = true;

            Event e = Event.current;
            int controlID = GUIUtility.GetControlID(FocusType.Passive);

            // Picking: re-raycast whenever the mouse position has changed.
            // Runs on Layout/Repaint too because mousePosition is always
            // current. The change-detection gate keeps cost down -- a
            // 50k-tri raycast costs ~1ms, fine at hover but wasteful 60Hz.
            if (e.mousePosition != _lastMousePos) {
                var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                bool prevHit = _hasHit;
                UpdateRaycast(ray);
                if (_hasHit != prevHit) {
                    Diag(LogLevel.Trace,
                        _hasHit
                            ? $"Raycast HIT at {_hitWorld} (was miss). mouse={e.mousePosition}"
                            : $"Raycast miss (was hit). mouse={e.mousePosition}");
                }
                if (_hasHit && !_firstHitLogged) {
                    _firstHitLogged = true;
                    Diag(LogLevel.Info,
                        $"First raycast hit of session: world={_hitWorld}, normal={_hitNormal}, snapshotBounds=({_snapshotWorldBounds.min}..{_snapshotWorldBounds.max}).");
                }
                _lastMousePos = e.mousePosition;
                sv.Repaint();
                // The UV crosshair / cursor-UV label in the Preview pane
                // tracks the same raycast, so repaint the window too.
                // Cheap: just a redraw of one IMGUI panel.
                if (_showUvCrosshair) Repaint();
            }

            // Repaint phase: draw overlays.
            if (e.type == EventType.Repaint) {
                DrawPreviewOverlay(sv);
                if (_hasHit) DrawBrushDisc(sv);
                if (_showSymmetryPlane && _symmetryEnabled) DrawSymmetryPlane(sv);
                if (_showSceneHud) DrawSceneHud(sv);
                if (_showHotkeyStrip) DrawHotkeyStrip(sv);
                if (!_hasHit) DrawOffMeshIndicator(sv, e.mousePosition);
            }

            switch (e.type) {
                case EventType.Layout:
                    HandleUtility.AddDefaultControl(controlID);
                    break;

                case EventType.MouseDown:
                    if (e.button == 0) {
                        Diag(LogLevel.Trace, $"MouseDown received. _hasHit={_hasHit}, mouse={e.mousePosition}");
                        // Dump a full ray / bake / bounds / submesh picture on
                        // the first click of every session. If the user is in
                        // the "every click misses" failure mode, this gives
                        // them one log line with every coordinate-space datum
                        // a triage needs -- no follow-up "can you log X" loop.
                        DumpFirstMouseDown(HandleUtility.GUIPointToWorldRay(e.mousePosition), e.mousePosition);
                        if (_hasHit) {
                            _strokeInProgress = true;
                            _strokeDispatches = 0;
                            PushUndo();
                            ApplyStroke();
                            e.Use();
                        }
                    }
                    break;

                case EventType.MouseDrag:
                    if (e.button == 0 && _strokeInProgress && _hasHit && CanDispatch()) {
                        ApplyStroke();
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (e.button == 0 && _strokeInProgress) {
                        _strokeInProgress = false;
                        _strokeCount++;
                        Diag(LogLevel.Trace, $"Stroke complete. dispatches in stroke={_strokeDispatches}.");
                        Repaint();
                        e.Use();
                    }
                    break;

                case EventType.KeyDown:
                    if (HandleHotkey(e)) e.Use();
                    break;

                case EventType.ScrollWheel:
                    if (_hasHit) {
                        // Wheel up = smaller, wheel down = larger (matches most paint apps).
                        float factor = e.delta.y > 0 ? 1.1f : 0.9f;
                        _radius = Mathf.Clamp(_radius * factor, 0.001f, 1f);
                        SaveEditorPrefs();
                        sv.Repaint();
                        Repaint();
                        e.Use();
                    }
                    break;
            }
        }

        private bool HandleHotkey(Event e) {
            switch (e.keyCode) {
                case KeyCode.LeftBracket:
                    _radius = Mathf.Clamp(_radius * 0.9f, 0.001f, 1f);
                    SaveEditorPrefs();
                    SceneView.RepaintAll();
                    Repaint();
                    return true;
                case KeyCode.RightBracket:
                    _radius = Mathf.Clamp(_radius * 1.1f, 0.001f, 1f);
                    SaveEditorPrefs();
                    SceneView.RepaintAll();
                    Repaint();
                    return true;
                case KeyCode.X:
                    _symmetryEnabled = !_symmetryEnabled;
                    SaveEditorPrefs();
                    SceneView.RepaintAll();
                    Repaint();
                    return true;
                case KeyCode.E:
                    _erase = !_erase;
                    SceneView.RepaintAll();
                    Repaint();
                    return true;
                case KeyCode.Z:
                    if (e.control || e.command) {
                        UndoLast();
                        return true;
                    }
                    return false;
            }
            return false;
        }

        private void UpdateRaycast(Ray ray) {
            _hasHit = false;
            if (_snapshotMesh == null || _snapshotWorldVerts == null) return;

            float bestT = float.PositiveInfinity;
            int bestI0 = 0, bestI1 = 0, bestI2 = 0;
            float bestU = 0f, bestV = 0f;

            var range = MaskPainterIO.SubmeshRange(_submeshIndex, _snapshotMesh.subMeshCount, WarnSubmeshDrift);
            int subStart = range.start;
            int subEnd   = range.end;

            for (int s = subStart; s < subEnd; s++) {
                var tris = _snapshotMesh.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3) {
                    int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                    if (MaskPainterIO.RayTriangle(ray.origin, ray.direction,
                            _snapshotWorldVerts[i0], _snapshotWorldVerts[i1], _snapshotWorldVerts[i2],
                            out float t, out float u, out float v)
                            && t < bestT) {
                        bestT = t;
                        bestI0 = i0; bestI1 = i1; bestI2 = i2;
                        bestU = u; bestV = v;
                    }
                }
            }
            if (bestT < float.PositiveInfinity) {
                _hasHit = true;
                _hitWorld = ray.origin + ray.direction * bestT;
                var e1 = _snapshotWorldVerts[bestI1] - _snapshotWorldVerts[bestI0];
                var e2 = _snapshotWorldVerts[bestI2] - _snapshotWorldVerts[bestI0];
                _hitNormal = Vector3.Cross(e1, e2).normalized;
                // The triangle's natural normal may face into the body if
                // the mesh has inverted winding. Flip toward the camera so
                // the brush disc and overlay sit on the correct side.
                if (Vector3.Dot(_hitNormal, ray.direction) > 0f) _hitNormal = -_hitNormal;

                // UV0 at the hit, interpolated from the hit triangle. The
                // shared mesh is the UV authority; the baked snapshot
                // doesn't carry UVs back through skinning. The triangle
                // indices match by construction (BakeMesh preserves the
                // index buffer), so the same i0/i1/i2 select the right
                // UV0 entries.
                _hitUv = ResolveHitUv(bestI0, bestI1, bestI2, bestU, bestV);
            }
        }

        private Vector2 ResolveHitUv(int i0, int i1, int i2, float u, float v) {
            var mesh = _renderer != null ? _renderer.sharedMesh : null;
            if (mesh == null) return Vector2.zero;
            var uvs = mesh.uv;
            if (uvs == null || uvs.Length == 0) return Vector2.zero;
            if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) return Vector2.zero;
            return MaskPainterIO.InterpolateUv(uvs[i0], uvs[i1], uvs[i2], u, v);
        }

        // ---- Scene drawing helpers ----

        private void DrawBrushDisc(SceneView sv) {
            var paintColor = _erase ? new Color(1f, 0.30f, 0.30f, 1f) : new Color(0.30f, 0.85f, 1f, 1f);
            var prev = Handles.color;

            // Outer ring (radius) - solid
            Handles.color = paintColor;
            Handles.DrawWireDisc(_hitWorld, _hitNormal, _radius);
            // Inner hardness ring - dotted feel via second pass at half alpha
            if (_hardness > 0.01f && _hardness < 0.99f) {
                Handles.color = new Color(paintColor.r, paintColor.g, paintColor.b, 0.45f);
                Handles.DrawWireDisc(_hitWorld, _hitNormal, _radius * _hardness);
            }
            // Center cross + dot
            Handles.color = paintColor;
            var size = HandleUtility.GetHandleSize(_hitWorld) * 0.04f;
            Handles.DrawSolidDisc(_hitWorld, _hitNormal, size * 0.4f);
            // Normal indicator (short stick out of the hit point)
            Handles.color = new Color(paintColor.r, paintColor.g, paintColor.b, 0.45f);
            Handles.DrawLine(_hitWorld, _hitWorld + _hitNormal * (_radius * 0.5f));

            // Mirror disc
            if (_symmetryEnabled) {
                var mirror = MaskPainterIO.MirrorAcrossLocalX(_hitWorld, _symmetryRoot);
                Handles.color = new Color(paintColor.r, paintColor.g, paintColor.b, 0.7f);
                Handles.DrawWireDisc(mirror, _hitNormal, _radius);
                if (_hardness > 0.01f && _hardness < 0.99f) {
                    Handles.color = new Color(paintColor.r, paintColor.g, paintColor.b, 0.3f);
                    Handles.DrawWireDisc(mirror, _hitNormal, _radius * _hardness);
                }
                Handles.color = new Color(paintColor.r, paintColor.g, paintColor.b, 0.45f);
                Handles.DrawDottedLine(_hitWorld, mirror, 4f);
            }
            Handles.color = prev;
        }

        private void DrawSymmetryPlane(SceneView sv) {
            if (_symmetryRoot == null) return;
            var fwd = _symmetryRoot.forward;
            var up  = _symmetryRoot.up;
            var ctr = _symmetryRoot.position;
            float size = Mathf.Max(0.5f, _snapshotWorldBounds.size.magnitude * 0.7f);
            var c00 = ctr + (-fwd - up) * (size * 0.5f);
            var c01 = ctr + (-fwd + up) * (size * 0.5f);
            var c11 = ctr + ( fwd + up) * (size * 0.5f);
            var c10 = ctr + ( fwd - up) * (size * 0.5f);
            var face = new Color(0.30f, 0.85f, 1f, 0.06f);
            var edge = new Color(0.30f, 0.85f, 1f, 0.55f);
            Handles.DrawSolidRectangleWithOutline(new[] { c00, c01, c11, c10 }, face, edge);
        }

        private void DrawPreviewOverlay(SceneView sv) {
            if (_previewMaterial == null || _maskRT == null || _snapshotMesh == null || _renderer == null) return;
            _previewMaterial.SetTexture("_MaskTex", _maskRT);
            _previewMaterial.SetVector("_ChannelMask", ChannelMaskVector());
            _previewMaterial.SetColor("_TintColor", TintColorForChannel());
            _previewMaterial.SetFloat("_TintAlpha", 0.55f);

            // Tiny scale-up plus shader Offset -1, -1 wins the depth fight
            // against the real SkinnedMeshRenderer without visible inflation.
            var m = _renderer.transform.localToWorldMatrix * Matrix4x4.Scale(new Vector3(1.001f, 1.001f, 1.001f));
            var range = MaskPainterIO.SubmeshRange(_submeshIndex, _snapshotMesh.subMeshCount, WarnSubmeshDrift);
            for (int s = range.start; s < range.end; s++) {
                Graphics.DrawMesh(_snapshotMesh, m, _previewMaterial, 0, sv.camera, s);
            }
        }

        // Sticky one-shot warning so the submesh-drift log doesn't fire
        // 60 times a second from OnSceneGui. Resets on Bake() so a fresh
        // pose snapshot can surface a new drift.
        private bool _submeshDriftWarned;
        private void WarnSubmeshDrift(string msg) {
            if (_submeshDriftWarned) return;
            _submeshDriftWarned = true;
            Diag(LogLevel.Warn, msg);
        }

        // Floating HUD top-left of the Scene view. Shows live tool state.
        private void DrawSceneHud(SceneView sv) {
            EnsureHudStyles();
            Handles.BeginGUI();
            try {
                const float w = 270f;
                const float h = 132f;
                var rect = new Rect(10, 10, w, h);
                GUI.Box(rect, GUIContent.none, _hudBoxStyle);
                float y = rect.y + 6;
                GUI.Label(new Rect(rect.x + 10, y, w - 20, 18),
                    "●  MASK PAINTER", _hudHeaderStyle);
                y += 20;
                GUI.Label(new Rect(rect.x + 10, y, w - 20, 16),
                    RendererSummary(), _hudLabelStyle);
                y += 18;
                string brushLine = $"brush  {_radius * 100f:0.0} cm  ·  str {_strength:0.00}  ·  hard {_hardness:0.00}";
                GUI.Label(new Rect(rect.x + 10, y, w - 20, 16), brushLine, _hudLabelStyle);
                y += 18;
                string modeLine = $"mode   {(_erase ? "ERASE" : "PAINT")}  ·  {(_mode == MaskMode.Grayscale ? "grayscale" : _channel.ToString() + " channel")}  ·  sym {(_symmetryEnabled ? "on" : "off")}";
                GUI.Label(new Rect(rect.x + 10, y, w - 20, 16), modeLine, _hudLabelStyle);
                y += 18;
                string statLine = $"strokes {_strokeCount}  ·  dispatches {_dispatchCount}  ·  {(_hasHit ? "ON MESH" : "off mesh")}";
                var statStyle = _hudLabelStyle;
                if (!_hasHit) {
                    statStyle = new GUIStyle(_hudLabelStyle) { normal = { textColor = new Color(0.95f, 0.65f, 0.20f) } };
                }
                GUI.Label(new Rect(rect.x + 10, y, w - 20, 16), statLine, statStyle);
                y += 18;
                if (_statsCoveragePct >= 0f) {
                    GUI.Label(new Rect(rect.x + 10, y, w - 20, 16), $"coverage {_statsCoveragePct:0.0}%", _hudHintStyle);
                }
            } finally {
                Handles.EndGUI();
            }
        }

        private void DrawHotkeyStrip(SceneView sv) {
            EnsureHudStyles();
            Handles.BeginGUI();
            try {
                var hint = "LMB paint   ·   [ / ]  size   ·   scroll  size   ·   X  symmetry   ·   E  erase   ·   Ctrl+Z  undo";
                var pos = sv.position;
                var rect = new Rect(0, pos.height - 28, pos.width, 22);
                GUI.Box(rect, GUIContent.none, _hudBoxStyle);
                var inset = new Rect(rect.x + 12, rect.y + 2, rect.width - 24, rect.height - 4);
                GUI.Label(inset, hint, _hudHintStyle);
            } finally {
                Handles.EndGUI();
            }
        }

        private void DrawOffMeshIndicator(SceneView sv, Vector2 mousePos) {
            EnsureHudStyles();
            Handles.BeginGUI();
            try {
                var rect = new Rect(mousePos.x + 14, mousePos.y + 6, 100, 18);
                var style = new GUIStyle(_hudHintStyle) {
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = new Color(0.95f, 0.65f, 0.20f, 0.95f) },
                };
                GUI.Label(rect, "(off mesh)", style);
            } finally {
                Handles.EndGUI();
            }
        }

        private void EnsureHudStyles() {
            if (_hudBoxStyle != null) return;
            _hudBoxStyle = new GUIStyle(GUI.skin.box) {
                normal = { background = MakeSolidTexture(new Color(0.08f, 0.08f, 0.08f, 0.78f)) },
            };
            _hudLabelStyle = new GUIStyle(EditorStyles.label) {
                normal = { textColor = new Color(0.92f, 0.92f, 0.92f, 1f) },
                fontSize = 11,
            };
            _hudHeaderStyle = new GUIStyle(_hudLabelStyle) {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.40f, 0.90f, 1f, 1f) },
            };
            _hudHintStyle = new GUIStyle(EditorStyles.miniLabel) {
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f, 0.95f) },
                fontSize = 10,
            };
        }

        private static Texture2D MakeSolidTexture(Color c) {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private Vector4 ChannelMaskVector() {
            if (_mode == MaskMode.Grayscale) return new Vector4(1, 0, 0, 0);
            switch (_channel) {
                case MaskChannel.R: return new Vector4(1, 0, 0, 0);
                case MaskChannel.G: return new Vector4(0, 1, 0, 0);
                case MaskChannel.B: return new Vector4(0, 0, 1, 0);
                case MaskChannel.A: return new Vector4(0, 0, 0, 1);
            }
            return new Vector4(1, 0, 0, 0);
        }

        private Color TintColorForChannel() {
            if (_mode == MaskMode.Grayscale) return new Color(1f, 0.55f, 0.10f, 1f); // orange
            switch (_channel) {
                case MaskChannel.R: return new Color(1f, 0.20f, 0.20f, 1f);
                case MaskChannel.G: return new Color(0.20f, 1f, 0.30f, 1f);
                case MaskChannel.B: return new Color(0.30f, 0.55f, 1f, 1f);
                case MaskChannel.A: return new Color(1f, 0.90f, 0.20f, 1f);
            }
            return Color.white;
        }
    }
}
