using HarmonyLib;
using ABI_RC.Systems.IK;
using ABI_RC.Core.Player;
using PlayerRemoteControl.Puppet;

namespace PlayerRemoteControl.Patches;

// Puppet: after VRIK solves from the puppet's own (suppressed) trackers, overwrite the pose with the
// controller's received pose.
[HarmonyPatch(typeof(IKSystem), "OnPostSolverUpdateGeneral")]
internal static class PostSolvePatch
{
    private static void Postfix() => PuppetSession.Active?.ApplyToAnimator();
}

// Puppet: overwrite the outgoing movement-data buffer with the controller's pose so the puppet's
// normal network sync carries it to every other player. Runs after PlayerSetup rebuilds the buffer.
[HarmonyPatch(typeof(PlayerSetup), "UpdatePlayerAvatarMovementData")]
internal static class PuppetOutgoingPatch
{
    private static void Postfix(PlayerSetup __instance) => PuppetSession.Active?.ApplyToOutgoing(__instance);
}

// Puppet: suppress the puppet's own advanced-avatar-settings send while controlled. The local AAS
// store still holds the puppet's default params (the controller's go straight to the animator), so
// letting this run would send wrong params and fight the re-broadcast in PuppetSession.BroadcastAas.
[HarmonyPatch(typeof(PlayerSetup), "SendAdvancedAvatarSettings")]
internal static class PuppetAasSuppressPatch
{
    private static bool Prefix() => PuppetSession.Active == null;
}