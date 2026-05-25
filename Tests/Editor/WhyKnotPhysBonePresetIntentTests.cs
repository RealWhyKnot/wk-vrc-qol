// WhyKnotPhysBonePresetIntentTests.cs
//
// Default-state contract for the PhysBonePreset intent component.
// processInPlayMode / processOnUpload defaults are what the build hook
// reads; tweak multipliers default to 1.0 so the recorded preset values
// pass through unchanged when the user has not adjusted them.

using NUnit.Framework;
using UnityEngine;
using WhyKnot.AvatarQol.Components;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class WhyKnotPhysBonePresetIntentTests {

        private GameObject _host;

        [SetUp]
        public void SetUp() {
            _host = new GameObject("PhysBonePresetIntentHost");
        }

        [TearDown]
        public void TearDown() {
            if (_host != null) Object.DestroyImmediate(_host);
            _host = null;
        }

        [Test]
        public void Defaults_ProcessInPlayModeAndOnUpload_BothTrue() {
            var intent = _host.AddComponent<WhyKnotPhysBonePresetIntent>();
            Assert.IsTrue(intent.processInPlayMode);
            Assert.IsTrue(intent.processOnUpload);
        }

        [Test]
        public void Defaults_TweakMultipliers_AllOne() {
            var intent = _host.AddComponent<WhyKnotPhysBonePresetIntent>();
            Assert.AreEqual(1f, intent.tweakPull);
            Assert.AreEqual(1f, intent.tweakSpring);
            Assert.AreEqual(1f, intent.tweakStiff);
            Assert.AreEqual(1f, intent.tweakGravity);
            Assert.AreEqual(1f, intent.tweakRadius);
        }

        [Test]
        public void Defaults_BoneListIsEmptyAndNotNull() {
            var intent = _host.AddComponent<WhyKnotPhysBonePresetIntent>();
            Assert.IsNotNull(intent.bones);
            Assert.AreEqual(0, intent.bones.Count);
        }

        [Test]
        public void Defaults_PresetIdIsNullOrEmpty() {
            var intent = _host.AddComponent<WhyKnotPhysBonePresetIntent>();
            // Field default is null for an uninitialised string; we accept
            // either null or empty so a future field-initialiser tweak does
            // not break this contract.
            Assert.IsTrue(string.IsNullOrEmpty(intent.presetId), "presetId must be unset by default; the window populates it on Add as Intent.");
        }
    }
}
