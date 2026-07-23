using MelonLoader;
using ABI_RC.Core.Savior;
using ABI_RC.Systems.GameEventSystem;
using PlayerRemoteControl.Controller;
using PlayerRemoteControl.Puppet;
using PlayerRemoteControl.Transport;

namespace PlayerRemoteControl;

// Entry point + session owner. A client is idle, controlling one player (ControlSession),
// or being controlled by one (PuppetSession), never more than one at a time. Consent is
// enforced puppet-side. An incoming RequestControl is only honored if AllowBeingControlled is set.
public sealed class PlayerRemoteControlMod : MelonMod
{
    internal static PlayerRemoteControlMod Instance { get; private set; }
    internal static MelonLogger.Instance Log => Instance.LoggerInstance;

    internal ITransport Transport { get; private set; }
    internal ControlSession Controlling { get; private set; }
    internal PuppetSession Controlled { get; private set; }

    private UI.ControlUI _ui;
    private bool _uiTried;

    internal static string LocalUuid => MetaPort.Instance != null ? MetaPort.Instance.ownerId : null;

    public override void OnInitializeMelon()
    {
        Instance = this;
        Settings.Init();

        Transport = new ModNetworkTransport();
        Transport.OnData += OnData;

        // ModNetwork needs a live instance connection before it can subscribe.
        CVRGameEventSystem.Instance.OnConnected.AddListener(_ => Transport.Init());
        CVRGameEventSystem.Instance.OnDisconnected.AddListener(_ => { StopAll(); Transport.Shutdown(); });

        LoggerInstance.Msg("PlayerRemoteControl initialized (idle).");
    }

    // --- session lifecycle -------------------------------------------------------------

    internal void StartControlling(string targetUuid)
    {
        if (Controlled != null) { LoggerInstance.Warning("Can't control while being controlled."); return; }
        Controlling?.Stop();
        Controlling = new ControlSession(targetUuid);
        Controlling.RequestControl();
    }

    internal void StopControlling()
    {
        Controlling?.Stop();
        Controlling = null;
    }

    private void StopAll()
    {
        Controlling?.Stop(); Controlling = null;
        Controlled?.Stop();  Controlled = null;
    }

    // Puppet-initiated release (panic buttons). Ends being controlled and reports it.
    internal void StopBeingControlled(string reason)
    {
        if (Controlled == null) return;
        LoggerInstance.Msg(reason);
        if (ABI_RC.Core.InteractionSystem.ViewManager.Instance != null)
            ABI_RC.Core.InteractionSystem.ViewManager.Instance.NotifyUser("Player Remote Control", reason, 3f);
        Controlled.Stop();
        Controlled = null;
    }

    // --- inbound routing ---------------------------------------------------------------

    private void OnData(string sender, byte[] payload)
    {
        if (payload == null || payload.Length == 0) return;
        var type = (PacketType)payload[0];

        switch (type)
        {
            case PacketType.RequestControl:
                // Always reply, accept or decline. The reply itself proves the mod is present, so the
                // controller distinguishes "no mod" (timeout) from "no consent" (decline).
                var profile = Settings.ResolveProfile(sender);
                if (!profile.AllowBeingControlled)
                {
                    LoggerInstance.Msg($"Declining control request from {sender} (consent off).");
                    Transport.Send(sender, new[] { (byte)PacketType.DeclineControl });
                    return;
                }
                if (Settings.WhitelistEnabled.Value && !Settings.IsWhitelisted(sender))
                {
                    LoggerInstance.Msg($"Declining control request from {sender} (not whitelisted).");
                    Transport.Send(sender, new[] { (byte)PacketType.DeclineControl });
                    return;
                }
                if (Controlling != null)
                {
                    LoggerInstance.Msg("Declining control request while controlling someone.");
                    Transport.Send(sender, new[] { (byte)PacketType.DeclineControl });
                    return;
                }
                Controlled?.Stop();
                Controlled = new PuppetSession(sender, profile);
                Controlled.AcceptControl();
                LoggerInstance.Msg($"Accepted control from {sender}.");
                break;

            case PacketType.AcceptControl:
                if (Controlling != null && Controlling.TargetUuid == sender) Controlling.OnAccepted(payload);
                break;

            case PacketType.DeclineControl:
                if (Controlling != null && Controlling.TargetUuid == sender) Controlling.OnDeclined();
                break;

            case PacketType.StopControl:
                if (Controlled != null && Controlled.ControllerUuid == sender) { Controlled.Stop(); Controlled = null; }
                if (Controlling != null && Controlling.TargetUuid == sender) { Controlling.Stop(); Controlling = null; }
                break;

            case PacketType.Pose:
            case PacketType.Aas:
            case PacketType.SetMute:
            case PacketType.SetVoiceOverride:
            case PacketType.ControllerVoice:
                // Only the controller who holds this session's token can drive it (blocks sender spoofing).
                if (Controlled != null && Controlled.ControllerUuid == sender
                    && Controlled.TryAuthenticate(payload, out var inner))
                    Controlled.OnControlData(type, inner);
                break;

            case PacketType.PuppetVoice:
                // Side-channel: the puppet's own voice for the controller's stereo monitor.
                if (Controlling != null && Controlling.TargetUuid == sender
                    && Controlling.TryAuthenticate(payload, out var pv))
                    Controlling.OnPuppetVoiceFrame(pv);
                break;
        }
    }

    // --- per-frame ---------------------------------------------------------------------

    public override void OnUpdate()
    {
        if (!_uiTried) TryBuildUI();
        _ui?.Tick();

        Controlling?.Update();
        Controlled?.Update();
    }

    private void TryBuildUI()
    {
        _uiTried = true;
        try { _ui = new UI.ControlUI(this); _ui.Build(); LoggerInstance.Msg("BTKUILib panel registered."); }
        catch (System.Exception e) { _ui = null; LoggerInstance.Msg($"BTKUILib unavailable ({e.GetType().Name}); panel disabled."); }
    }
}