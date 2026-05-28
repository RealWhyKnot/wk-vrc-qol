# Changelog

All notable changes to this project will be documented in this file. Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning follows [Semantic Versioning](https://semver.org/).

<!-- Entries under "## Unreleased" are appended automatically by the changelog-append GitHub
     workflow on every push to main, then promoted to the versioned section by release.yml when
     a tag is cut. Don't hand-edit Unreleased -- your edits will be overwritten on the next push.
     To override an entry, amend the commit subject before merge. -->

## Unreleased

_No notable changes since the last release._

---

## [v1.4.0-beta.4](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.4.0-beta.4) -- 2026-05-28

### Changed
- **clipping:** Split scan and apply internals (7378671)
- **uv:** Split transfer geometry and raster helpers (0c24ac5)
- **cleanup:** Remove retired avatar tool code (f8eb7ec)

### Fixed
- **core:** Refresh bundled editor internals (beec500)

---

## [v1.4.0-beta.3](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.4.0-beta.3) -- 2026-05-28

### Added
- **ui:** Fit editor tool windows (12e1cbf)
- **intent:** Cache fixer precompute (19a7f67)
- **clipping:** Include physbone motion fixes (d5d9cc3)
- **physbone:** Add clipping fixer (7a7dd67)

### Fixed
- **ui:** Remove animated title chrome (155ac6c)
- **clipping:** Reweight clipping fixes (30d2733)
- **clipping:** Move physbone risks by mesh (b077085)
- **physbone:** Refresh reinit with sdk signature (298ba07)
- **clipping:** Separate physbone motion fixes (a726b9d)

---

## [v1.4.0-beta.2](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.4.0-beta.2) -- 2026-05-27

### Fixed
- **ui:** Retire mesh sculpt and weight transfer menu entries (eb29e54)

---

## [v1.4.0-beta.1](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.4.0-beta.1) -- 2026-05-27

### Added
- **weight-transfer:** Add surface weight transfer (88105a0)
- **mesh-sculpt:** Add generated mesh sculptor (465d6af)
- **ui:** Add WhyKnot logo chrome (bc0abb4)
- **uv-transfer:** Add anti-aliased bake padding (46435b4)
- **ui:** Add responsive branded editor chrome (f6cdb1c)

### Fixed
- **ui:** Simplify window chrome (480f449)
- **uv-transfer:** Use projected source correspondence (a7f6d5b)

---

## [v1.3.0](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.3.0) -- 2026-05-27

### Added
- **uv-transfer:** Mesh-to-mesh texture rebake tool (3739848)
- **mask-painter:** UV map preview pane plus raycast snapshot fixes (cf1bbb6)
- **hot-reload:** Auto-reimport shaders on file change (1b8a6cd)

### Changed
- **uv-transfer:** Parallel core + flat spatial grid + AABB early-out (c30e34b)

### Fixed
- **mask-painter:** Brush no longer stamps camera-visible UV region (e22cd8e)
- **scripts:** Wait for non-empty NUnit results file, not just existence (55be8fd)
- **scripts:** Bump test results flush poll to 60 seconds (83d3146)
- **scripts:** Drop -quit from batch test runner; wait for results flush (db4162c)
- **mask-painter:** Render preview on depth-hidden mesh (c1c4d9f)

---

## [v1.2.1](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.2.1) -- 2026-05-25

### Fixed
- **Hot reload status menu collision**: `WkHotReloadStatus.cs` carried a `[MenuItem("Window/WhyKnot/Hot Reload Status")]` attribute that Unity refused on a second registration ("a menu item with the same name already exists") whenever both vrcfury-qol and avatar-qol were installed in the same project -- their two synced copies of the file both tried to register the identical menu path. The attribute moves out of the synced source file; this package now wires its own `Window/WhyKnot/Avatar QoL/Hot Reload Status` entry from non-synced code so the two packages stop racing for the path.

---

## [v1.2.0](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.2.0) -- 2026-05-25

### Added
- **Window / WhyKnot / Avatar QoL / Logs**: per-package session-log viewer with level filter chips (Debug / Info / Warning / Error), free-text search, live tail via `FileSystemWatcher`, and Open in Explorer buttons. Tabbed by every WkLogger registered with this package's registry.
- **Project Settings / WhyKnot / Avatar QoL**: Console mirror toggles per registered logger, default theme override (WhyKnot / VRCFury), hot-reload watcher enable toggle that takes effect on next Editor startup, and an Optional integrations status panel showing which versionDefine symbols (WK_NDMF, WK_VRC_SDK_AVATARS) are active.
- **Window / WhyKnot / Hot Reload Status**: refresh counter, last compile result, recent file events, Open Log button, watcher-enabled toggle.

### Changed
- Bundled `Editor/Internal/` refreshed from the wk-core 1.2.0 surface. The 1.2.0 source ships an NDMF-first build pipeline (`WkAvatarPass<TSession>` + `WkAvatarPipeline.Register` routing through NDMF when installed and through four phase-bucket `IVRCSDKPreprocessAvatarCallback` implementations as a fallback), a three-tier generated-asset scope (Temporary / Session / Persistent), an in-house animator-builder (`WkAac.For` returning a fluent `IWkAnimatorBuilder`; no third-party AAC dependency), a `WkAvatarPreviewSession` with original<->proxy mapping + force-reset + crash recovery, UI Toolkit primitive set + USS theme stylesheets, a buffered `WkLogger` writer with full lifecycle envelope, and the hot-reload watcher now covers `.asmdef` + `.asmref` changes with `InternalBufferOverflowException` recovery.
- Editor asmdef gains `nadena.dev.ndmf` in references and declares `WK_NDMF` + `WK_VRC_SDK_AVATARS` versionDefines so the bundled NDMF bridge + SDK-coupled fallback in `WkAvatarPipeline` compile in. NDMF reference is warn-tolerated when absent.
- Existing tool code keeps the prior API surface; subsequent feature work picks up the new helpers incrementally.

---

## [v1.2.0-beta.2](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.2.0-beta.2) -- 2026-05-25

### Breaking
- Package ID renamed from `dev.whyknot.avatar-qol` to `dev.whyknot.wk-vrc-qol`. GitHub repo renamed from `RealWhyKnot/vrc-avatar-qol` to `RealWhyKnot/wk-vrc-qol`. VCC has no in-place upgrade path between different package IDs -- remove the old package and add the new one. Menu entries move from `Tools/WhyKnot/vrc-avatar-qol/...` to `Tools/WhyKnot/wk-vrc-qol/...` (same for `GameObject/WhyKnot/wk-vrc-qol/...` right-click entries). Mask Painter EditorPrefs keys are re-prefixed too, so the brush radius / strength / symmetry / etc. user settings reset to defaults on first use after upgrade.

### License
- `LICENSE` now carries a GPL-3.0 Section 7 author-reservation clause: the copyright holder (WhyKnot) reserves the right to incorporate this software into closed-source works distributed by the copyright holder, in particular VRChat avatar uploads. Recipients' GPL rights and obligations are unchanged.

---

## [v1.2.0-beta.1](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.2.0-beta.1) -- 2026-05-25

### Changed
- Bundled `Editor/Internal/` refreshed from the wk-core 1.2.0 source. Picks up an NDMF-`ErrorReport`-shaped scope stack on top of `WkLogger` (`WkLogContext`, `BeginTask`, `InfoBlock` / `WarningBlock` / `ErrorBlock`); new utility helpers (`MeshUtility`, `BlendShapeUtility`, `FolderUtility`, `UndoUtility`); new reflection helpers (`WkReflection`, `WkReflectionCache`, `WkGlobalId`, `WkJsonClone`); an `EditorApplication.update` ticker (`WkEditorTicker`) and a typed `EditorPrefs` wrapper (`WkEditorPrefs` + `WkSessionState`); a `WkToolWindow` / `WkInspectorEditor` / `WkMenuPaths` scaffolding tier; thirteen new `WkStyles` primitives (`SubtleDivider`, `Foldout`, `TwoColumn`, `SearchField`, `TabBar`, `ProgressBar`, `ObjectFieldRow`, `DangerButtonInline`, `SecondaryButtonInline`, `StatusBanner`, `Checker`, `RectBorder`, `TitleBar`) and four new `GUIStyle`s (`Caption`, `Code`, `TitleBarStyle`, `RowAlt`); a broadened theme palette (`DividerSubtle`, `BackgroundEmphasis`, `ButtonHover`) and a `NoticeKind.Danger` value; and theme-routed `EditorElementWalker` chrome that reads from `WkStyles.Current` instead of baking palette literals. The local `Editor/Common/BlendShapeUtility.cs` is now duplicated by the bundled copy at `Editor/Internal/Utilities/BlendShapeUtility.cs` -- callers still reference the local copy; a follow-up will drop the local and redirect imports. No user-visible behaviour change in this version.
- Split the five large editor windows into per-concern partial classes (062053c)

---

## [v1.1.1](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.1.1) -- 2026-05-25

### Added
- **logging:** Every diagnostic line in this package now routes through `AvatarQolLogger.Instance` (the package's registered `WkLogger`). Sessions are written to `%LocalAppData%/WhyKnot/Logs/dev.whyknot.wk-vrc-qol/session-<timestamp>.log`, capped at 3 retained sessions per package. Each line carries a level tag, source file:line, calling method, and message. Info, Warning, and Error mirror to the Unity Console as before; Debug stays file-only. The session file is project-independent so a bug report can point at the same path regardless of which Unity project surfaced it. Multi-line StringBuilder dumps from `WeightSanityCheckWindow` (Inspect Vertex, Weight Dump, verbose scan log) and `PhysBoneClippingRiskWindow` (verbose scan log) now go through the logger too.
- **theming:** Tool window OnGUI / OnInspectorGUI bodies open `using (WkStyles.Scope(WkTheme.WhyKnot))` so the IMGUI palette emits the WhyKnot brand colors (black / gray / light blue). Covers Weight Sanity Check, PhysBone Preset, PhysBone Clipping Risks, Bone Merger, Mesh Fix window, and the AutoTightenToBody / MeshFixController inspector editors.
- Loom M1, intent-component split, Mask Painter (1.1.0-beta.5) (556c170)
- **loom+ci:** Defer Loom, autoload logger version (1.1.0-beta.6) (97903d2)
- Bundle wk-core, drop dev.whyknot.core dep, remove Loom (1.2.0) (e78ffe0)

### Changed
- **deps:** Bumped `dev.whyknot.core` dependency to `>=1.1.0` so the new theming system and `WkLogger` are guaranteed available.
- **deps:** Add `dev.whyknot.core` (>=1.0.0) as a hard `vpmDependency`. VCC auto-installs the shared utility package alongside wk-vrc-qol. Internal-only refactor that moves `AvatarQolStyles` (palette + lazy GUIStyles + IMGUI primitives) -> `WkStyles`, `HumanoidSideMap` + `BoneSide` -> `WhyKnot.Core.Utilities`, `GetGameObjectPath` -> `WhyKnot.Core.Utilities.PathUtility`, and the FBX-clone helper that `WeightFixer` and `BoneMergerWindow` duplicated -> `WhyKnot.Core.Utilities.FbxMeshUtility`. The three domain-specific issue-category colors (humanoid / spatial / center) stay in this package as `AvatarQolCategoryColors`. No user-visible behaviour change beyond Ctrl+Z on `WeightFixer` now also removing the cloned `.mesh` asset from disk (parity with Bone Merger's existing behaviour).
- **deps:** Bump actions/checkout from 4 to 6 (#1) (0d8ab2b)
- Mesh Fix pipeline: fix native-array leak, missing using, idempotent delayCall, preview leak; only clone write-target meshes (31d1746)
- Editor asmdef: gate WHYKNOT_NDMF on nadena.dev.ndmf >=1.0.0; qualify Object disambiguation (5785f9d)
- Surrounding tools: WeightFixer handshake; Clipping Keep/Merge/Overwrite (3221b6d)
- Mesh Fix pipeline: redesign Auto Mesh Fixes around plan/apply with shape registry (e3b5885)
- Add Auto Mesh Fixes, PhysBone Clipping Risks, Bone Merger; namespace tools under WhyKnot/wk-vrc-qol (1471d75)
- Editor asmdef: drop the speculative WHYKNOT_NDMF versionDefine (5a7f5e1)

### Fixed
- `PhysBoneReinitHook.cs` no longer passes a Unity `Component` as the second argument to `AvatarQolLogger.Instance.Warning(...)`. Side effect of the 1.1.0-beta.3 mass migration from `Debug.LogWarning(msg, contextObject)` -- the regex stripped the `Debug.LogWarning(` prefix but left the trailing context argument behind, which clashed with `WkLogger.Warning`'s `[CallerMemberName] string member = ""` parameter and failed CS1503. The context object reference is dropped; the offending component's `name` is already interpolated into the message.
- **logger:** Qualify PackageInfo to avoid ambiguity with UnityEditor.PackageInfo (fead5a3)

### Removed
- Auto Mesh Fixes pipeline (`AutoTightenToBody`, `WhyKnotMeshFixController`, the Mesh Fix window, both build hooks, the garment-tighten and body-hide operations). The garment-tighten output never produced a usable result in practice. Scenes that still hold an `AutoTightenToBody` component will log a missing-script warning on next open -- remove the stub component to clear it.

---

## [1.0.1](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.0.1) -- 2026-05-07

### Changed
- License: switched from MIT to GPL-3.0-or-later. Same set of users can use, modify, and redistribute; downstream forks now propagate the GPL terms instead of MIT's permissive ones.
- Repo infra: auto-maintained `CHANGELOG.md` via verified bot commits on every push to `main` (conventional-commit subjects bucket into Added/Changed/Fixed). Branch protection ruleset on `main` now requires signed commits.

---

## [1.0.0](https://github.com/RealWhyKnot/wk-vrc-qol/releases/tag/v1.0.0) -- 2026-05-03

First release as a VRChat Package Manager (VPM) package, installable via the Creator Companion at `https://vpm.whyknot.dev/index.json`.

### Added
- VPM package metadata (`package.json`) declaring `dev.whyknot.wk-vrc-qol` with a hard `vpmDependencies` on `com.vrchat.avatars` (>= 3.5.0).
- Editor assembly definition (`Editor/dev.whyknot.wk-vrc-qol.Editor.asmdef`) scoping the tools to the Editor platform and gating the SDK-conditional code via `versionDefines` for `VRC_SDK_VRCSDK3`.

### Changed
- **Breaking for loose-script users.** Prior to 1.0.0 the recommended install was to drop the `Editor/` folder anywhere under your `Assets/` tree; Unity compiled the scripts into the project's default editor assembly. With the new asmdef, code now compiles into a dedicated `dev.whyknot.wk-vrc-qol.Editor` assembly. If you were previously importing as loose scripts and you upgrade by adding the asmdef in place, *internal* type references inside this package keep working, but any **external** code in your project that referenced these tools' types (e.g. `WhyKnot.AvatarQol.AvatarQol` from your own scripts) will need its asmdef to add `dev.whyknot.wk-vrc-qol.Editor` to its `references`.
- Recommended migration: remove the old loose-script copy from `Assets/` and reinstall via VCC. Unity asset GUIDs are regenerated on import; nothing inside this package references its own files by GUID, so no project-side cleanup is required beyond removing the duplicate.

### Notes
- The `#if VRC_SDK_VRCSDK3` blocks in `PhysBonePlanApplier` and `PhysBonePresetWindow` are preserved verbatim. They are now effectively always-on (the package hard-depends on `com.vrchat.avatars`, which sets the define via `versionDefines`), but kept for future flexibility if the dependency is later relaxed.
