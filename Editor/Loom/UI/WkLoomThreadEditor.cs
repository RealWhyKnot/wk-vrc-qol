// WkLoomThreadEditor.cs
//
// Inspector for WkLoomThread. M1 surface:
//   * Menu Path + Explicit Parameter Name + the per-thread flags.
//   * Action list with one "Add ObjectToggleAction" button. Each row
//     uses the default property drawer; remove via the trash button.
//
// Custom [SerializeReference] list polish (drag-reorder, type picker
// dropdown, copy/paste) lands in M3 alongside the rest of the Loom UI.
// The list at M1 is functional but lean -- pick a button, fill the
// fields, build.

using UnityEditor;
using UnityEngine;
using WhyKnot.Core.Styling;

namespace WhyKnot.AvatarQol.Loom.UI {

    [CustomEditor(typeof(WkLoomThread))]
    [CanEditMultipleObjects]
    internal sealed class WkLoomThreadEditor : Editor {

        private SerializedProperty _menuPath;
        private SerializedProperty _kind;
        private SerializedProperty _defaultOn;
        private SerializedProperty _defaultValue;
        private SerializedProperty _persistAcrossSessions;
        private SerializedProperty _networkSynced;
        private SerializedProperty _explicitParamName;
        private SerializedProperty _icon;
        private SerializedProperty _actions;

        private bool _advancedOpen;

        private void OnEnable() {
            _menuPath              = serializedObject.FindProperty(nameof(WkLoomThread.menuPath));
            _kind                  = serializedObject.FindProperty(nameof(WkLoomThread.kind));
            _defaultOn             = serializedObject.FindProperty(nameof(WkLoomThread.defaultOn));
            _defaultValue          = serializedObject.FindProperty(nameof(WkLoomThread.defaultValue));
            _persistAcrossSessions = serializedObject.FindProperty(nameof(WkLoomThread.persistAcrossSessions));
            _networkSynced         = serializedObject.FindProperty(nameof(WkLoomThread.networkSynced));
            _explicitParamName     = serializedObject.FindProperty(nameof(WkLoomThread.explicitParamName));
            _icon                  = serializedObject.FindProperty(nameof(WkLoomThread.icon));
            _actions               = serializedObject.FindProperty(nameof(WkLoomThread.actions));
            _advancedOpen = SessionState.GetBool("WhyKnot.AvatarQol.LoomThread.AdvancedOpen", false);
        }

        public override void OnInspectorGUI() {
            using var _wkTheme = WkStyles.Scope(WkTheme.WhyKnot);
            serializedObject.Update();

            EditorGUILayout.PropertyField(_menuPath);
            EditorGUILayout.PropertyField(_defaultOn);
            EditorGUILayout.PropertyField(_persistAcrossSessions);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
            DrawActionsList();

            EditorGUILayout.Space(4);
            _advancedOpen = EditorGUILayout.Foldout(_advancedOpen,
                new GUIContent("Advanced",
                    "Parameter shape, sync, explicit name. The defaults are tuned for a saved, network-synced bool toggle; touch only when you need to deviate."),
                true);
            SessionState.SetBool("WhyKnot.AvatarQol.LoomThread.AdvancedOpen", _advancedOpen);
            if (_advancedOpen) {
                EditorGUILayout.PropertyField(_kind);
                if (_kind.enumValueIndex != (int)ThreadKind.Bool) {
                    EditorGUILayout.PropertyField(_defaultValue);
                }
                EditorGUILayout.PropertyField(_networkSynced);
                EditorGUILayout.PropertyField(_explicitParamName);
                EditorGUILayout.PropertyField(_icon);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawActionsList() {
            if (_actions.arraySize == 0) {
                EditorGUILayout.HelpBox(
                    "This Thread has no actions yet. Add one below.",
                    MessageType.Info);
            } else {
                for (int i = 0; i < _actions.arraySize; i++) {
                    DrawActionRow(i);
                }
            }

            EditorGUILayout.Space(2);
            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(new GUIContent("+ Object Toggle",
                        "Toggle a GameObject's active state when this Thread is on."))) {
                    AppendObjectToggleAction();
                }
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawActionRow(int index) {
            var element = _actions.GetArrayElementAtIndex(index);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                using (new EditorGUILayout.HorizontalScope()) {
                    var typeName = element.managedReferenceFullTypename;
                    var label = string.IsNullOrEmpty(typeName) ? $"Action {index}" : ShortTypeName(typeName);
                    EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("X", "Remove this action."), GUILayout.Width(22))) {
                        _actions.DeleteArrayElementAtIndex(index);
                        return;
                    }
                }
                EditorGUI.indentLevel++;
                var endProperty = element.GetEndProperty();
                var iterator = element.Copy();
                bool enterChildren = true;
                while (iterator.NextVisible(enterChildren) && !SerializedProperty.EqualContents(iterator, endProperty)) {
                    EditorGUILayout.PropertyField(iterator, true);
                    enterChildren = false;
                }
                EditorGUI.indentLevel--;
            }
        }

        private static string ShortTypeName(string managedReferenceFullTypename) {
            // managedReferenceFullTypename comes back as "AssemblyName FullTypeName";
            // we just want the leaf type for the label.
            var space = managedReferenceFullTypename.IndexOf(' ');
            var full = space >= 0 ? managedReferenceFullTypename.Substring(space + 1) : managedReferenceFullTypename;
            var dot = full.LastIndexOf('.');
            return dot >= 0 ? full.Substring(dot + 1) : full;
        }

        private void AppendObjectToggleAction() {
            _actions.arraySize++;
            var element = _actions.GetArrayElementAtIndex(_actions.arraySize - 1);
            element.managedReferenceValue = new ObjectToggleAction();
        }
    }
}
