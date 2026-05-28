// ClippingFixerWindowSourceTests.cs
//
// Cheap insurance against re-introducing the removed Auto Mesh Fixes
// coupling. The current mesh clipping workflow owns its component and
// mesh write path directly; it should not reference the deleted mesh-fix
// setup types or the old motion-reduction action.

using System.IO;
using NUnit.Framework;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class ClippingFixerWindowSourceTests {

        private static readonly string[] RelativePaths = {
            "Editor/Tools/ClippingFixerWindow.cs",
            "Editor/Tools/ClippingFixerWindow.Issues.cs",
        };

        private static readonly string[] PhysBoneSourcePaths = {
            "Editor/Clipping/ClippingFixer.cs",
            "Editor/Clipping/ClippingFixer.Scan.cs",
            "Editor/Clipping/ClippingFixApplyHook.cs",
            "Editor/Tools/ClippingFixerWindow.Scan.cs",
            "Runtime/Clipping/WhyKnotClippingFixIntent.cs",
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
                        $"{relativePath} must not reference removed mesh clipping action '{token}'.");
                }
            }
        }

        [Test]
        public void ClippingFixerKeepsPhysBoneMotionPathWired() {
            var packageRoot = LocatePackageRoot();
            string core = ReadSources(
                packageRoot,
                "Editor/Clipping/ClippingFixer.cs",
                "Editor/Clipping/ClippingFixer.Scan.cs",
                "Editor/Clipping/ClippingFixer.Apply.cs");
            StringAssert.Contains("IssueKind.PhysBoneMotion", core);
            StringAssert.Contains("PhysBoneClippingAnalyzer.ScanOneMesh", core);
            StringAssert.Contains("IncludePhysBoneMotion", core);
            StringAssert.Contains("ApplyIssueWeightsToCurrentMesh", core);

            foreach (var relativePath in PhysBoneSourcePaths) {
                string text = ReadSource(packageRoot, relativePath);
                StringAssert.Contains("PhysBone", text, $"{relativePath} must keep the PhysBone motion warning path visible.");
            }
        }

        [Test]
        public void PhysBoneMotionFixDoesNotEditPhysBoneSettings() {
            var packageRoot = LocatePackageRoot();
            string core = ReadSources(
                packageRoot,
                "Editor/Clipping/ClippingFixer.cs",
                "Editor/Clipping/ClippingFixer.Scan.cs",
                "Editor/Clipping/ClippingFixer.Apply.cs");
            string hook = ReadSource(packageRoot, "Editor/Clipping/ClippingFixApplyHook.cs");
            string window = ReadSource(packageRoot, "Editor/Tools/ClippingFixerWindow.Issues.cs");

            StringAssert.Contains("SetBoneWeights", core);
            Assert.IsFalse(core.Contains("mesh.vertices ="));
            Assert.IsFalse(core.Contains("PhysBoneSourcesAdjusted"));
            Assert.IsFalse(hook.Contains("PhysBoneSourcesAdjusted"));
            Assert.IsFalse(window.Contains("adjust the matching PhysBone source settings"));
            Assert.IsFalse(File.Exists(Path.Combine(packageRoot, "Editor", "Tools", "PhysBoneClippingAnalyzer.MotionReduction.cs")));
        }

        [Test]
        public void SelectionAndPreviewControlsStayVisible() {
            var packageRoot = LocatePackageRoot();
            string window = ReadSource(packageRoot, "Editor/Tools/ClippingFixerWindow.cs");
            string issues = ReadSource(packageRoot, "Editor/Tools/ClippingFixerWindow.Issues.cs");

            StringAssert.Contains("new Vector2(720, 520)", window);
            StringAssert.Contains("new Vector2(1040f, 780f)", window);
            StringAssert.Contains("Stop wobble", issues);
            StringAssert.Contains("Add fix component", issues);
            StringAssert.Contains("Warning selection is only used by destructive apply", issues);
            StringAssert.Contains("Add component saves the current settings", issues);
            Assert.IsFalse(issues.Contains("Add component ("),
                "Add component must ignore row selection; selected rows are only for destructive apply.");
            Assert.IsFalse(issues.Contains("Add component also uses the current selection"),
                "The issue list must not claim the component uses selected rows.");
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

        private static string ReadSource(string packageRoot, string relativePath) {
            var fullPath = Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(fullPath), $"Expected to find {relativePath} under {packageRoot}.");
            return File.ReadAllText(fullPath);
        }

        private static string ReadSources(string packageRoot, params string[] relativePaths) {
            return string.Join("\n", System.Array.ConvertAll(
                relativePaths,
                relativePath => ReadSource(packageRoot, relativePath)));
        }
    }
}
