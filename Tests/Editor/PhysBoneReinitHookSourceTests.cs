// PhysBoneReinitHookSourceTests.cs
//
// Source-level guard for the SDK PhysBone reinit call. The VRC SDK has
// shipped InitTransforms with a boolean parameter; invoking it as a
// no-argument method logs reflection parameter warnings on every PhysBone.

using System.IO;
using NUnit.Framework;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class PhysBoneReinitHookSourceTests {

        [Test]
        public void ReinitHookUsesCurrentSdkPhysBoneRefreshCalls() {
            string source = ReadSource("Editor/Common/PhysBoneReinitHook.cs");

            StringAssert.Contains("InitTransforms(true)", source);
            StringAssert.Contains("InitParameters()", source);
            StringAssert.Contains("UpdateShape()", source);
            Assert.IsFalse(source.Contains("Invoke(c, null)"), "PhysBone reinit must not use the old no-argument reflection call.");
        }

        private static string ReadSource(string relativePath) {
            var packageRoot = LocatePackageRoot();
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
