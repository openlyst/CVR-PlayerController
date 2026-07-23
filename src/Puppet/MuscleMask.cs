using System;
using UnityEngine;

namespace PlayerRemoteControl.Puppet;

// Regions of Unity's 95-muscle humanoid array that the puppet can retain (keep driving themselves)
// while the controller drives everything else. Indices match PlayerAvatarMovementData.MuscleGroups.
[Flags]
internal enum BodyRegion
{
    None         = 0,
    Head         = 1 << 0, // neck + head, muscles 9-14
    Spine        = 1 << 1, // lower spine + chest, muscles 0-8
    LeftArm      = 1 << 2, // 37-45
    RightArm     = 1 << 3, // 46-54
    LeftLeg      = 1 << 4, // 21-28
    RightLeg     = 1 << 5, // 29-36
    LeftFingers  = 1 << 6, // 55-74
    RightFingers = 1 << 7, // 75-94

    Arms    = LeftArm | RightArm,
    Legs    = LeftLeg | RightLeg,
    Fingers = LeftFingers | RightFingers,
}

// Selective-pose helper. Around the controller's full-body write, snapshot the puppet's own solved
// muscles and hand back just the retained regions, so "head only", "both arms only", etc. all fall
// out of one mechanism.
// GetHumanPose reads the current animator muscle state, the retained indices are overwritten, and
// SetHumanPose writes it back. Capture must run post-solve, while the animator still holds the
// puppet's own pose (before the controller's write overwrites it).
internal sealed class MuscleRetainer
{
    private static readonly (int start, int count, BodyRegion region)[] Regions =
    {
        (9,  6,  BodyRegion.Head),
        (0,  9,  BodyRegion.Spine),
        (37, 9,  BodyRegion.LeftArm),
        (46, 9,  BodyRegion.RightArm),
        (21, 8,  BodyRegion.LeftLeg),
        (29, 8,  BodyRegion.RightLeg),
        (55, 20, BodyRegion.LeftFingers),
        (75, 20, BodyRegion.RightFingers),
    };

    private HumanPoseHandler _handler;
    private Animator _animator;
    private HumanPose _pose;
    private readonly float[] _ownMuscles = new float[95];
    private bool _captured;

    private bool Ensure(Animator animator)
    {
        if (animator == null || animator.avatar == null || !animator.avatar.isHuman) return false;
        if (animator != _animator || _handler == null)
        {
            _handler?.Dispose();
            _handler = new HumanPoseHandler(animator.avatar, animator.transform);
            _animator = animator;
        }
        return true;
    }

    // Snapshot the puppet's own solved muscles. Call before the controller's pose write.
    internal void CaptureOwn(Animator animator)
    {
        _captured = false;
        if (!Ensure(animator)) return;
        _handler.GetHumanPose(ref _pose);
        if (_pose.muscles == null || _pose.muscles.Length < 95) return;
        Array.Copy(_pose.muscles, _ownMuscles, 95);
        _captured = true;
    }

    // Restore the retained regions to the captured own pose. Call after the controller's
    // pose write, on the animator (local render).
    internal void RestoreRetained(Animator animator, BodyRegion retained)
    {
        if (!_captured || retained == BodyRegion.None) return;
        if (!Ensure(animator)) return;
        _handler.GetHumanPose(ref _pose);
        if (_pose.muscles == null || _pose.muscles.Length < 95) return;
        foreach (var (start, count, region) in Regions)
            if ((retained & region) != 0)
                Array.Copy(_ownMuscles, start, _pose.muscles, start, count);
        _handler.SetHumanPose(ref _pose);
    }

    // Overwrite the retained regions in an outgoing movement-data buffer with the puppet's
    // own muscles, so remote observers also see the retained limbs track the puppet, not the
    // controller. Uses the last captured snapshot.
    internal void ApplyRetainedToData(float[] muscleValues, BodyRegion retained)
    {
        if (!_captured || retained == BodyRegion.None || muscleValues == null || muscleValues.Length < 95) return;
        foreach (var (start, count, region) in Regions)
            if ((retained & region) != 0)
                Array.Copy(_ownMuscles, start, muscleValues, start, count);
    }
}