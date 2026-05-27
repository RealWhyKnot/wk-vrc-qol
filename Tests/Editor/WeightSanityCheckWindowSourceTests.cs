// WeightSanityCheckWindowSourceTests.cs

using System.IO;
using NUnit.Framework;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class WeightSanityCheckWindowSourceTests {

        [Test]
        public void IssueListSupportsSelectionButComponentIgnoresRows() {
            var packageRoot = LocatePackageRoot();
            string controls = ReadSource(packageRoot, "Editor/Tools/WeightSanityCheckWindow.Controls.cs");
            string rows = ReadSource(packageRoot, "Editor/Tools/WeightSanityCheckWindow.IssueRows.cs");
            string actions = ReadSource(packageRoot, "Editor/Tools/WeightSanityCheckWindow.Actions.cs");

            StringAssert.Contains("Select all", controls);
            StringAssert.Contains("Clear selection", controls);
            StringAssert.Contains("Preview selected", controls);
            StringAssert.Contains("Fix selected", controls);
            StringAssert.Contains("_selectedIssueIndices", rows);
            StringAssert.Contains("Row selection is ignored for this button", controls);
            StringAssert.Contains("Row selection ", actions);
            StringAssert.Contains("is ignored for the component", actions);
        }

        private static string LocatePackageRoot() {
            var dir = new DirectoryInfo(GetThisDirectory());
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "package.json"))) {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, "Could not locate the wk-vrc-qol package root.");
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
    }
}
