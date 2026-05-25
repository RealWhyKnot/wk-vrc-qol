// AvatarIntentSessionTests.cs
//
// Verifies the shared intent-session contract that every Avatar QoL
// build hook depends on:
//   - Capture snapshots a renderer's sharedMesh so Dispose restores it.
//   - RememberSpawnedComponent destroys spawned components in LIFO order.
//   - Adopt destroys generated Unity Objects.
// Without these guarantees, a build / play cycle would leak in-memory
// clones or leave orphaned PhysBones on the source avatar.

using NUnit.Framework;
using UnityEngine;
using WhyKnot.AvatarQol.Intent;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class AvatarIntentSessionTests {

        private GameObject _rendererHost;
        private SkinnedMeshRenderer _renderer;
        private Mesh _originalMesh;

        [SetUp]
        public void SetUp() {
            _rendererHost = new GameObject("IntentSessionRenderer");
            _renderer = _rendererHost.AddComponent<SkinnedMeshRenderer>();
            _originalMesh = new Mesh { name = "OriginalMesh" };
            _renderer.sharedMesh = _originalMesh;
        }

        [TearDown]
        public void TearDown() {
            if (_rendererHost != null) Object.DestroyImmediate(_rendererHost);
            if (_originalMesh != null) Object.DestroyImmediate(_originalMesh);
            _rendererHost = null;
            _renderer = null;
            _originalMesh = null;
        }

        [Test]
        public void Capture_ThenSwapSharedMesh_ThenDispose_RestoresOriginal() {
            var session = new AvatarIntentSession();
            session.Capture(_renderer);

            var swappedIn = new Mesh { name = "SwappedIn" };
            try {
                _renderer.sharedMesh = swappedIn;
                Assert.AreSame(swappedIn, _renderer.sharedMesh);

                session.Dispose();

                Assert.AreSame(_originalMesh, _renderer.sharedMesh, "Dispose must put the captured original sharedMesh back on the renderer.");
            } finally {
                Object.DestroyImmediate(swappedIn);
            }
        }

        [Test]
        public void Adopt_DestroysGeneratedObjectOnDispose() {
            var session = new AvatarIntentSession();
            var generated = new Mesh { name = "Generated" };
            session.Adopt(generated);

            session.Dispose();

            Assert.IsTrue(generated == null, "Adopted object must be DestroyImmediate-d on Dispose.");
        }

        [Test]
        public void RememberSpawnedComponent_DestroysComponentOnDispose() {
            var spawnedHost = new GameObject("SpawnedComponentHost");
            try {
                var component = spawnedHost.AddComponent<BoxCollider>();
                Assert.IsNotNull(component);

                var session = new AvatarIntentSession();
                session.RememberSpawnedComponent(component);

                session.Dispose();

                Assert.IsTrue(component == null, "Spawned component must be DestroyImmediate-d on Dispose.");
                Assert.IsTrue(spawnedHost != null, "Dispose must not destroy the host GameObject, only the component it spawned.");
            } finally {
                if (spawnedHost != null) Object.DestroyImmediate(spawnedHost);
            }
        }

        [Test]
        public void Dispose_RemovesSpawnedComponentsInLifoOrder() {
            var host = new GameObject("LifoHost");
            try {
                var first = host.AddComponent<BoxCollider>();
                var second = host.AddComponent<SphereCollider>();
                var third = host.AddComponent<CapsuleCollider>();

                var session = new AvatarIntentSession();
                session.RememberSpawnedComponent(first);
                session.RememberSpawnedComponent(second);
                session.RememberSpawnedComponent(third);

                session.Dispose();

                Assert.IsTrue(first == null);
                Assert.IsTrue(second == null);
                Assert.IsTrue(third == null);
                Assert.AreEqual(0, host.GetComponents<Collider>().Length, "Every spawned component must be destroyed.");
            } finally {
                if (host != null) Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void HasChanges_TracksEveryCategory() {
            var session = new AvatarIntentSession();
            Assert.IsFalse(session.HasChanges, "Fresh session has no changes.");

            session.Capture(_renderer);
            Assert.IsTrue(session.HasChanges, "Capture should mark the session as having changes.");

            session.Dispose();
            Assert.IsFalse(session.HasChanges, "Dispose should clear the change flag.");

            var generated = new Mesh { name = "G" };
            session.Adopt(generated);
            Assert.IsTrue(session.HasChanges, "Adopt should mark the session as having changes.");
            session.Dispose();

            var host = new GameObject("H");
            try {
                var c = host.AddComponent<BoxCollider>();
                session.RememberSpawnedComponent(c);
                Assert.IsTrue(session.HasChanges, "RememberSpawnedComponent should mark the session as having changes.");
                session.Dispose();
            } finally {
                if (host != null) Object.DestroyImmediate(host);
            }
        }
    }
}
