using System.Collections.Generic;
using UnityEngine;
using DarkRift;
using ABI_RC.Core.Networking;
using ABI_RC.Core.Player;
using ABI_RC.Systems.InputManagement;
using PlayerRemoteControl.Transport;

namespace PlayerRemoteControl.Puppet;

// Puppet side: apply the controller's incoming pose/params to the local avatar so both local render
// and outgoing network sync reflect the controller, which is what makes every other player see the
// puppet move. Local input is suppressed (PuppetInputBlock), the view is anchored
// (PuppetViewOverride), and voice runs through PuppetVoiceControl.
// DrivingRemote is set only around the mod's own animator writes, so the input block
// lets those through while still no-opping the live player's writes.
internal sealed class PuppetSession
{
    internal static PuppetSession Active { get; private set; }
    internal static bool DrivingRemote;

    internal string ControllerUuid { get; }
    internal PermissionProfile Profile { get; private set; }
    internal bool VoiceOverrideActive => _voice.VoiceOverrideActive;

    // Issued to the controller in AcceptControl; every inbound control packet must echo it back.
    private uint _token;
    private float _nextConsentCheck;
    private bool _anchoring;

    internal bool TryAuthenticate(byte[] payload, out byte[] inner) => ControlPacket.Unwrap(payload, _token, out inner);
    internal byte[] Authenticate(byte[] payload) => ControlPacket.Wrap(payload, _token);

    // Received pose, double-buffered to interpolate past→current between packets. _applyBuffer is a
    // scratch copy so WriteDataToAnimatorLerped never reads and writes the same object (aliasing it as
    // both current and past gave a stepped, inaccurate pose).
    private readonly PlayerAvatarMovementData _current = new();
    private readonly PlayerAvatarMovementData _past = new();
    private readonly PlayerAvatarMovementData _applyBuffer = new();
    private bool _hasPose;

    // EMA of the packet arrival interval, so interpolation progress reaches ~1 by the time the next
    // packet lands regardless of the sender's rate.
    private float _lastPoseTime;
    private float _poseInterval = 0.033f;

    // Latest AAS blob.
    private float[] _aasFloats; private int[] _aasInts; private byte[] _aasBytes;

    // Latest received view target, kept for the offset diagnostic below.
    private Vector3 _lastViewPos;
    private Quaternion _lastViewRot = Quaternion.identity;
    private float _nextViewDiag;

    // The puppet's own face/eye tracking, snapshotted before the controller's pose overwrites the
    // outgoing buffer. Restored when the puppet keeps their head, so observers see their expression.
    private readonly float[] _ownFace = new float[37];

    private readonly PuppetVoiceControl _voice = new();
    private readonly PuppetViewOverride _view = new();
    private readonly HeadHide _headHide = new();
    private readonly MuscleRetainer _retainer = new();

    internal PuppetSession(string controllerUuid, PermissionProfile profile)
    {
        ControllerUuid = controllerUuid;
        Profile = profile ?? Settings.DefaultProfile();
    }

    // True when the puppet keeps their own head/view (head-only). Camera, AudioListener and head-hide
    // then stay fully native. Anchoring the camera would fight the HMD and make the listener oscillate
    // (voices pulse). The controller still drives the body minus head.
    private bool RetainsView => Profile.ForceFreeLook;

    internal void AcceptControl()
    {
        Active = this;
        // Only anchor the view (and hide the head) when actually taking over the puppet's view.
        _anchoring = !RetainsView;
        if (_anchoring)
        {
            _view.Enable();
            _headHide.Enable();
        }
        // Issue a fresh per-session token; the controller echoes it in every control packet.
        _token = ControlPacket.NewToken();
        PlayerRemoteControlMod.Instance.Transport.Send(ControllerUuid,
            ControlPacket.Wrap(new[] { (byte)PacketType.AcceptControl }, _token));
    }

    internal void OnControlData(PacketType type, byte[] payload)
    {
        switch (type)
        {
            case PacketType.Pose:
            {
                Vector3 viewPos; Quaternion viewRot;
                try
                {
                    _past.CopyDataFrom(_current);
                    PoseCodec.Decode(payload, _current, out viewPos, out viewRot);
                }
                catch { return; } // malformed packet; drop it quietly
                _hasPose = true;

                // Track arrival interval for pose interpolation.
                float now = Time.time;
                if (_lastPoseTime > 0f)
                {
                    float dt = now - _lastPoseTime;
                    if (dt > 0f && dt < 0.5f) _poseInterval = Mathf.Lerp(_poseInterval, dt, 0.2f);
                }
                _lastPoseTime = now;

                // The controller's real camera transform is the view target (interpolated toward).
                _view.SetTarget(viewPos, viewRot);
                _lastViewPos = viewPos;
                _lastViewRot = viewRot;
                break;
            }
            case PacketType.Aas:
                if (!AasCodec.Decode(payload, out _aasFloats, out _aasInts, out _aasBytes)) return;
                // The puppet's own AAS send is suppressed while controlled (PuppetAasSuppressPatch),
                // so re-broadcast the controller's blob. It's the only AAS others receive from us.
                BroadcastAas(_aasFloats, _aasInts, _aasBytes);
                break;
            case PacketType.SetMute:
                _voice.SetMute(payload.Length > 1 && payload[1] != 0 && Profile.AllowMute);
                break;
            case PacketType.SetVoiceOverride:
                _voice.SetVoiceOverride(payload.Length > 1 && payload[1] != 0 && Profile.AllowVoiceOverride);
                break;
            case PacketType.ControllerVoice:
                _voice.OnRemoteVoiceFrame(ControllerUuid, payload);
                break;
        }
    }

    // Post-solve: stamp the received pose onto the animator (local render). Animator params
    // from current. Body pose via WriteDataToAnimatorLerped, interpolating _past→_applyBuffer.
    internal void ApplyToAnimator()
    {
        if (!_hasPose) return;
        var setup = PlayerSetup.Instance;
        if (setup == null || setup.Animator == null) return;

        DrivingRemote = true;
        try
        {
            var am = setup.AnimatorManager;
            if (am != null)
            {
                am.MovementX = _current.AnimatorMovementX;
                am.MovementY = _current.AnimatorMovementY;
                am.Grounded = _current.AnimatorGrounded;
                am.Emote = (int)_current.AnimatorEmote;
                am.GestureLeft = _current.AnimatorGestureLeft;
                am.GestureRight = _current.AnimatorGestureRight;
                am.Toggle = (int)_current.AnimatorToggle;
                am.Sitting = _current.AnimatorSitting;
                am.Crouching = _current.AnimatorCrouching;
                am.Flying = _current.AnimatorFlying;
                am.Prone = _current.AnimatorProne;
                if (_aasFloats != null)
                    try { am.ApplyAdvancedAvatarSettings(_aasFloats, _aasInts, _aasBytes); } catch { }
            }

            float progress = _poseInterval > 0f ? Mathf.Clamp01((Time.time - _lastPoseTime) / _poseInterval) : 1f;

            // Selective pose: snapshot the puppet's own solved muscles before the controller's write,
            // then hand back the retained regions afterwards. No mask = full-body drive.
            var retained = Profile.EffectiveRetain;
            if (retained != BodyRegion.None) _retainer.CaptureOwn(setup.Animator);

            _applyBuffer.CopyDataFrom(_current);
            _applyBuffer.WriteDataToAnimatorLerped(setup.Animator, Vector3.zero,
                isBlocked: false, isBlockedAlt: false, _past, progress);

            if (retained != BodyRegion.None) _retainer.RestoreRetained(setup.Animator, retained);
        }
        finally { DrivingRemote = false; }
    }

    // Move the puppet's real character controller to the controller's root each frame, so the puppet is
    // genuinely relocated (not just visually), with no snap-back to their old spot when control ends. The
    // controller moves smoothly, so this reads as movement, not a teleport.
    private void MovePuppetBody()
    {
        if (!_hasPose) return;
        // Locomotion consent off: stay put. Controller can pose but not walk the puppet around.
        if (!Profile.AllowLocomotion) return;
        var cc = ABI_RC.Systems.Movement.BetterBetterCharacterController.Instance;
        if (cc == null) return;
        float progress = _poseInterval > 0f ? Mathf.Clamp01((Time.time - _lastPoseTime) / _poseInterval) : 1f;
        Vector3 pos = Vector3.Lerp(_past.RootPosition, _current.RootPosition, progress);

        if (FreeLook)
        {
            // Position only. Forcing the controller's yaw would pin the puppet's left/right look
            // (desktop yaw rotates the body), so leave rotation to their own input.
            cc.TeleportPlayerTo(pos, interpolate: false, updateGround: false);
        }
        else
        {
            // Locked: align rig yaw to the controller so the menu opens in the shared look direction.
            // Yaw only: the full RootRotation euler can carry pitch/roll (avatar load, VR lean) that
            // tilts the whole rig and throws the view sideways until it settles.
            cc.TeleportPlayerTo(pos, new Vector3(0f, _current.RootRotation.y, 0f), interpolate: false, updateGround: false);
        }
    }

    // Re-emit the received AAS blob as the puppet's own outgoing AdvancedAvatarSettings (tag 10100),
    // byte-for-byte like PlayerSetup, so every other player applies the controller's params to us.
    private void BroadcastAas(float[] f, int[] i, byte[] b)
    {
        if (f == null || i == null || b == null) return;
        var net = NetworkManager.Instance?.GameNetwork;
        if (net == null || net.ConnectionState != ConnectionState.Connected) return;
        using var w = DarkRiftWriter.Create();
        w.Write(f.Length); w.Write(i.Length); w.Write(b.Length);
        foreach (var x in f) w.Write(x);
        foreach (var x in i) w.Write(x);
        foreach (var x in b) w.Write(x);
        using var msg = Message.Create(10100, w);
        net.SendMessage(msg, SendMode.Unreliable);
    }

    // Postfix on the local movement-data build: overwrite the outgoing buffer with the
    // controller's pose so remote observers see the puppet move, not the suppressed live pose.
    internal void ApplyToOutgoing(PlayerSetup setup)
    {
        if (!_hasPose) return;
        var outgoing = setup.GetPlayerMovementData();
        if (outgoing == null) return;
        // Keep the puppet's own world position when locomotion is off: driven in place, not walked.
        Vector3 ownRoot = outgoing.RootPosition;

        // Keeping the head means keeping face/eye tracking too. It rides outside the muscle array
        // (blendshapes + eye fields), so the muscle retainer misses it. Snapshot it here and restore
        // after the controller's data lands, so observers see the puppet's expression and gaze.
        bool keepFace = (Profile.EffectiveRetain & BodyRegion.Head) != 0;
        bool ownFaceEnabled = false;
        bool ownEyeOverride = false, ownBlinkOverride = false;
        Vector3 ownEyePos = default; float ownBlinkL = 0f, ownBlinkR = 0f;
        if (keepFace)
        {
            ownFaceEnabled = outgoing.FaceTrackingEnabled;
            if (outgoing.FaceTrackingData != null) System.Array.Copy(outgoing.FaceTrackingData, _ownFace, 37);
            ownEyeOverride = outgoing.EyeTrackingOverride;
            ownBlinkOverride = outgoing.EyeBlinkingOverride;
            ownEyePos = outgoing.EyeTrackingPosition;
            ownBlinkL = outgoing.EyeTrackingBlinkProgressLeft;
            ownBlinkR = outgoing.EyeTrackingBlinkProgressRight;
        }

        outgoing.CopyDataFrom(_current);
        if (!Profile.AllowLocomotion) outgoing.RootPosition = ownRoot;
        // Retained limbs track the puppet for observers too, not the controller.
        _retainer.ApplyRetainedToData(outgoing.MuscleValues, Profile.EffectiveRetain);

        if (keepFace)
        {
            outgoing.FaceTrackingEnabled = ownFaceEnabled;
            if (outgoing.FaceTrackingData != null) System.Array.Copy(_ownFace, outgoing.FaceTrackingData, 37);
            outgoing.EyeTrackingOverride = ownEyeOverride;
            outgoing.EyeBlinkingOverride = ownBlinkOverride;
            outgoing.EyeTrackingPosition = ownEyePos;
            outgoing.EyeTrackingBlinkProgressLeft = ownBlinkL;
            outgoing.EyeTrackingBlinkProgressRight = ownBlinkR;
        }
    }

    // Free-look = the puppet-local toggle, or forced when the profile retains the head. Either way the
    // view rotation stays the puppet's own. Only the position is anchored.
    private bool FreeLook => Settings.FreeLookWhileControlled.Value || Profile.ForceFreeLook;

    private bool _lastRotationLocked = true;

    // Panic release: spamming jump 10× within 10s, or pressing tilde (~), ends being controlled. Both
    // are input the mod never suppresses, so they always work.
    private readonly Queue<float> _jumpTimes = new();
    private bool _prevJump;
    private const int JumpPanicCount = 10;
    private const float JumpPanicWindow = 10f;

    private bool CheckPanic()
    {
        if (Input.GetKeyDown(KeyCode.BackQuote))
        {
            PlayerRemoteControlMod.Instance.StopBeingControlled("Control released (tilde key).");
            return true;
        }

        bool jump = CVRInputManager.Instance != null && CVRInputManager.Instance.jump;
        if (jump && !_prevJump)
        {
            float now = Time.time;
            _jumpTimes.Enqueue(now);
            while (_jumpTimes.Count > 0 && _jumpTimes.Peek() < now - JumpPanicWindow) _jumpTimes.Dequeue();
            if (_jumpTimes.Count >= JumpPanicCount)
            {
                _jumpTimes.Clear();
                _prevJump = jump;
                PlayerRemoteControlMod.Instance.StopBeingControlled("Control released (jump spam).");
                return true;
            }
        }
        _prevJump = jump;
        return false;
    }

    // Consent can change mid-session. Re-resolve the puppet's own settings periodically so revoking
    // consent (or whitelisting the controller out) ends control at once, and tightening limb,
    // locomotion or voice permissions takes effect live instead of being frozen at accept time.
    private bool RevalidateConsent()
    {
        if (Time.time < _nextConsentCheck) return false;
        _nextConsentCheck = Time.time + 0.5f;

        var fresh = Settings.ResolveProfile(ControllerUuid);
        if (!fresh.AllowBeingControlled ||
            (Settings.WhitelistEnabled.Value && !Settings.IsWhitelisted(ControllerUuid)))
        {
            PlayerRemoteControlMod.Instance.StopBeingControlled("Control released (consent revoked).");
            return true;
        }

        Profile = fresh;
        if (!Profile.AllowVoiceOverride) _voice.SetVoiceOverride(false);
        if (!Profile.AllowMute) _voice.SetMute(false);

        // Toggling head-retain mid-session flips whether we anchor the view / hide the head.
        bool anchor = !RetainsView;
        if (anchor != _anchoring)
        {
            _anchoring = anchor;
            if (anchor) { _view.Enable(); _headHide.Enable(); }
            else { _view.Disable(); _headHide.Disable(); }
        }
        return false;
    }

    internal void Update()
    {
        if (CheckPanic()) return;
        if (RevalidateConsent()) return;
        if (!RetainsView)
        {
            bool locked = !FreeLook;
            _view.RotationLocked = locked;
            _view.LevelHorizon = Settings.LevelHorizonWhileControlled.Value;
            if (locked != _lastRotationLocked)
            {
                _lastRotationLocked = locked;
                _view.ForceReanchor(); // re-drive the camera immediately on the toggle
                if (locked) PlayerRemoteControlMod.Log.Msg("Free-look off; resuming camera/rig drive.");
            }
            _headHide.Update();
        }
        MovePuppetBody();
        _voice.Update(ControllerUuid);
        LogViewOffset();
    }

    // Temporary diagnostic for the "camera sits behind / rotated off from my body" report: every 2s,
    // log the transmitted view pose against the driven head bone and root. A large |Δ| with agreeing
    // rotations points to a playspace offset. Disagreeing rotations point to a rotation-space bug.
    private void LogViewOffset()
    {
        if (!_hasPose || Time.time < _nextViewDiag) return;
        _nextViewDiag = Time.time + 2f;
        var setup = PlayerSetup.Instance;
        if (setup == null || setup.Animator == null || !setup.Animator.isHuman) return;
        var head = setup.Animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) return;

        Vector3 headPos = head.position;
        Vector3 root = _current.RootPosition;
        Vector3 posDelta = _lastViewPos - headPos;
        Vector3 viewFwd = _lastViewRot * Vector3.forward;
        Vector3 headFwd = head.forward;
        float fwdAngle = Vector3.Angle(viewFwd, headFwd);
        PlayerRemoteControlMod.Log.Msg(
            $"[view-diag] viewPos={_lastViewPos:F2} headPos={headPos:F2} rootPos={root:F2} " +
            $"posDelta={posDelta:F2} |Δ|={posDelta.magnitude:F2}m viewEuler={_lastViewRot.eulerAngles:F0} " +
            $"headEuler={head.rotation.eulerAngles:F0} fwdAngle={fwdAngle:F0}deg locked={!FreeLook}");
    }

    internal void Stop()
    {
        if (Active == this) Active = null;
        _view.Disable();
        _headHide.Disable();
        _voice.Restore();
        PlayerRemoteControlMod.Instance.Transport.Send(ControllerUuid, new[] { (byte)PacketType.StopControl });
        PlayerRemoteControlMod.Log.Msg("Control ended (puppet restored).");
    }
}