// MaskPainterWindow.Advanced.cs

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class MaskPainterWindow {

        private void DrawAdvancedSection() {
            _advancedOpen = EditorGUILayout.Foldout(_advancedOpen,
                new GUIContent("Advanced",
                    "Save options, scene overlay toggles, diagnostics. Defaults are right for most masks."),
                true, WkStyles.FoldoutHeader);
            if (!_advancedOpen) return;
            using (WkStyles.Section("Save options",
                    "How the saved PNG is post-processed and re-imported.")) {
                using (new EditorGUILayout.HorizontalScope()) {
                    bool prev = _dilateOnSave;
                    _dilateOnSave = EditorGUILayout.ToggleLeft(
                        new GUIContent("Dilate on save",
                            "Bleed painted pixels N steps outward into empty UV-island gutter at save time. Almost always wanted - prevents black halos when the shader bilinear-samples near a UV island edge."),
                        _dilateOnSave, GUILayout.Width(180));
                    if (prev != _dilateOnSave) SaveEditorPrefs();
                    using (new EditorGUI.DisabledScope(!_dilateOnSave)) {
                        int prevIter = _dilationIterations;
                        _dilationIterations = EditorGUILayout.IntSlider(_dilationIterations, 1, 16);
                        if (prevIter != _dilationIterations) SaveEditorPrefs();
                    }
                }
                bool prevSRGB = _sRGBOnSave;
                _sRGBOnSave = EditorGUILayout.ToggleLeft(
                    new GUIContent("Import as sRGB",
                        "Off for masks (this is data, not photographic colour). Turn on only if the mask will be used as an albedo / colour input."),
                    _sRGBOnSave);
                if (prevSRGB != _sRGBOnSave) SaveEditorPrefs();
            }
            using (WkStyles.Section("Scene overlay",
                    "Toggle the in-scene HUD, hotkey strip, and symmetry plane visualisation.")) {
                bool prevHud = _showSceneHud;
                _showSceneHud = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show HUD (top-left)",
                        "Floating panel in the Scene view showing target, brush settings, and live counters."),
                    _showSceneHud);
                if (prevHud != _showSceneHud) { SaveEditorPrefs(); SceneView.RepaintAll(); }

                bool prevHints = _showHotkeyStrip;
                _showHotkeyStrip = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show hotkey hint strip (bottom)",
                        "Floating bar at the bottom of the Scene view showing keyboard shortcuts."),
                    _showHotkeyStrip);
                if (prevHints != _showHotkeyStrip) { SaveEditorPrefs(); SceneView.RepaintAll(); }

                bool prevPlane = _showSymmetryPlane;
                _showSymmetryPlane = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show symmetry plane",
                        "Draw a faint quad at the symmetry root's local YZ plane so you can verify the mirror is where you expect."),
                    _showSymmetryPlane);
                if (prevPlane != _showSymmetryPlane) { SaveEditorPrefs(); SceneView.RepaintAll(); }
            }
            using (WkStyles.Section("Diagnostics",
                    "Visibility into the painting pipeline. Turn Verbose on if something isn't behaving and you want every state transition mirrored to the Console.")) {
                bool prev = _verboseLog;
                _verboseLog = EditorGUILayout.ToggleLeft(
                    new GUIContent("Verbose log (mirror trace to Console)",
                        "Off: trace lines go to the session log file only. On: they also appear in the Unity Console. State transitions (start, stop, save) always go to Console regardless."),
                    _verboseLog);
                if (prev != _verboseLog) SaveEditorPrefs();

                using (new EditorGUILayout.HorizontalScope()) {
                    if (GUILayout.Button(
                            new GUIContent("Dump state",
                                "Print the current tool state (target, baked snapshot, RT, undo stack, brush settings, counters) to the Unity console."),
                            GUILayout.Height(22))) {
                        DumpState();
                    }
                    if (GUILayout.Button(
                            new GUIContent("Open log folder",
                                "Open the per-package session log directory in the system file browser."),
                            GUILayout.Height(22))) {
                        OpenLogFolder();
                    }
                }
            }
        }
    }
}
