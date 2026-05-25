// WhyKnotBoneMergerIntentTests.cs
//
// Default-state contract for the BoneMerger intent component. The
// processInPlayMode / processOnUpload defaults are what the build hook
// reads -- changing them silently would alter behaviour for every
// avatar that already has the component, so the defaults are pinned by
// these tests.

using NUnit.Framework;
using UnityEngine;
using WhyKnot.AvatarQol.Components;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class WhyKnotBoneMergerIntentTests {

        private GameObject _host;

        [SetUp]
        public void SetUp() {
            _host = new GameObject("BoneMergerIntentHost");
        }

        [TearDown]
        public void TearDown() {
            if (_host != null) Object.DestroyImmediate(_host);
            _host = null;
        }

        [Test]
        public void Defaults_ProcessInPlayModeAndOnUpload_BothTrue() {
            var intent = _host.AddComponent<WhyKnotBoneMergerIntent>();
            Assert.IsTrue(intent.processInPlayMode, "Default for processInPlayMode must be true.");
            Assert.IsTrue(intent.processOnUpload, "Default for processOnUpload must be true.");
        }

        [Test]
        public void Defaults_DeleteAndReparentFlags_BothTrue() {
            var intent = _host.AddComponent<WhyKnotBoneMergerIntent>();
            Assert.IsTrue(intent.deleteMergedBones, "Default for deleteMergedBones must be true (destructive Apply preserves existing behaviour).");
            Assert.IsTrue(intent.reparentChildren, "Default for reparentChildren must be true.");
        }

        [Test]
        public void Defaults_VerboseLogIsFalse() {
            var intent = _host.AddComponent<WhyKnotBoneMergerIntent>();
            Assert.IsFalse(intent.verboseLog, "Default for verboseLog must be false (off unless the user opts in).");
        }

        [Test]
        public void Defaults_PairListIsEmptyAndNotNull() {
            var intent = _host.AddComponent<WhyKnotBoneMergerIntent>();
            Assert.IsNotNull(intent.pairs, "pairs list must be initialised so the inspector can edit it without an NRE.");
            Assert.AreEqual(0, intent.pairs.Count, "pairs list must start empty.");
        }
    }
}
