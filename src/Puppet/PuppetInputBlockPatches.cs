using HarmonyLib;
using ABI_RC.Core.Player;
using ABI_RC.Core.Util.AnimatorManager;
using PlayerRemoteControl.Puppet;

namespace PlayerRemoteControl.Puppet;

// While being controlled, block the puppet's own live parameter input (menu / OSC / face tracking /
// gestures) from reaching its animator, so only the controller's data drives it. These three
// *_Animator methods are the choke point every local write funnels through. The mod's own writes flag
// themselves via PuppetSession.DrivingRemote so they pass.
internal static class PuppetInputBlock
{
    internal static bool Blocked(AvatarAnimatorManager manager)
    {
        if (PuppetSession.Active == null || PuppetSession.DrivingRemote) return false;
        var setup = PlayerSetup.Instance;
        return setup != null && ReferenceEquals(manager, setup.AnimatorManager);
    }
}

[HarmonyPatch(typeof(AvatarAnimatorManager), "SetFloatParameter_Animator")]
internal static class PuppetBlockFloat
{
    private static bool Prefix(AvatarAnimatorManager __instance) => !PuppetInputBlock.Blocked(__instance);
}

[HarmonyPatch(typeof(AvatarAnimatorManager), "SetIntParameter_Animator")]
internal static class PuppetBlockInt
{
    private static bool Prefix(AvatarAnimatorManager __instance) => !PuppetInputBlock.Blocked(__instance);
}

[HarmonyPatch(typeof(AvatarAnimatorManager), "SetBoolParameter_Animator")]
internal static class PuppetBlockBool
{
    private static bool Prefix(AvatarAnimatorManager __instance) => !PuppetInputBlock.Blocked(__instance);
}