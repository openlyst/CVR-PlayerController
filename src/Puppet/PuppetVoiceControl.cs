using ABI_RC.Systems.Communications;
using PlayerRemoteControl.Transport;

namespace PlayerRemoteControl.Puppet;

// Puppet-side voice while controlled:
//  - Mute (gated by AllowMute): force the mic muted every frame so no observer hears the puppet.
//    This is the only thing that silences them. Reasserted per-frame because other systems toggle
//    Comms_Manager.IsMicMuted.
//  - Voice override (gated by AllowVoiceOverride): the controller's Opus frames are mixed into the
//    puppet's outgoing voice (VoiceMixer), so observers hear the controller from the puppet and CVR
//    drives the puppet's visemes from that audio. The controller's own pipeline is also fed locally
//    so the puppet hears them. The real mic stays live, so both voices come out of the puppet.
internal sealed class PuppetVoiceControl
{
    private bool _mute;
    private bool _voiceOverride;
    private bool _savedMicMuted;

    internal bool VoiceOverrideActive => _voiceOverride;

    internal void SetMute(bool on)
    {
        if (on == _mute) return;
        if (on) _savedMicMuted = Comms_Manager.IsMicMuted;
        _mute = on;
        if (!on) Comms_Manager.IsMicMuted = _savedMicMuted;
        PlayerRemoteControlMod.Log.Msg($"Puppet mute {(on ? "ON" : "OFF")}.");
    }

    internal void SetVoiceOverride(bool on)
    {
        if (on == _voiceOverride) return;
        // Consent is enforced by the caller (PuppetSession gates on the resolved profile).
        _voiceOverride = on;
        PlayerRemoteControlMod.Log.Msg($"Puppet voice override {(on ? "ON" : "OFF")}.");
    }

    // Reassert the explicit mute each frame (voice override leaves the mic live).
    private string _controllerUuid;

    internal void Update(string controllerUuid)
    {
        _controllerUuid = controllerUuid;
        if (_mute) Comms_Manager.IsMicMuted = true;
    }

    internal void OnRemoteVoiceFrame(string controllerUuid, byte[] payload)
    {
        if (!_voiceOverride) return;

        if (!VoiceCodec.Decode(payload, out ushort seq, out byte[] data)) return;

        // (1) Down-mix into the puppet's outgoing mic stream, so one clean combined stream reaches
        // observers and drives their visemes.
        VoiceMixer.Enqueue(data);

        // (2) Local monitor: feed the controller's own pipeline so the puppet hears them, in stereo
        // (2D) so the voice sits evenly in both ears.
        var client = Comms_Manager.Instance?.Client;
        if (client != null && client.FindParticipantPipeline(controllerUuid, out var pipe) && pipe != null)
        {
            pipe._shouldRead = true;
            pipe._shouldWrite = true;
            if (pipe.AudioSource != null) pipe.AudioSource.spatialBlend = 0f;
            try { pipe.OnDataReceived(seq, data); } catch { }
        }
    }

    internal void Restore()
    {
        VoiceMixer.Clear();
        if (_mute) Comms_Manager.IsMicMuted = _savedMicMuted;
        _mute = false;
        _voiceOverride = false;

        // Return the controller's pipeline to normal spatialization.
        var client = Comms_Manager.Instance?.Client;
        if (!string.IsNullOrEmpty(_controllerUuid) && client != null &&
            client.FindParticipantPipeline(_controllerUuid, out var pipe) && pipe != null && pipe.AudioSource != null)
            pipe.AudioSource.spatialBlend = 1f;
    }
}