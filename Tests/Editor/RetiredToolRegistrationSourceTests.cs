// RetiredToolRegistrationSourceTests.cs
//
// Source-level guards for tools whose backend remains in tree but whose
// public Unity menu entries are intentionally disabled.

using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class RetiredToolRegistrationSourceTests {

        [TestCase("Editor/Tools/MeshSculpt/MeshSculptTool.cs")]
        [TestCase("Editor/Tools/WeightTransfer/WeightTransferTool.cs")]
        public void RetiredToolsDoNotRegisterMenuItems(string relativePath) {
            string text = ReadSource(LocatePackageRoot(), relativePath);
            var activeMenuItem = Regex.Match(text, @"(?m)^\s*\[MenuItem\s*\(");
            Assert.IsFalse(activeMenuItem.Success, $"{relativePath} must not register public menu entries.");
        }

        private static string ReadSource(string packageRoot, string relativePath) {
            var fullPath = Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(fullPath), $"Expected to find {relativePath} under {packageRoot}.");
            return File.ReadAllText(fullPath);
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
    }
}
