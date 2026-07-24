using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using MelonLoader;
using ABI.CCK.Components;
using ABI_RC.Core.Player;
using ABI_RC.Core.Savior;
using ABI_RC.Core.EventSystem;
using ABI_RC.Core.InteractionSystem;
using ABI_RC.Systems.GameEventSystem;
using PlayerRemoteControl.Transport;

namespace PlayerRemoteControl.Controller;

// Controller side: request control of a target, and once they consent, switch the networked avatar
// to the invisible premade so others stop seeing the controller, then let
// ControllerCapturePatches stream the solved pose + AAS to the puppet each tick.
// Locally wearing the target's avatar (first-person view) is best-effort (see ControllerWear).
internal sealed class ControlSession
{
    internal string TargetUuid { get; }
    internal bool Accepted { get; private set; }

    private string _originalAvatarGuid;
    private bool _switchedInvisible;
    private ControllerWear _wear;
    private ControllerVoiceMonitor _voiceMonitor;

    // A target running the mod always replies (accept or decline); no reply within the timeout means
    // they don't have the mod.
    private bool _pending;
    private float _requestedAt;
    private const float HandshakeTimeout = 4f;

    // Session token the puppet handed us in AcceptControl; every control packet we send echoes it.
    private uint _token;
    internal byte[] Authenticate(byte[] payload) => ControlPacket.Wrap(payload, _token);
    internal bool TryAuthenticate(byte[] payload, out byte[] inner) => ControlPacket.Unwrap(payload, _token, out inner);

    // How far to raise the networked (invisible) body's root so its nameplate sits above the puppet's.
    // Both bodies share a ground point (the puppet is driven to the controller's root), so nameplates
    // would otherwise separate only by avatar head heights. Raising by (puppetHeadHeight + margin)
    // guarantees ours is higher. Only the invisible networked body moves. The real body/camera and the
    // pose sent to the puppet keep the true ground position, so there's no feedback.
    internal float NetworkedRootRaise { get; private set; }
    private const float NameplateMargin = 0.5f;

    internal ControlSession(string targetUuid) { TargetUuid = targetUuid; }

    // In-game HUD toast + log.
    private static void Notify(string message)
    {
        PlayerRemoteControlMod.Log.Msg(message);
        if (ViewManager.Instance != null) ViewManager.Instance.NotifyUser("Player Remote Control", message, 3f);
    }

    // Ask the target for control. They only accept if their AllowBeingControlled is on.
    internal void RequestControl()
    {
        PlayerRemoteControlMod.Log.Msg($"Requesting control of {TargetUuid}…");
        _pending = true;
        _requestedAt = Time.time;
        PlayerRemoteControlMod.Instance.Transport.Send(TargetUuid, new[] { (byte)PacketType.RequestControl });
    }

    // Target has the mod but hasn't consented (AllowBeingControlled off).
    internal void OnDeclined()
    {
        if (Accepted) return;
        _pending = false;
        Notify("User has not consented to being controlled");
        PlayerRemoteControlMod.Instance.StopControlling();
    }

    internal void OnAccepted(byte[] payload)
    {
        _pending = false;
        if (Accepted) return;
        if (payload != null && payload.Length >= 5)
            _token = (uint)(payload[1] | (payload[2] << 8) | (payload[3] << 16) | (payload[4] << 24));
        Accepted = true;
        PlayerRemoteControlMod.Log.Msg($"{TargetUuid} accepted; control active.");

        _originalAvatarGuid = MetaPort.Instance != null ? MetaPort.Instance.currentAvatarGuid : null;
        MelonCoroutines.Start(SwitchInvisibleThenWear());
    }

    // Order matters: fire the networked invisible switch first, wait until it's actually the current
    // avatar, then overlay the local-only puppet wear. Wearing first lets the invisible switch's later
    // load clobber it.
    private IEnumerator SwitchInvisibleThenWear()
    {
        SwitchToInvisible();
        if (_switchedInvisible)
        {
            float timeout = Time.time + 10f;
            while (Time.time < timeout &&
                   (MetaPort.Instance == null || MetaPort.Instance.currentAvatarGuid != Settings.InvisibleAvatarGuid))
                yield return null;

            // If the invisible avatar never became current, the API switch failed (missing, not public,
            // or not permitted, so CVR fell back to the default). A placeholder body drives nothing
            // useful, so cancel and report.
            if (!Accepted) yield break;
            if (MetaPort.Instance == null || MetaPort.Instance.currentAvatarGuid != Settings.InvisibleAvatarGuid)
            {
                Notify("Invisible avatar failed to load; cancelling control");
                PlayerRemoteControlMod.Instance.StopControlling();
                yield break;
            }
            yield return null; // let the instantiate finish before we overlay
        }
        if (!Accepted) yield break; // stopped while switching
        _wear = new ControllerWear(TargetUuid);
        _wear.Enter();

        // Re-wear if the puppet swaps avatars mid-control so the first-person copy tracks the new one.
        // The session (invisible body, pose/AAS streaming) keeps running. Only the local copy reloads.
        _avatarChangeListener = OnPuppetAvatarChanged;
        CVRGameEventSystem.Avatar.OnRemoteAvatarLoad.AddListener(_avatarChangeListener);
    }

    private System.Action<CVRPlayerEntity, CVRAvatar> _avatarChangeListener;

    private void OnPuppetAvatarChanged(CVRPlayerEntity player, CVRAvatar avatar)
    {
        if (!Accepted || player == null || player.Uuid != TargetUuid) return;
        PlayerRemoteControlMod.Log.Msg("Puppet switched avatar; re-wearing their new avatar locally.");
        _wear?.Exit();
        _wear = new ControllerWear(TargetUuid);
        _wear.Enter();
    }

    // Networked avatar switch via the AvatarSwitch API, so every player sees the controller become
    // invisible. Not a local-only load.
    private void SwitchToInvisible()
    {
        if (Settings.InvisibleAvatarGuid == "00000000-0000-0000-0000-000000000000")
        {
            PlayerRemoteControlMod.Log.Warning("Invisible avatar GUID not set; skipping networked hide (you stay visible).");
            return;
        }
        if (AssetManagement.Instance == null) return;
        AssetManagement.Instance.LoadLocalAvatar(Settings.InvisibleAvatarGuid);
        _switchedInvisible = true;
    }

    // Measure head height above root from the worn avatar (a copy of the puppet's), so the raise scales
    // with the puppet's actual size. World positions already account for avatar scale.
    private void UpdateNameplateRaise()
    {
        var setup = PlayerSetup.Instance;
        if (setup == null || setup.Animator == null || !setup.Animator.isHuman || setup.AvatarObject == null) return;
        var head = setup.Animator.GetBoneTransform(HumanBodyBones.Head);
        if (head == null) return;
        float headHeight = head.position.y - setup.AvatarObject.transform.position.y;
        if (headHeight > 0f) NetworkedRootRaise = headHeight + NameplateMargin;
    }

    // Controller UI → toggle muting the puppet's mic (honored only if puppet consented).
    internal void SetMute(bool on) =>
        PlayerRemoteControlMod.Instance.Transport.Send(TargetUuid, Authenticate(new[] { (byte)PacketType.SetMute, (byte)(on ? 1 : 0) }));

    // True while routing the controller's voice through the puppet. Gates the voice-capture patch.
    internal bool VoiceOverrideOn { get; private set; }

    // Controller UI: toggle routing the controller's voice through the puppet. Tells the
    // puppet to accept (they still gate on their own consent), forwards Opus frames, and mutes the
    // puppet's voice locally so the controller doesn't hear their own audio echoed off the re-emit.
    internal void SetVoiceOverride(bool on)
    {
        VoiceOverrideOn = on;
        PlayerRemoteControlMod.Instance.Transport.Send(TargetUuid, Authenticate(new[] { (byte)PacketType.SetVoiceOverride, (byte)(on ? 1 : 0) }));
        MutePuppetPipelineLocally(on); // mute the fused CVR stream to avoid hearing ourselves
        if (on) _voiceMonitor ??= new ControllerVoiceMonitor(TargetUuid);
        else { _voiceMonitor?.Dispose(); _voiceMonitor = null; }
    }

    // A puppet voice frame arrived on the side-channel. Play it on the stereo monitor.
    internal void OnPuppetVoiceFrame(byte[] payload) => _voiceMonitor?.OnFrame(payload);

    // Incremented by the capture patch each time a pose frame is streamed to the puppet.
    internal int PoseFramesSent;
    private int _lastTrackFrames;
    private float _nextTrack;

    // The streamed pose is re-broadcast by the puppet to everyone, so a steady frame rate plus the
    // puppet still being present means observers are seeing the control. Logged periodically. If the
    // puppet leaves, stop cleanly.
    private void TrackRemote()
    {
        if (Time.time < _nextTrack) return;
        _nextTrack = Time.time + 5f;

        if (CVRPlayerManager.Instance == null || !CVRPlayerManager.Instance.UserIdToPlayerEntity.ContainsKey(TargetUuid))
        {
            Notify("Puppet left the instance; stopping control");
            PlayerRemoteControlMod.Instance.StopControlling();
            return;
        }

        int fps = (PoseFramesSent - _lastTrackFrames) / 5;
        _lastTrackFrames = PoseFramesSent;
        PlayerRemoteControlMod.Log.Msg(
            $"[control] {TargetUuid}: streaming remotely ~{fps} pose fps; invisible={_switchedInvisible}, voiceOverride={VoiceOverrideOn}");
    }

    // The puppet's fused stream carries the controller's voice (mixed in on their side), so CVR would
    // play it back as the controller's own voice on a delay. Mute the puppet's pipeline locally.
    // AudioSource.mute alone doesn't hold (FixedUpdate re-sets it from the pipeline's own flags), so
    // set _selfModerationMute (which that flag calc honors) and re-assert.
    private void MutePuppetPipelineLocally(bool mute)
    {
        var client = ABI_RC.Systems.Communications.Comms_Manager.Instance?.Client;
        if (client != null && client.FindParticipantPipeline(TargetUuid, out var pipe) && pipe != null)
        {
            pipe._selfModerationMute = mute;
            if (pipe.AudioSource != null) pipe.AudioSource.mute = mute;
        }
    }

    internal void Update()
    {
        // No reply to our control request within the timeout → the target isn't running the mod.
        if (_pending && !Accepted && Time.time - _requestedAt > HandshakeTimeout)
        {
            _pending = false;
            Notify("User does not have the mod");
            PlayerRemoteControlMod.Instance.StopControlling();
            return;
        }

        if (!Accepted) return;
        // Voice routing is a global controller-side toggle now. Apply it live to the active session.
        if (Settings.RouteVoiceWhenControlling.Value != VoiceOverrideOn) SetVoiceOverride(Settings.RouteVoiceWhenControlling.Value);
        _wear?.Update();
        UpdateNameplateRaise();
        if (VoiceOverrideOn) MutePuppetPipelineLocally(true); // re-assert against CVR's per-tick reset
        TrackRemote();
        // No body-parking teleport: the puppet's outgoing root is overridden with the controller's, so
        // they already co-locate by construction. Teleporting the controller to the puppet each frame
        // fed the offset back through the puppet's root and sent both players climbing skyward. Nameplate
        // de-clash is handled by raising the networked root instead (see NetworkedRootRaise).
    }

    internal void Stop()
    {
        if (!Accepted) { return; }
        Accepted = false;
        if (_avatarChangeListener != null)
        {
            CVRGameEventSystem.Avatar.OnRemoteAvatarLoad.RemoveListener(_avatarChangeListener);
            _avatarChangeListener = null;
        }
        if (VoiceOverrideOn) { VoiceOverrideOn = false; MutePuppetPipelineLocally(false); }
        _voiceMonitor?.Dispose();
        _voiceMonitor = null;
        // Token-stamped so the puppet can verify the stop came from this session's controller.
        PlayerRemoteControlMod.Instance.Transport.Send(TargetUuid, Authenticate(new[] { (byte)PacketType.StopControl }));

        // Undo the local wear (head-hide restore + un-hide the puppet). Doesn't touch the networked
        // avatar, which is restored just below.
        _wear?.Exit();
        _wear = null;

        // Switch back to the original avatar via the API so all players see the controller return. This
        // also reloads it locally, replacing the "_PLAYERLOCAL" puppet copy.
        if (_switchedInvisible && !string.IsNullOrEmpty(_originalAvatarGuid) && AssetManagement.Instance != null)
            AssetManagement.Instance.LoadLocalAvatar(_originalAvatarGuid);
        _switchedInvisible = false;

        PlayerRemoteControlMod.Log.Msg("Stopped controlling.");
    }
}