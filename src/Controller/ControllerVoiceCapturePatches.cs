using HarmonyLib;
using ABI_RC.Systems.Communications.Networking;
using PlayerRemoteControl.Transport;

namespace PlayerRemoteControl.Controller;

// Controller: while controlling with voice-override on, intercept the outgoing Opus frames, forward
// each to the puppet (who mixes it into their own stream), and skip the local send, so the
// controller's voice comes out of the puppet rather than the invisible parked body. The puppet's side
// of the plumbing lives in CaptureMixPatch on the audio thread.
[HarmonyPatch(typeof(Comms_Client), "SendVoiceData")]
internal static class VoiceCaptureBridge
{
    private static bool Prefix(ushort sequence, byte[] data)
    {
        var cs = PlayerRemoteControlMod.Instance?.Controlling;
        if (cs != null && cs.Accepted && cs.VoiceOverrideOn)
        {
            PlayerRemoteControlMod.Instance.Transport.Send(cs.TargetUuid, cs.Authenticate(VoiceCodec.Encode(sequence, data, PacketType.ControllerVoice)));
            return false; // the puppet carries the voice now
        }
        return true;
    }
}