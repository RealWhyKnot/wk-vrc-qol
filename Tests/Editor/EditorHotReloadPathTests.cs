// EditorHotReloadPathTests.cs
//
// Pure-function coverage for the path-classification helpers EditorHotReload
// uses to decide which file-watcher events trigger which behaviour. The
// FileSystemWatcher plumbing and AssetDatabase.ImportAsset side of the
// drainer is a live path and is verified by hand against the deployed
// painter (see project_mask_painter notes); these tests pin down the
// extension and root-resolution rules so a regression in the parsing layer
// fails loudly without needing a paint session.

using NUnit.Framework;
using WhyKnot.AvatarQol.Internal.HotReload;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class EditorHotReloadPathTests {

        // -------------------------------------------------------------------
        // IsTrackedExtension
        // -------------------------------------------------------------------

        [TestCase("Foo.cs",                    true)]
        [TestCase("Foo.asmdef",                true)]
        [TestCase("Foo.asmref",                true)]
        [TestCase("Foo.shader",                true)]
        [TestCase("Foo.compute",               true)]
        [TestCase("Foo.cginc",                 true)]
        [TestCase("Foo.hlsl",                  true)]
        [TestCase("Foo.CS",                    true)]    // case-insensitive
        [TestCase("Foo.SHADER",                true)]
        [TestCase("Foo.png",                   false)]
        [TestCase("Foo.meta",                  false)]
        [TestCase("Foo.txt",                   false)]
        [TestCase("Foo",                       false)]
        [TestCase("",                          false)]
        [TestCase(null,                        false)]
        public void IsTrackedExtension_RecognisesScriptsAndShaders(string path, bool expected) {
            Assert.AreEqual(expected, EditorHotReload.IsTrackedExtension(path));
        }

        // -------------------------------------------------------------------
        // IsShaderSource
        // -------------------------------------------------------------------

        [TestCase("Foo.shader",  true)]
        [TestCase("Foo.compute", true)]
        [TestCase("Foo.cginc",   true)]
        [TestCase("Foo.hlsl",    true)]
        [TestCase("Foo.HLSL",    true)]     // case-insensitive
        [TestCase("Foo.cs",      false)]
        [TestCase("Foo.asmdef",  false)]
        [TestCase("Foo.png",     false)]
        [TestCase("",            false)]
        [TestCase(null,          false)]
        public void IsShaderSource_RecognisesShaderRelatedExtensions(string path, bool expected) {
            Assert.AreEqual(expected, EditorHotReload.IsShaderSource(path));
        }

        // -------------------------------------------------------------------
        // IsRecompilableShaderAsset
        // -------------------------------------------------------------------

        [TestCase("Foo.shader",  true)]
        [TestCase("Foo.compute", true)]
        [TestCase("Foo.SHADER",  true)]
        [TestCase("Foo.cginc",   false)]    // include file, not a recompilable asset
        [TestCase("Foo.hlsl",    false)]    // include file, not a recompilable asset
        [TestCase("Foo.cs",      false)]
        [TestCase("",            false)]
        [TestCase(null,          false)]
        public void IsRecompilableShaderAsset_SeparatesShadersFromIncludes(string path, bool expected) {
            Assert.AreEqual(expected, EditorHotReload.IsRecompilableShaderAsset(path));
        }

        // -------------------------------------------------------------------
        // ResolveReimportRoot
        // -------------------------------------------------------------------

        // Files under Packages/<id>/... must resolve to Packages/<id>.
        [TestCase("Packages/dev.whyknot.wk-vrc-qol/Editor/Tools/MaskPainter/Shaders/UvSpaceBrush.shader", "Packages/dev.whyknot.wk-vrc-qol")]
        [TestCase("Packages/com.unity.something/Shaders/Foo.cginc", "Packages/com.unity.something")]
        // Backslashes in the input are normalised to forward slashes.
        [TestCase("Packages\\dev.whyknot.wk-vrc-qol\\Editor\\Includes\\Common.hlsl", "Packages/dev.whyknot.wk-vrc-qol")]
        // Plain "Packages/<file>" (no package id segment) falls back to the
        // Packages root.
        [TestCase("Packages/Loose.cginc", "Packages")]
        public void ResolveReimportRoot_FindsPackageRoot(string unityPath, string expected) {
            Assert.AreEqual(expected, EditorHotReload.ResolveReimportRoot(unityPath));
        }

        // Assets/ paths must NOT expand: a stray include edit in an avatar
        // project would otherwise reimport every third-party shader in the
        // project. The Reimport buttons in individual tools cover those cases.
        [TestCase("Assets/Shaders/Foo.cginc")]
        [TestCase("Assets/MyAvatar/Includes/Bar.hlsl")]
        [TestCase("Assets")]
        public void ResolveReimportRoot_SkipsAssetsTree(string unityPath) {
            Assert.IsNull(EditorHotReload.ResolveReimportRoot(unityPath));
        }

        [TestCase("")]
        [TestCase(null)]
        [TestCase("SomeOtherRoot/Foo.shader")]
        public void ResolveReimportRoot_NullsUnrecognisedRoots(string unityPath) {
            Assert.IsNull(EditorHotReload.ResolveReimportRoot(unityPath));
        }
    }
}
