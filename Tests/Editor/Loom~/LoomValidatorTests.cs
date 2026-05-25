// LoomValidatorTests.cs
//
// Coverage for the failure modes the design brief called out as silently-
// broken in VRCFury today: missing descriptor, empty parameter name,
// duplicate parameter names, broken action targets, unsupported action
// modes at M1.
//
// Each test sets up a tiny scene -- a GameObject acting as the avatar
// root (with or without VRCAvatarDescriptor), one or two child Threads --
// runs Discover + Validate, and asserts on the produced diagnostics.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using WhyKnot.AvatarQol.Loom;
using WhyKnot.AvatarQol.Loom.Pipeline;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class LoomValidatorTests {

        private readonly List<GameObject> _toDestroy = new List<GameObject>();

        [TearDown]
        public void TearDown() {
            foreach (var go in _toDestroy) {
                if (go != null) Object.DestroyImmediate(go);
            }
            _toDestroy.Clear();
        }

        [Test]
        public void Validate_NullDiscovery_ReportsError() {
            var diags = LoomValidator.Validate(null);
            AssertHasErrorContaining(diags, "No avatar selected");
        }

        [Test]
        public void Validate_MissingDescriptor_ReportsError() {
            var avatar = MakeAvatarRoot(withDescriptor: false);
            AddThread(avatar, "Outfit/Hat");
            var discovery = LoomDiscovery.Discover(avatar);
            var diags = LoomValidator.Validate(discovery);
            AssertHasErrorContaining(diags, "VRCAvatarDescriptor");
        }

        [Test]
        public void Validate_EmptyParamName_ReportsError() {
            var avatar = MakeAvatarRoot();
            AddThread(avatar, menuPath: "", explicitParam: "");
            var discovery = LoomDiscovery.Discover(avatar);
            var diags = LoomValidator.Validate(discovery);
            AssertHasErrorContaining(diags, "no menu path or explicit parameter name");
        }

        [Test]
        public void Validate_DuplicateParamNames_ReportsError() {
            var avatar = MakeAvatarRoot();
            AddThread(avatar, "Outfit/Hat");
            AddThread(avatar, "Outfit/Hat");
            var discovery = LoomDiscovery.Discover(avatar);
            var diags = LoomValidator.Validate(discovery);
            AssertHasErrorContaining(diags, "Two Threads compile to the same parameter name");
        }

        [Test]
        public void Validate_ExplicitParamOverridesMenuPath_AvoidsDuplicate() {
            var avatar = MakeAvatarRoot();
            AddThread(avatar, "Outfit/Hat");
            AddThread(avatar, "Outfit/Hat", explicitParam: "Hat2");
            var discovery = LoomDiscovery.Discover(avatar);
            var diags = LoomValidator.Validate(discovery);
            Assert.IsFalse(
                diags.Any(d => d.Severity == LoomDiagnosticSeverity.Error
                            && d.Message.Contains("Two Threads compile to the same parameter name")),
                "Distinct explicit param names should let two Threads share a menu path.");
        }

        [Test]
        public void Validate_NullObjectToggleTarget_ReportsError() {
            var avatar = MakeAvatarRoot();
            var thread = AddThread(avatar, "Outfit/Hat");
            thread.actions.Add(new ObjectToggleAction { target = null });
            var discovery = LoomDiscovery.Discover(avatar);
            var diags = LoomValidator.Validate(discovery);
            AssertHasErrorContaining(diags, "has no target");
        }

        [Test]
        public void Validate_ToggleMode_ReportsError_AtM1() {
            var avatar = MakeAvatarRoot();
            var thread = AddThread(avatar, "Outfit/Hat");
            var target = new GameObject("HatMesh");
            target.transform.SetParent(avatar.transform);
            _toDestroy.Add(target);
            thread.actions.Add(new ObjectToggleAction { target = target, mode = ObjectToggleMode.Toggle });
            var discovery = LoomDiscovery.Discover(avatar);
            var diags = LoomValidator.Validate(discovery);
            AssertHasErrorContaining(diags, "mode Toggle");
        }

        [Test]
        public void Validate_NonBoolKind_ReportsError_AtM1() {
            var avatar = MakeAvatarRoot();
            var thread = AddThread(avatar, "Outfit/Slider");
            thread.kind = ThreadKind.Float;
            var discovery = LoomDiscovery.Discover(avatar);
            var diags = LoomValidator.Validate(discovery);
            AssertHasErrorContaining(diags, "ThreadKind.Float");
        }

        [Test]
        public void Validate_WellFormedThread_NoErrors() {
            var avatar = MakeAvatarRoot();
            var thread = AddThread(avatar, "Outfit/Hat");
            var target = new GameObject("HatMesh");
            target.transform.SetParent(avatar.transform);
            _toDestroy.Add(target);
            thread.actions.Add(new ObjectToggleAction { target = target, mode = ObjectToggleMode.TurnOn });

            var discovery = LoomDiscovery.Discover(avatar);
            var diags = LoomValidator.Validate(discovery);
            Assert.IsFalse(
                diags.Any(d => d.Severity == LoomDiagnosticSeverity.Error),
                "Well-formed Thread should produce no errors. Got: " + string.Join("; ", diags.Select(d => d.Message)));
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private GameObject MakeAvatarRoot(bool withDescriptor = true) {
            var go = new GameObject("TestAvatar");
            _toDestroy.Add(go);
            if (withDescriptor) go.AddComponent<VRCAvatarDescriptor>();
            return go;
        }

        private WkLoomThread AddThread(GameObject avatarRoot, string menuPath, string explicitParam = "") {
            var child = new GameObject($"ThreadHolder_{avatarRoot.transform.childCount}");
            child.transform.SetParent(avatarRoot.transform);
            _toDestroy.Add(child);
            var thread = child.AddComponent<WkLoomThread>();
            thread.menuPath = menuPath;
            thread.explicitParamName = explicitParam;
            return thread;
        }

        private static void AssertHasErrorContaining(List<LoomDiagnostic> diags, string substring) {
            Assert.IsTrue(
                diags.Any(d => d.Severity == LoomDiagnosticSeverity.Error
                            && d.Message.IndexOf(substring, System.StringComparison.Ordinal) >= 0),
                $"Expected an Error diagnostic containing '{substring}'. " +
                "Got: " + string.Join(" | ", diags.Select(d => $"{d.Severity}: {d.Message}")));
        }
    }
}
