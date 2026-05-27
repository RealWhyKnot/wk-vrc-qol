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

        private static readonly string[] RelativePaths = {
            "Editor/Tools/PhysBoneClippingRiskWindow.cs",
            "Editor/Tools/PhysBoneClippingRiskWindow.Issues.cs",
        };

        [Test]
        public void NoReferencesToRemovedAutoMeshFixesTypes() {
            var packageRoot = LocatePackageRoot();
            string[] banned = {
                "AutoTightenToBody",
                "WhyKnotMeshFixController",
                "MeshFixWindow",
                "MeshFixBaker",
                "WhyKnot.AvatarQol.MeshFixes",
                "Auto Mesh Fixes (removed)",
                "ReduceMotion",
                "CanReduceMotion",
                "Reduce motion",
                "new GUIContent(\"Motion\"",
            };
            foreach (var relativePath in RelativePaths) {
                var fullPath = Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.IsTrue(File.Exists(fullPath), $"Expected to find {relativePath} under {packageRoot}.");

                string text = File.ReadAllText(fullPath);
                foreach (var token in banned) {
                    Assert.IsFalse(text.Contains(token),
                        $"{relativePath} must not reference removed PhysBone Clipping Risks action '{token}'.");
                }
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
            Assert.IsNotNull(dir, "Could not locate the wk-vrc-qol package root (no package.json found walking up).");
            return dir.FullName;
        }

        private static string GetThisDirectory(
                [System.Runtime.CompilerServices.CallerFilePath] string filePath = "") {
            return Path.GetDirectoryName(filePath);
        }
    }
}
