using System.Globalization;
using PlayerRemoteControl.Puppet;

namespace PlayerRemoteControl;

// The permissions a puppet grants for one control session: what the controller may do and which body
// regions the puppet keeps. Resolved per requesting user (a per-user override if present, else the
// global default) and drives the whole session: consent gate, locomotion, voice, and muscle mask.
internal sealed class PermissionProfile
{
    internal bool AllowBeingControlled;
    internal bool AllowLocomotion = true;
    internal bool AllowVoiceOverride;
    internal bool AllowMute;

    // Regions the puppet keeps driving themselves, while the controller drives the rest. Retaining the head
    // is the "head only" scenario: puppet keeps head + view, controller drives the body.
    internal BodyRegion Retain = BodyRegion.None;

    // Effective retained regions.
    internal BodyRegion EffectiveRetain => Retain;

    // Free-look is forced whenever the puppet retains their head.
    internal bool ForceFreeLook => (Retain & BodyRegion.Head) != 0;

    internal PermissionProfile Clone() => new()
    {
        AllowBeingControlled = AllowBeingControlled,
        AllowLocomotion = AllowLocomotion,
        AllowVoiceOverride = AllowVoiceOverride,
        AllowMute = AllowMute,
        Retain = Retain,
    };

    // Compact per-user storage: "bc,loc,vo,mute,0,retainInt". Field 5 is a reserved "0" (was the old
    // HeadOnly flag), kept so the retain bitmask stays at a stable position. Parsing tolerates missing
    // trailing fields.
    internal string Serialize()
    {
        int r = (int)Retain;
        return string.Join(",",
            B(AllowBeingControlled), B(AllowLocomotion), B(AllowVoiceOverride),
            B(AllowMute), "0", r.ToString(CultureInfo.InvariantCulture));
    }

    internal static PermissionProfile Deserialize(string s)
    {
        var p = new PermissionProfile();
        if (string.IsNullOrWhiteSpace(s)) return p;
        var f = s.Split(',');
        if (f.Length > 0) p.AllowBeingControlled = f[0] == "1";
        if (f.Length > 1) p.AllowLocomotion = f[1] == "1";
        if (f.Length > 2) p.AllowVoiceOverride = f[2] == "1";
        if (f.Length > 3) p.AllowMute = f[3] == "1";
        // f[4] reserved (old HeadOnly); folded into Retain's Head bit now.
        if (f.Length > 5 && int.TryParse(f[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
            p.Retain = (BodyRegion)r;
        return p;
    }

    private static string B(bool v) => v ? "1" : "0";
}