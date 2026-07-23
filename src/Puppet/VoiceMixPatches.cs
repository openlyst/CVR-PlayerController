using HarmonyLib;
using ABI_RC.Systems.Communications.Audio.Components;

namespace PlayerRemoteControl.Puppet;

// Puppet-side voice down-mixing on CVR's audio thread. Hooks Comms_CapturePipeline.OnDataReceived,
// where the mic PCM is processed and the capture amplitude (which drives the puppet's own visemes) is
// computed. The prefix forwards the puppet's clean pre-mix frame to the controller's monitor, then
// mixes the controller's queued voice into the PCM. Because the mix lands before amplitude/VAD, the
// puppet's avatar lip-syncs to the controller too, the gate opens, and the combined PCM becomes one
// clean Opus stream for observers. The postfix forces the gate open if the mixed amplitude still fell
// below the VAD threshold, so the controller's voice is never dropped.
[HarmonyPatch(typeof(Comms_CapturePipeline), "OnDataReceived", new[] { typeof(float[]), typeof(int), typeof(bool), typeof(int) })]
internal static class CaptureMixPatch
{
    // Prefix→method→postfix run sequentially on the same audio thread, so a plain static flag is safe.
    private static bool _mixedThisFrame;

    private static void Prefix(float[] data)
    {
        _mixedThisFrame = false;
        var ps = PuppetSession.Active;
        if (ps == null || !ps.VoiceOverrideActive) return;

        // Side-channel: forward the puppet's OWN clean (pre-mix) voice → controller's stereo monitor.
        var packet = VoiceMixer.BuildSideChannelPacket(data);
        if (packet != null) PlayerRemoteControlMod.Instance.Transport.Send(ps.ControllerUuid, ps.Authenticate(packet));

        // Mix the controller's voice into the mic PCM before amplitude/VAD is computed.
        _mixedThisFrame = VoiceMixer.MixInto(data);
    }

    private static void Postfix(ref bool __result)
    {
        if (_mixedThisFrame) __result = true; // ensure the mixed-in controller voice is transmitted
    }
}