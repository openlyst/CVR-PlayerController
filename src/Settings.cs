using System.Collections.Generic;
using MelonLoader;
using PlayerRemoteControl.Puppet;

namespace PlayerRemoteControl;

// MelonPreferences-backed consent + config. Every remote action is gated on the target's own flag,
// checked on the target's client before anything is applied, so a controller can never force a
// behavior the puppet hasn't opted into.
internal static class Settings
{
    // Premade fully-invisible avatar used to hide the controller. Swap in a different published GUID
    // to use your own (see ControlSession).
    internal static readonly string InvisibleAvatarGuid = "01f39187-064e-45dd-9b8f-386f431f8d8b";

    // ModNetwork subscription id (<= 32 chars); both clients must share it.
    internal const string MessageId = "PlayerRemoteControl.v1";

    private static MelonPreferences_Category _cat;

    internal static MelonPreferences_Entry<bool> AllowBeingControlled;   // puppet consents to IK/param control
    internal static MelonPreferences_Entry<bool> AllowVoiceOverride;     // puppet consents to controller voice routed through it
    internal static MelonPreferences_Entry<bool> AllowMute;              // puppet consents to being mic-muted by the controller
    internal static MelonPreferences_Entry<bool> RouteVoiceWhenControlling; // controller-side: route my voice through whoever I control
    internal static MelonPreferences_Entry<bool> FreeLookWhileControlled; // puppet-local: don't lock view to the controller
    internal static MelonPreferences_Entry<bool> LevelHorizonWhileControlled; // puppet-local comfort: drop head roll from the locked view
    internal static MelonPreferences_Entry<bool> AllowLocomotion;         // consent: controller may move my body through the world
    internal static MelonPreferences_Entry<bool> WhitelistEnabled;        // if on, only whitelisted users may control us
    internal static MelonPreferences_Entry<string> Whitelist;            // comma-separated user IDs allowed to control us

    // Default control-scope (the profile used when a requester has no per-user override).
    internal static MelonPreferences_Entry<bool> RetainHead;             // keep my neck+head (and view); "head only" scenario
    internal static MelonPreferences_Entry<bool> RetainSpine;            // keep my spine/chest
    internal static MelonPreferences_Entry<bool> RetainLeftArm;
    internal static MelonPreferences_Entry<bool> RetainRightArm;
    internal static MelonPreferences_Entry<bool> RetainLeftLeg;
    internal static MelonPreferences_Entry<bool> RetainRightLeg;
    internal static MelonPreferences_Entry<bool> RetainFingers;          // keep my own finger tracking

    // Per-user permission overrides, serialized as "uuid=profile;uuid2=profile" (see PermissionProfile).
    internal static MelonPreferences_Entry<string> UserProfiles;

    internal static void Init()
    {
        _cat = MelonPreferences.CreateCategory("PlayerRemoteControl", "Player Remote Control");
        AllowBeingControlled = _cat.CreateEntry("AllowBeingControlled", false,
            "Allow being controlled", description: "Let a consenting player puppeteer your avatar's pose and parameters.");
        AllowVoiceOverride = _cat.CreateEntry("AllowVoiceOverride", false,
            "Allow voice override", description: "Let the controller's voice be routed through your avatar (proxy backend only).");
        AllowMute = _cat.CreateEntry("AllowMute", false,
            "Allow mute", description: "Let the controller mute your microphone while controlling you.");
        RouteVoiceWhenControlling = _cat.CreateEntry("RouteVoiceWhenControlling", false,
            "Route my voice when controlling", description: "While you control someone, route your voice through them (they still have to allow voice override).");
        FreeLookWhileControlled = _cat.CreateEntry("FreeLookWhileControlled", false,
            "Free look while controlled", description: "Look around independently instead of having your view locked to the controller (your body is still driven).");
        LevelHorizonWhileControlled = _cat.CreateEntry("LevelHorizonWhileControlled", false,
            "Level horizon (no head roll)", description: "Comfort: when your view is locked to the controller, ignore their head roll so you only get pitch and yaw, like desktop. Keeps the horizon level to reduce motion sickness.");
        AllowLocomotion = _cat.CreateEntry("AllowLocomotion", true,
            "Allow moving my body", description: "Let the controller move you through the world. Off = you're puppeted in place (they can pose you but not walk you around).");
        WhitelistEnabled = _cat.CreateEntry("WhitelistEnabled", false,
            "Whitelist only", description: "Only allow players on your whitelist to control you.");
        Whitelist = _cat.CreateEntry("Whitelist", "",
            "Whitelist (user IDs)", description: "Comma-separated user IDs allowed to control you.");

        RetainHead = _cat.CreateEntry("RetainHead", false,
            "Keep my head", description: "Retain control of your own neck+head and view while the controller drives the rest (the \"head only\" scenario).");
        RetainSpine = _cat.CreateEntry("RetainSpine", false,
            "Keep my spine", description: "Retain control of your own spine/chest.");
        RetainLeftArm = _cat.CreateEntry("RetainLeftArm", false, "Keep my left arm", description: "Retain your own left arm.");
        RetainRightArm = _cat.CreateEntry("RetainRightArm", false, "Keep my right arm", description: "Retain your own right arm.");
        RetainLeftLeg = _cat.CreateEntry("RetainLeftLeg", false, "Keep my left leg", description: "Retain your own left leg.");
        RetainRightLeg = _cat.CreateEntry("RetainRightLeg", false, "Keep my right leg", description: "Retain your own right leg.");
        RetainFingers = _cat.CreateEntry("RetainFingers", false, "Keep my fingers", description: "Retain your own finger tracking.");

        UserProfiles = _cat.CreateEntry("UserProfiles", "",
            "Per-user permission overrides", description: "Advanced: per-user permission profiles (edit via the Whitelist page).");
    }

    // --- permission profiles -----------------------------------------------------------

    // The global default profile, built live from the individual consent + retain toggles.
    internal static PermissionProfile DefaultProfile()
    {
        var p = new PermissionProfile
        {
            AllowBeingControlled = AllowBeingControlled.Value,
            AllowLocomotion = AllowLocomotion.Value,
            AllowVoiceOverride = AllowVoiceOverride.Value,
            AllowMute = AllowMute.Value,
        };
        var r = BodyRegion.None;
        if (RetainHead.Value) r |= BodyRegion.Head;
        if (RetainSpine.Value) r |= BodyRegion.Spine;
        if (RetainLeftArm.Value) r |= BodyRegion.LeftArm;
        if (RetainRightArm.Value) r |= BodyRegion.RightArm;
        if (RetainLeftLeg.Value) r |= BodyRegion.LeftLeg;
        if (RetainRightLeg.Value) r |= BodyRegion.RightLeg;
        if (RetainFingers.Value) r |= BodyRegion.Fingers;
        p.Retain = r;
        return p;
    }

    private static Dictionary<string, PermissionProfile> ProfileMap()
    {
        var map = new Dictionary<string, PermissionProfile>();
        var s = UserProfiles.Value;
        if (string.IsNullOrWhiteSpace(s)) return map;
        foreach (var entry in s.Split(';'))
        {
            var t = entry.Trim();
            if (t.Length == 0) continue;
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            var uuid = t.Substring(0, eq);
            map[uuid] = PermissionProfile.Deserialize(t.Substring(eq + 1));
        }
        return map;
    }

    private static void SaveProfileMap(Dictionary<string, PermissionProfile> map)
    {
        var parts = new List<string>();
        foreach (var kv in map) parts.Add(kv.Key + "=" + kv.Value.Serialize());
        UserProfiles.Value = string.Join(";", parts);
    }

    // True if this user has an explicit per-user override.
    internal static bool HasUserProfile(string uuid) => ProfileMap().ContainsKey(uuid);

    // The per-user override for this user, or a copy of the default if none exists.
    internal static PermissionProfile GetUserProfile(string uuid)
        => ProfileMap().TryGetValue(uuid, out var p) ? p : DefaultProfile();

    internal static void SetUserProfile(string uuid, PermissionProfile profile)
    {
        if (string.IsNullOrEmpty(uuid)) return;
        var map = ProfileMap();
        map[uuid] = profile;
        SaveProfileMap(map);
    }

    internal static void ClearUserProfile(string uuid)
    {
        var map = ProfileMap();
        if (map.Remove(uuid)) SaveProfileMap(map);
    }

    // Resolve the profile that governs a control session from this requester: their
    // per-user override if present, otherwise the global default.
    internal static PermissionProfile ResolveProfile(string uuid) => GetUserProfile(uuid);

    // --- whitelist helpers -------------------------------------------------------------

    internal static HashSet<string> WhitelistSet()
    {
        var set = new HashSet<string>();
        var s = Whitelist.Value;
        if (!string.IsNullOrWhiteSpace(s))
            foreach (var part in s.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) set.Add(t);
            }
        return set;
    }

    internal static bool IsWhitelisted(string uuid) => WhitelistSet().Contains(uuid);

    internal static void SetWhitelisted(string uuid, bool on)
    {
        if (string.IsNullOrEmpty(uuid)) return;
        var set = WhitelistSet();
        bool changed = on ? set.Add(uuid) : set.Remove(uuid);
        if (changed) Whitelist.Value = string.Join(",", set);
    }
}