using System;
using BTKUILib.UIObjects;
using BTKUILib.UIObjects.Components;
using ABI_RC.Core.Player;
using PlayerRemoteControl.Puppet;

namespace PlayerRemoteControl.UI;

// BTKUILib control panel (soft dependency, so all BTKUILib types stay confined here). A root page
// with a live status line and active-session panel, plus three sub-pages: Control a Player, Consent
// & Safety, and Whitelist.
public sealed class ControlUI
{
    private const string ModName = "PlayerRemoteControl";

    private readonly PlayerRemoteControlMod _mod;

    private Page _root;
    private TextBlock _status;
    private Category _activeCat;

    private Page _playersPage;
    private Category _playersCat;

    private Page _whitelistPage;
    private Category _whitelistUsersCat;
    private Category _whitelistAddCat;

    public ControlUI(PlayerRemoteControlMod mod) { _mod = mod; }

    public void Build()
    {
        _root = new Page(ModName, ModName, true, "")
        { MenuTitle = "Player Remote Control", MenuSubtitle = "Puppeteer a consenting player" };

        // --- root: live status + active-session controls ---
        var statusCat = _root.AddCategory("Status", showHeader: true);
        _status = statusCat.AddTextBlock("Idle.");

        var navCat = _root.AddCategory("Menus", showHeader: false);
        _playersPage = navCat.AddPage("Control a Player", "", "Pick someone in the instance to control", ModName);
        var consentPage = navCat.AddPage("Consent & Safety", "", "Your consent, free-look and panic settings", ModName);
        _whitelistPage = navCat.AddPage("Whitelist", "", "Restrict who is allowed to control you", ModName);

        _activeCat = _root.AddCategory("Active Session", showHeader: true);

        BuildPlayersPage();
        BuildConsentPage(consentPage);
        BuildWhitelistPage();

        RefreshPlayers();
        RefreshActive();
        RefreshWhitelist();
        UpdateStatus();
    }

    // --- Control a Player page ----------------------------------------------------------

    private void BuildPlayersPage()
    {
        _playersCat = _playersPage.AddCategory("Players in instance");
    }

    private void RefreshPlayers()
    {
        _playersCat.ClearChildren();

        var mgr = CVRPlayerManager.Instance;
        if (mgr == null || mgr.NetworkPlayers.Count == 0)
        {
            _playersCat.AddTextBlock("No other players in this instance.");
            return;
        }

        foreach (var p in mgr.NetworkPlayers)
        {
            if (p == null || string.IsNullOrEmpty(p.Uuid)) continue;
            string uuid = p.Uuid;
            var b = _playersCat.AddButton($"Control {p.Username}", "", "Request control of this player");
            b.OnPress += () => { _mod.StartControlling(uuid); UpdateStatus(); RefreshActive(); };
        }
    }

    // --- Consent & Safety page ----------------------------------------------------------

    private void BuildConsentPage(Page page)
    {
        var affect = page.AddCategory("Permissions");
        AddToggle(affect, "Allow being controlled", Settings.AllowBeingControlled);
        AddToggle(affect, "Allow moving my body", Settings.AllowLocomotion);
        AddToggle(affect, "Allow voice override", Settings.AllowVoiceOverride);
        AddToggle(affect, "Allow mute", Settings.AllowMute);
        AddToggle(affect, "Route my voice when controlling", Settings.RouteVoiceWhenControlling);

        var scope = page.AddCategory("Limb Overrides", showHeader: true, canCollapse: true, collapsed: true);
        scope.AddTextBlock("Choose what you want to be controlled.");
        // "Allow" is the inverse of Retain: on = controller drives this limb, off = you keep it.
        AddInverseToggle(scope, "Allow head", Settings.RetainHead);
        AddInverseToggle(scope, "Allow spine", Settings.RetainSpine);
        AddInverseToggle(scope, "Allow left arm", Settings.RetainLeftArm);
        AddInverseToggle(scope, "Allow right arm", Settings.RetainRightArm);
        AddInverseToggle(scope, "Allow left leg", Settings.RetainLeftLeg);
        AddInverseToggle(scope, "Allow right leg", Settings.RetainRightLeg);
        AddInverseToggle(scope, "Allow fingers", Settings.RetainFingers);
        AddToggle(scope, "Free look", Settings.FreeLookWhileControlled);
        AddToggle(scope, "Level horizon (no head roll)", Settings.LevelHorizonWhileControlled);

        var panic = page.AddCategory("Panic release");
        panic.AddTextBlock("You can always end being controlled instantly: press the ~ (tilde) key, or spam Jump 10 times within 10 seconds.");
    }

    // --- Whitelist page -----------------------------------------------------------------

    private void BuildWhitelistPage()
    {
        var cfg = _whitelistPage.AddCategory("Whitelist");
        AddToggle(cfg, "Whitelist only", Settings.WhitelistEnabled);
        cfg.AddTextBlock("When enabled, only whitelisted users can control you.");

        _whitelistUsersCat = _whitelistPage.AddCategory("Whitelisted users");
        _whitelistAddCat = _whitelistPage.AddCategory("Add from instance");
    }

    private void RefreshWhitelist()
    {
        _whitelistUsersCat.ClearChildren();
        var set = Settings.WhitelistSet();
        if (set.Count == 0)
            _whitelistUsersCat.AddTextBlock("No one whitelisted yet. Users without an override use your default consent/scope settings.");
        else
            foreach (var uuid in set)
            {
                string id = uuid;
                bool custom = Settings.HasUserProfile(id);
                var cat = _whitelistUsersCat;

                cat.AddTextBlock((custom ? "★ " : "") + NameFor(id)
                    + (custom ? " (custom permissions)" : " (using your defaults; changing a toggle creates an override)"));

                AddProfileToggle(cat, id, "Allow being controlled", p => p.AllowBeingControlled, (p, v) => p.AllowBeingControlled = v);
                AddProfileToggle(cat, id, "Allow moving my body", p => p.AllowLocomotion, (p, v) => p.AllowLocomotion = v);
                AddProfileToggle(cat, id, "Allow voice override", p => p.AllowVoiceOverride, (p, v) => p.AllowVoiceOverride = v);
                AddProfileToggle(cat, id, "Allow mute", p => p.AllowMute, (p, v) => p.AllowMute = v);
                AddProfileRegionToggle(cat, id, "Keep head + view (\"head only\")", BodyRegion.Head);
                AddProfileRegionToggle(cat, id, "Keep spine", BodyRegion.Spine);
                AddProfileRegionToggle(cat, id, "Keep left arm", BodyRegion.LeftArm);
                AddProfileRegionToggle(cat, id, "Keep right arm", BodyRegion.RightArm);
                AddProfileRegionToggle(cat, id, "Keep left leg", BodyRegion.LeftLeg);
                AddProfileRegionToggle(cat, id, "Keep right leg", BodyRegion.RightLeg);
                AddProfileRegionToggle(cat, id, "Keep fingers", BodyRegion.Fingers);

                if (custom)
                    cat.AddButton("↺ Use my defaults", "", "Remove this user's override").OnPress += () =>
                    { Settings.ClearUserProfile(id); RefreshWhitelist(); };
                cat.AddButton($"✕ Remove {NameFor(id)}", "", "Remove from whitelist").OnPress += () =>
                { Settings.SetWhitelisted(id, false); Settings.ClearUserProfile(id); RefreshWhitelist(); };
            }

        _whitelistAddCat.ClearChildren();
        var mgr = CVRPlayerManager.Instance;
        if (mgr == null || mgr.NetworkPlayers.Count == 0)
        {
            _whitelistAddCat.AddTextBlock("No other players in this instance.");
            return;
        }
        foreach (var p in mgr.NetworkPlayers)
        {
            if (p == null || string.IsNullOrEmpty(p.Uuid) || Settings.IsWhitelisted(p.Uuid)) continue;
            string uuid = p.Uuid;
            _whitelistAddCat.AddButton($"＋ {p.Username}", "", "Add to whitelist").OnPress += () =>
            { Settings.SetWhitelisted(uuid, true); RefreshWhitelist(); };
        }
    }

    // --- Active session (root) ----------------------------------------------------------

    private void RefreshActive()
    {
        _activeCat.ClearChildren();
        var session = _mod.Controlling;
        if (session == null)
        {
            _activeCat.AddTextBlock("Not controlling anyone. Open “Control a Player” to start.");
            return;
        }

        _activeCat.AddButton("■ Stop controlling", "", "End the control session").OnPress += () =>
        { _mod.StopControlling(); UpdateStatus(); RefreshActive(); };

        var mute = _activeCat.AddToggle("Mute puppet", "Silence the puppet's mic (needs their consent)", false);
        mute.OnValueUpdated += v => session.SetMute(v);
    }

    // --- helpers ------------------------------------------------------------------------

    private static void AddToggle(Category cat, string label, MelonLoader.MelonPreferences_Entry<bool> entry)
    {
        var t = cat.AddToggle(label, label, entry.Value);
        t.OnValueUpdated += v => entry.Value = v;
    }

    // Toggle displayed as the inverse of its backing entry: the Retain* entry means "keep it myself",
    // so allow = !retain.
    private static void AddInverseToggle(Category cat, string label, MelonLoader.MelonPreferences_Entry<bool> entry)
    {
        var t = cat.AddToggle(label, label, !entry.Value);
        t.OnValueUpdated += v => entry.Value = !v;
    }

    // Per-user profile toggle: reads the user's resolved profile (default if no override yet); any
    // change writes back an explicit override.
    private void AddProfileToggle(Category cat, string uuid, string label,
        Func<PermissionProfile, bool> get, Action<PermissionProfile, bool> set)
    {
        var t = cat.AddToggle(label, label, get(Settings.GetUserProfile(uuid)));
        t.OnValueUpdated += v =>
        {
            var p = Settings.GetUserProfile(uuid);
            set(p, v);
            Settings.SetUserProfile(uuid, p);
        };
    }

    private void AddProfileRegionToggle(Category cat, string uuid, string label, BodyRegion region)
    {
        var t = cat.AddToggle(label, label, (Settings.GetUserProfile(uuid).Retain & region) != 0);
        t.OnValueUpdated += v =>
        {
            var p = Settings.GetUserProfile(uuid);
            if (v) p.Retain |= region; else p.Retain &= ~region;
            Settings.SetUserProfile(uuid, p);
        };
    }

    private static string NameFor(string uuid)
    {
        var mgr = CVRPlayerManager.Instance;
        if (mgr != null && mgr.UserIdToPlayerEntity.TryGetValue(uuid, out var e) && e != null && !string.IsNullOrEmpty(e.Username))
            return e.Username;
        return uuid.Length > 8 ? uuid.Substring(0, 8) + "…" : uuid;
    }

    private void UpdateStatus()
    {
        if (_status == null) return;
        var controlling = _mod.Controlling;
        var controlled = _mod.Controlled;
        if (controlling != null)
            _status.Text = controlling.Accepted
                ? $"Controlling {NameFor(controlling.TargetUuid)}."
                : $"Requesting control of {NameFor(controlling.TargetUuid)}…";
        else if (controlled != null)
            _status.Text = $"Being controlled by {NameFor(controlled.ControllerUuid)}.";
        else
            _status.Text = "Idle.";
    }

    private bool _hadControlling;
    private bool _hadControlled;
    private int _rosterSig = -1;

    // Signature of the current instance roster: changes whenever someone joins or leaves.
    private static int RosterSignature()
    {
        var mgr = CVRPlayerManager.Instance;
        if (mgr == null) return 0;
        int sig = 17;
        foreach (var p in mgr.NetworkPlayers)
            if (p != null && !string.IsNullOrEmpty(p.Uuid)) sig = sig * 31 + p.Uuid.GetHashCode();
        return sig;
    }

    // Poll control state so the status line + active panel refresh when a session starts or
    // ends on its own (handshake timeout, decline, avatar-load failure, panic release, etc.).
    // Also rebuild the roster-driven lists when players join or leave, so no manual refresh is needed.
    public void Tick()
    {
        bool controlling = _mod.Controlling != null;
        bool controlled = _mod.Controlled != null;
        if (controlling != _hadControlling || controlled != _hadControlled)
        {
            _hadControlling = controlling;
            _hadControlled = controlled;
            RefreshActive();
            UpdateStatus();
        }

        int sig = RosterSignature();
        if (sig != _rosterSig)
        {
            _rosterSig = sig;
            RefreshPlayers();
            RefreshWhitelist();
        }
    }
}