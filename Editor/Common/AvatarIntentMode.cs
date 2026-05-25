// AvatarIntentMode.cs
//
// Describes which lifecycle stage an avatar-intent runner is executing
// against. The same code path -- "apply this intent to the avatar" --
// runs in three different contexts with different rollback semantics:
//
//   Upload   = IVRCSDKPreprocessAvatarCallback during Build & Publish.
//              Restored in OnPostprocessAvatar so the editor scene is
//              untouched after upload.
//   PlayMode = ExitingEditMode -> mutate scene avatars in place. Unity
//              discards play-mode changes on exit; we still dispose the
//              session to put generated meshes back so PhysBone reinit
//              and animator binding stay correct on the next play.
//   Preview  = Edit-mode preview against an instantiated avatar clone.
//              The source avatar is hidden in the Scene view while a
//              preview is active.
//
// The mode is supplied to the runner so an intent that needs to behave
// differently between preview and upload (e.g., suppressing destructive
// asset writes) has the signal to do so.

namespace WhyKnot.AvatarQol.Intent {

    internal enum AvatarIntentMode {
        Preview,
        PlayMode,
        Upload,
    }
}
