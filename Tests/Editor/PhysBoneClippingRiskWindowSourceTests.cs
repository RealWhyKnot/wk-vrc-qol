// PhysBoneClippingRiskWindowSourceTests.cs
//
// Cheap insurance against re-introducing the Auto Mesh Fixes coupling
// that was removed in this release. PhysBoneClippingRiskWindow used to
// hold a "Create mesh fix setup" workflow that auto-spawned
// AutoTightenToBody components; after removal the file must remain free
// of references to the deleted types. A regex over the file content
// catches an accidental re-add long before a manual code review would.

using System.IO;
using NUnit.Framework;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class PhysBoneClippingRiskWindowSourceTests {

        private const string RelativePath = "Editor/Tools/PhysBoneClippingRiskWindow.cs";

        [Test]
        public void NoReferencesToRemovedAutoMeshFixesTypes() {
            var packageRoot = LocatePackageRoot();
            var fullPath = Path.Combine(packageRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(fullPath), $"Expected to find {RelativePath} under {packageRoot}.");

            string text = File.ReadAllText(fullPath);
            string[] banned = {
                "AutoTightenToBody",
                "WhyKnotMeshFixController",
                "MeshFixWindow",
                "MeshFixBaker",
                "WhyKnot.AvatarQol.MeshFixes",
            };
            foreach (var token in banned) {
                Assert.IsFalse(text.Contains(token),
                    $"PhysBoneClippingRiskWindow.cs must not reference the removed Auto Mesh Fixes type '{token}'.");
            }
        }

        // Walks up from this test's source directory until it finds the
        // package's package.json (or hits the filesystem root). Keeps the
        // test independent of where the package is placed in the project.
        private static string LocatePackageRoot() {
            var dir = new DirectoryInfo(GetThisDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, "Could not locate the avatar-qol package root (no package.json found walking up).");
            return dir.FullName;
        }

        private static string GetThisDirectory(
                [System.Runtime.CompilerServices.CallerFilePath] string filePath = "") {
            return Path.GetDirectoryName(filePath);
        }
    }
}
