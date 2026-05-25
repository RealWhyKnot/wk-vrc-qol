// PhysBoneClippingAnalyzer.Reflection.cs

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

#if VRC_SDK_VRCSDK3
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Tools {

    internal static partial class PhysBoneClippingAnalyzer {

#if VRC_SDK_VRCSDK3
        private static bool HasMember(object source, params string[] names) {
            if (source == null) return false;
            var type = source.GetType();
            foreach (var name in names) {
                if (FindField(type, name) != null || FindProperty(type, name) != null) return true;
            }
            return false;
        }

        private static object GetMemberValue(object source, params string[] names) {
            if (source == null) return null;
            var type = source.GetType();
            foreach (var name in names) {
                var field = FindField(type, name);
                if (field != null) {
                    try {
                        return field.GetValue(source);
                    } catch {
                        return null;
                    }
                }

                var prop = FindProperty(type, name);
                if (prop != null) {
                    try {
                        return prop.GetValue(source, null);
                    } catch {
                        return null;
                    }
                }
            }
            return null;
        }

        private static FieldInfo FindField(Type type, string name) {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var t = type; t != null; t = t.BaseType) {
                foreach (var field in t.GetFields(flags)) {
                    if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)) return field;
                }
            }
            return null;
        }

        private static PropertyInfo FindProperty(Type type, string name) {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            for (var t = type; t != null; t = t.BaseType) {
                foreach (var prop in t.GetProperties(flags)) {
                    if (prop.GetIndexParameters().Length != 0) continue;
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) return prop;
                }
            }
            return null;
        }

        private static Transform ReadTransform(object source, params string[] names) {
            return ObjectToTransform(GetMemberValue(source, names));
        }

        private static HashSet<Transform> ReadTransformSet(object source, params string[] names) {
            return TransformEnumerableToSet(GetMemberValue(source, names));
        }

        private static HashSet<Transform> TransformEnumerableToSet(object value) {
            var output = new HashSet<Transform>();
            if (value == null) return output;

            var single = ObjectToTransform(value);
            if (single != null) {
                output.Add(single);
                return output;
            }

            if (value is string) return output;
            if (value is IEnumerable enumerable) {
                foreach (var item in enumerable) {
                    var t = ObjectToTransform(item);
                    if (t != null) output.Add(t);
                }
            }
            return output;
        }

        private static Transform ObjectToTransform(object value) {
            if (value == null) return null;
            if (value is Transform transform) return transform;
            if (value is GameObject go) return go.transform;
            if (value is Component component) return component.transform;
            return null;
        }

        private static float ReadFloat(object source, float fallback, params string[] names) {
            var value = GetMemberValue(source, names);
            if (value == null) return fallback;
            try {
                if (value is Vector3 vector) return vector.magnitude;
                return Convert.ToSingle(value);
            } catch {
                return fallback;
            }
        }

        private static bool HasWritableFloat(object source, params string[] names) {
            if (source == null) return false;
            var type = source.GetType();
            foreach (var name in names) {
                var field = FindField(type, name);
                if (field != null && !field.IsInitOnly && CanStoreFloat(field.FieldType)) return true;
                var prop = FindProperty(type, name);
                if (prop != null && prop.CanWrite && CanStoreFloat(prop.PropertyType)) return true;
            }
            return false;
        }

        private static bool TrySetFloat(object source, Func<float, float> adjust, params string[] names) {
            if (source == null || adjust == null) return false;
            var type = source.GetType();
            foreach (var name in names) {
                var field = FindField(type, name);
                if (field != null && !field.IsInitOnly && TryReadFloatValue(field.GetValue(source), out var fieldValue)) {
                    var next = adjust(fieldValue);
                    if (Mathf.Approximately(fieldValue, next)) return false;
                    try {
                        field.SetValue(source, ConvertFloatForType(next, field.FieldType));
                        return true;
                    } catch {
                        return false;
                    }
                }

                var prop = FindProperty(type, name);
                if (prop != null && prop.CanWrite && TryReadFloatValue(prop.GetValue(source, null), out var propValue)) {
                    var next = adjust(propValue);
                    if (Mathf.Approximately(propValue, next)) return false;
                    try {
                        prop.SetValue(source, ConvertFloatForType(next, prop.PropertyType), null);
                        return true;
                    } catch {
                        return false;
                    }
                }
            }
            return false;
        }

        private static bool TrySetBool(object source, bool next, params string[] names) {
            if (source == null) return false;
            var type = source.GetType();
            foreach (var name in names) {
                var field = FindField(type, name);
                if (field != null && !field.IsInitOnly && TrySetBoolMember(source, field, next)) return true;
                var prop = FindProperty(type, name);
                if (prop != null && prop.CanWrite && TrySetBoolMember(source, prop, next)) return true;
            }
            return false;
        }

        private static bool TrySetAdvancedBoolTrue(object source, params string[] names) {
            if (source == null) return false;
            var type = source.GetType();
            foreach (var name in names) {
                var field = FindField(type, name);
                if (field != null && !field.IsInitOnly && TrySetAdvancedBoolMember(source, field)) return true;
                var prop = FindProperty(type, name);
                if (prop != null && prop.CanWrite && TrySetAdvancedBoolMember(source, prop)) return true;
            }
            return false;
        }

        private static bool ReadBool(object source, bool fallback, params string[] names) {
            var value = GetMemberValue(source, names);
            if (value == null) return fallback;
            try {
                if (value is bool b) return b;
                if (value is int i) return i != 0;
                if (value is float f) return !Mathf.Approximately(f, 0f);
                var text = value.ToString();
                if (string.Equals(text, "True", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(text, "False", StringComparison.OrdinalIgnoreCase)) return false;
            } catch {
                return fallback;
            }
            return fallback;
        }

        private static bool ReadAdvancedBool(object source, bool fallback, params string[] names) {
            var value = GetMemberValue(source, names);
            if (value == null) return fallback;
            try {
                if (value is bool b) return b;
                if (value is int i) return i != 0;
                if (value is float f) return !Mathf.Approximately(f, 0f);
                var text = value.ToString();
                if (text.IndexOf("False", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                if (text.IndexOf("True", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (text.IndexOf("Self", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (text.IndexOf("Other", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            } catch {
                return fallback;
            }
            return fallback;
        }

        private static bool CanStoreFloat(Type type) {
            return type == typeof(float) || type == typeof(double) || type == typeof(int);
        }

        private static bool TryReadFloatValue(object value, out float output) {
            output = 0f;
            if (value == null) return false;
            try {
                output = Convert.ToSingle(value);
                return true;
            } catch {
                return false;
            }
        }

        private static object ConvertFloatForType(float value, Type type) {
            if (type == typeof(double)) return (double)value;
            if (type == typeof(int)) return Mathf.RoundToInt(value);
            return value;
        }

        private static bool TrySetBoolMember(object source, FieldInfo field, bool next) {
            var current = field.GetValue(source);
            var converted = ConvertBoolForType(next, field.FieldType);
            if (converted == null || ValuesEqual(current, converted)) return false;
            try {
                field.SetValue(source, converted);
                return true;
            } catch {
                return false;
            }
        }

        private static bool TrySetBoolMember(object source, PropertyInfo prop, bool next) {
            var current = prop.GetValue(source, null);
            var converted = ConvertBoolForType(next, prop.PropertyType);
            if (converted == null || ValuesEqual(current, converted)) return false;
            try {
                prop.SetValue(source, converted, null);
                return true;
            } catch {
                return false;
            }
        }

        private static bool TrySetAdvancedBoolMember(object source, FieldInfo field) {
            var current = field.GetValue(source);
            var converted = ConvertAdvancedBoolTrue(field.FieldType);
            if (converted == null || ValuesEqual(current, converted)) return false;
            try {
                field.SetValue(source, converted);
                return true;
            } catch {
                return false;
            }
        }

        private static bool TrySetAdvancedBoolMember(object source, PropertyInfo prop) {
            var current = prop.GetValue(source, null);
            var converted = ConvertAdvancedBoolTrue(prop.PropertyType);
            if (converted == null || ValuesEqual(current, converted)) return false;
            try {
                prop.SetValue(source, converted, null);
                return true;
            } catch {
                return false;
            }
        }

        private static object ConvertBoolForType(bool value, Type type) {
            if (type == typeof(bool)) return value;
            if (type == typeof(int)) return value ? 1 : 0;
            if (type == typeof(float)) return value ? 1f : 0f;
            if (type == typeof(double)) return value ? 1d : 0d;
            return null;
        }

        private static object ConvertAdvancedBoolTrue(Type type) {
            if (type == typeof(bool)) return true;
            if (type == typeof(int)) return 1;
            if (type == typeof(float)) return 1f;
            if (type == typeof(double)) return 1d;
            if (!type.IsEnum) return null;
            try {
                return Enum.Parse(type, "True", true);
            } catch {
                try {
                    return Enum.ToObject(type, 1);
                } catch {
                    return null;
                }
            }
        }

        private static bool ValuesEqual(object left, object right) {
            if (left == null || right == null) return left == right;
            if (TryReadFloatValue(left, out var leftFloat) && TryReadFloatValue(right, out var rightFloat)) {
                return Mathf.Approximately(leftFloat, rightFloat);
            }
            return left.Equals(right);
        }

        private static int CountObjectReferences(object value) {
            if (value == null) return 0;
            if (value is string) return 0;
            if (value is UnityEngine.Object unityObject) return unityObject != null ? 1 : 0;
            if (value is IEnumerable enumerable) {
                int count = 0;
                foreach (var item in enumerable) {
                    if (item == null) continue;
                    if (item is UnityEngine.Object itemObject && itemObject == null) continue;
                    count++;
                }
                return count;
            }
            return 1;
        }

        private static string GetTypeText(Type type) {
            var sb = new StringBuilder();
            for (var t = type; t != null; t = t.BaseType) {
                if (sb.Length > 0) sb.Append(" ");
                sb.Append(t.FullName ?? t.Name);
            }
            return sb.ToString();
        }

        private static string ToCm(float metres) {
            return (metres * 100f).ToString("0.0");
        }
#endif
    }
}
