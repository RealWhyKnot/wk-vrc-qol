// LoomPlannerTests.cs
//
// Coverage for the discover -> plan lowering: one Thread compiles to one
// parameter + one menu item + one layer with Off/On states and reciprocal
// transitions; ObjectToggleAction modes route the constant value to the
// right state's binding.

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using WhyKnot.AvatarQol.Loom;
using WhyKnot.AvatarQol.Loom.Pipeline;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class LoomPlannerTests {

        private readonly List<GameObject> _toDestroy = new List<GameObject>();

        [TearDown]
        public void TearDown() {
            foreach (var go in _toDestroy) {
                if (go != null) Object.DestroyImmediate(go);
            }
            _toDestroy.Clear();
        }

        [Test]
        public void Plan_EmptyDiscovery_ReturnsEmptyPlan() {
            var plan = LoomPlanner.Plan(null);
            Assert.AreEqual(0, plan.Parameters.Count);
            Assert.AreEqual(0, plan.Layers.Count);
            Assert.AreEqual(0, plan.MenuItems.Count);
        }

        [Test]
        public void Plan_NoThreads_ReturnsEmptyPlan() {
            var avatar = MakeAvatar();
            var discovery = LoomDiscovery.Discover(avatar);
            var plan = LoomPlanner.Plan(discovery);
            Assert.AreEqual(0, plan.Parameters.Count);
            Assert.AreEqual(0, plan.Layers.Count);
            Assert.AreEqual(0, plan.MenuItems.Count);
        }

        [Test]
        public void Plan_OneThread_ProducesOneOfEach() {
            var avatar = MakeAvatar();
            BuildHatThread(avatar, ObjectToggleMode.TurnOn);

            var plan = LoomPlanner.Plan(LoomDiscovery.Discover(avatar));
            Assert.AreEqual(1, plan.Parameters.Count);
            Assert.AreEqual(1, plan.MenuItems.Count);
            Assert.AreEqual(1, plan.Layers.Count);
        }

        [Test]
        public void Plan_BoolParameter_DefaultsAndSyncFlagsCarryThrough() {
            var avatar = MakeAvatar();
            var thread = BuildHatThread(avatar, ObjectToggleMode.TurnOn);
            thread.defaultOn = true;
            thread.persistAcrossSessions = false;
            thread.networkSynced = false;

            var plan = LoomPlanner.Plan(LoomDiscovery.Discover(avatar));
            var p = plan.Parameters[0];
            Assert.AreEqual("Outfit/Hat", p.Name);
            Assert.AreEqual(PlannedParameterType.Bool, p.Type);
            Assert.AreEqual(1f, p.DefaultValue);
            Assert.IsFalse(p.PersistAcrossSessions);
            Assert.IsFalse(p.NetworkSynced);
        }

        [Test]
        public void Plan_ExplicitParamName_OverridesMenuPath() {
            var avatar = MakeAvatar();
            var thread = BuildHatThread(avatar, ObjectToggleMode.TurnOn);
            thread.explicitParamName = "Hat";

            var plan = LoomPlanner.Plan(LoomDiscovery.Discover(avatar));
            Assert.AreEqual("Hat", plan.Parameters[0].Name);
            // Menu path still routes the item under Outfit/Hat for the UI.
            Assert.AreEqual("Outfit/Hat", plan.MenuItems[0].Path);
            // Menu item refers to the param by its resolved name.
            Assert.AreEqual("Hat", plan.MenuItems[0].ParameterName);
        }

        [Test]
        public void Plan_Layer_HasOffAndOnStatesAndReciprocalTransitions() {
            var avatar = MakeAvatar();
            BuildHatThread(avatar, ObjectToggleMode.TurnOn);

            var plan = LoomPlanner.Plan(LoomDiscovery.Discover(avatar));
            var layer = plan.Layers[0];
            Assert.AreEqual("[Loom] Outfit/Hat", layer.Name);
            Assert.AreEqual(2, layer.States.Count);
            Assert.IsTrue(layer.States.Any(s => s.Name == "Off"));
            Assert.IsTrue(layer.States.Any(s => s.Name == "On"));

            Assert.AreEqual(2, layer.Transitions.Count);
            Assert.IsTrue(layer.Transitions.Any(
                t => t.FromState == "Off" && t.ToState == "On" && t.Mode == PlannedTransitionMode.If));
            Assert.IsTrue(layer.Transitions.Any(
                t => t.FromState == "On" && t.ToState == "Off" && t.Mode == PlannedTransitionMode.IfNot));
        }

        [Test]
        public void Plan_Layer_DefaultStateMatchesDefaultOn() {
            var avatar = MakeAvatar();
            var thread = BuildHatThread(avatar, ObjectToggleMode.TurnOn);

            thread.defaultOn = false;
            var planA = LoomPlanner.Plan(LoomDiscovery.Discover(avatar));
            Assert.AreEqual("Off", planA.Layers[0].DefaultStateName);

            thread.defaultOn = true;
            var planB = LoomPlanner.Plan(LoomDiscovery.Discover(avatar));
            Assert.AreEqual("On", planB.Layers[0].DefaultStateName);
        }

        [Test]
        public void Plan_ObjectToggleTurnOn_OnStateActivatesTarget() {
            var avatar = MakeAvatar();
            BuildHatThread(avatar, ObjectToggleMode.TurnOn);

            var plan = LoomPlanner.Plan(LoomDiscovery.Discover(avatar));
            var layer = plan.Layers[0];
            var onState  = layer.States.First(s => s.Name == "On");
            var offState = layer.States.First(s => s.Name == "Off");

            Assert.AreEqual(1, onState.Bindings.Count);
            Assert.AreEqual(1, offState.Bindings.Count);
            Assert.AreEqual(1f, onState.Bindings[0].ConstantValue,
                "TurnOn -> active=true while on.");
            Assert.AreEqual(0f, offState.Bindings[0].ConstantValue,
                "TurnOn -> active=false while off.");
            Assert.AreEqual("HatMesh", onState.Bindings[0].RelativePath);
            Assert.AreEqual(typeof(GameObject), onState.Bindings[0].BindingType);
            Assert.AreEqual("m_IsActive", onState.Bindings[0].PropertyName);
        }

        [Test]
        public void Plan_ObjectToggleTurnOff_InvertsConstants() {
            var avatar = MakeAvatar();
            BuildHatThread(avatar, ObjectToggleMode.TurnOff);

            var plan = LoomPlanner.Plan(LoomDiscovery.Discover(avatar));
            var layer = plan.Layers[0];
            var onState  = layer.States.First(s => s.Name == "On");
            var offState = layer.States.First(s => s.Name == "Off");

            Assert.AreEqual(0f, onState.Bindings[0].ConstantValue,
                "TurnOff -> active=false while on.");
            Assert.AreEqual(1f, offState.Bindings[0].ConstantValue,
                "TurnOff -> active=true while off.");
        }

        [Test]
        public void Plan_TwoThreads_ProduceTwoLayersAndParameters() {
            var avatar = MakeAvatar();
            BuildHatThread(avatar, ObjectToggleMode.TurnOn);
            BuildOtherThread(avatar);

            var plan = LoomPlanner.Plan(LoomDiscovery.Discover(avatar));
            Assert.AreEqual(2, plan.Layers.Count);
            Assert.AreEqual(2, plan.Parameters.Count);
            Assert.AreEqual(2, plan.MenuItems.Count);
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private GameObject MakeAvatar() {
            var go = new GameObject("TestAvatar");
            _toDestroy.Add(go);
            go.AddComponent<VRCAvatarDescriptor>();
            return go;
        }

        private WkLoomThread BuildHatThread(GameObject avatar, ObjectToggleMode mode) {
            var holder = new GameObject("ThreadHolder");
            holder.transform.SetParent(avatar.transform);
            _toDestroy.Add(holder);
            var thread = holder.AddComponent<WkLoomThread>();
            thread.menuPath = "Outfit/Hat";

            var target = new GameObject("HatMesh");
            target.transform.SetParent(avatar.transform);
            _toDestroy.Add(target);

            thread.actions.Add(new ObjectToggleAction { target = target, mode = mode });
            return thread;
        }

        private WkLoomThread BuildOtherThread(GameObject avatar) {
            var holder = new GameObject("OtherHolder");
            holder.transform.SetParent(avatar.transform);
            _toDestroy.Add(holder);
            var thread = holder.AddComponent<WkLoomThread>();
            thread.menuPath = "Outfit/Boots";

            var target = new GameObject("BootMesh");
            target.transform.SetParent(avatar.transform);
            _toDestroy.Add(target);

            thread.actions.Add(new ObjectToggleAction { target = target, mode = ObjectToggleMode.TurnOn });
            return thread;
        }
    }
}
