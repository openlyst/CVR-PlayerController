using HarmonyLib;
using UnityEngine;
using ABI_RC.Core.Player;
using PlayerRemoteControl.Transport;

namespace PlayerRemoteControl.Controller;

// While an accepted ControlSession is active, stream the controller's solved pose and
// advanced-avatar-settings blob to the puppet, tapped at the same points CVR builds them, so the
// puppet receives the same data other players normally would.
[HarmonyPatch(typeof(PlayerSetup))]
internal static class ControllerCapturePatches
{
    // Per-frame pose: UpdatePlayerAvatarMovementData rebuilds the local movement data from the
    // animator, so snapshot the result and send it.
    [HarmonyPostfix]
    [HarmonyPatch("UpdatePlayerAvatarMovementData")]
    private static void CapturePose_Postfix(PlayerSetup __instance)
    {
        var session = PlayerRemoteControlMod.Instance?.Controlling;
        if (session == null || !session.Accepted) return;

        var data = __instance.GetPlayerMovementData();
        if (data == null) return;

        // Ship the real active-camera world transform (not a bone) so the puppet sees exactly what the
        // controller sees, falling back to the calibrated viewpoint if neither camera is active.
        var camObj = (__instance.vrCamera != null && __instance.vrCamera.activeInHierarchy) ? __instance.vrCamera
                   : (__instance.desktopCamera != null && __instance.desktopCamera.activeInHierarchy) ? __instance.desktopCamera
                   : null;
        Vector3 viewPos; Quaternion viewRot;
        if (camObj != null) { viewPos = camObj.transform.position; viewRot = camObj.transform.rotation; }
        else { viewPos = __instance.GetViewWorldPosition(); viewRot = __instance.GetViewWorldRotation(); }

        var payload = PoseCodec.Encode(data, viewPos, viewRot);
        PlayerRemoteControlMod.Instance.Transport.Send(session.TargetUuid, session.Authenticate(payload));
        session.PoseFramesSent++;

        // After capturing the true pose for the puppet, raise the outgoing buffer's root so the invisible
        // body's nameplate sits above the puppet's for observers. Only affects what CVR sends. The real
        // body/camera and the puppet's driven position keep the true ground height, so there's no feedback.
        float raise = session.NetworkedRootRaise;
        if (raise > 0f)
        {
            var p = data.RootPosition;
            p.y += raise;
            data.RootPosition = p;
        }
    }

    // AAS blob (~10 Hz), the same data other players receive: face tracking, OSC, contacts. Only sent
    // on change, to stay under the cap.
    [HarmonyPrefix]
    [HarmonyPatch("SendAdvancedAvatarSettings")]
    private static void CaptureAas_Prefix(PlayerSetup __instance)
    {
        var session = PlayerRemoteControlMod.Instance?.Controlling;
        if (session == null || !session.Accepted) return;

        var am = __instance.AnimatorManager;
        if (am == null || !am.AASParameterChangedSinceLastSync) return;
        var desc = __instance.AvatarDescriptor;
        if (desc == null || !desc.avatarUsesAdvancedSettings) return;

        var sync = am.GetAASParameterSyncData();
        var payload = AasCodec.Encode(sync.AdditionalParametersFloat, sync.AdditionalParametersInt, sync.AdditionalParametersByte);
        PlayerRemoteControlMod.Instance.Transport.Send(session.TargetUuid, session.Authenticate(payload));
    }
}