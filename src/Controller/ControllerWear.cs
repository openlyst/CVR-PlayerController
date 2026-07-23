using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;
using ABI_RC.Core.IO;
using ABI_RC.Core.Player;
using ABI_RC.Core.Savior;
using ABI_RC.Core.EventSystem;
using ABI_RC.Core.Networking.API.Responses;

namespace PlayerRemoteControl.Controller;

// Controller-side "wear the puppet": load a local-only copy of the puppet's avatar onto the local
// player (first-person view of them) and hide the real puppet avatar locally so there aren't two
// overlapping bodies. The networked avatar stays the invisible premade (owned by ControlSession);
// this "_PLAYERLOCAL" instantiation replaces only the local render and is never networked.
// A custom "_PLAYERLOCAL" load doesn't register the view cameras as head-hidden, and the distance
// disabler would wipe any registration, so head-hide is set up here against every camera.
internal sealed class ControllerWear
{
    internal static ControllerWear Active { get; private set; }

    private readonly string _puppetUuid;
    private bool _localAvatarReady;

    private Renderer[] _puppetRenderers;
    private readonly HeadHide _headHide = new();

    internal ControllerWear(string puppetUuid) { _puppetUuid = puppetUuid; }

    internal void Enter()
    {
        Active = this;
        _headHide.Enable();

        if (CVRPlayerManager.Instance == null ||
            !CVRPlayerManager.Instance.UserIdToPlayerEntity.TryGetValue(_puppetUuid, out var puppet) || puppet == null)
        {
            PlayerRemoteControlMod.Log.Warning("Wear: puppet entity not found; skipping local wear.");
            return;
        }

        var meta = puppet.ContentMetadata;
        if (string.IsNullOrEmpty(meta.AssetId))
        {
            PlayerRemoteControlMod.Log.Warning("Wear: puppet avatar metadata unavailable; skipping local wear.");
            return;
        }

        HidePuppetLocally(puppet);

        // router=null reuses the puppet's already-loaded "assetId/fileHash" prefab, copied onto
        // "_PLAYERLOCAL" with no download; CVR falls back to the blocked-avatar prefab if it isn't loaded.
        string localUuid = MetaPort.Instance != null ? MetaPort.Instance.ownerId : "";
        MelonCoroutines.Start(LoadLocalAvatar(meta.AssetId, meta.FileHash, meta.CompatibilityVersion, localUuid));

        PlayerRemoteControlMod.Log.Msg($"Wearing {puppet.Username}'s avatar locally.");
    }

    private IEnumerator LoadLocalAvatar(string assetId, string fileHash, CompatibilityVersions compat, string localUuid)
    {
        _localAvatarReady = false;
        yield return CVRObjectLoader.Instance.InstantiateAvatarFromBundle(
            assetId, fileHash, "_PLAYERLOCAL", null, null, "", compat, localUuid);
        _localAvatarReady = true;
        _headHide.Enable(); // (re)register head-hidden cameras against the freshly loaded avatar
        PlayerRemoteControlMod.Log.Msg("Wear: local avatar loaded.");
    }

    // Hide the real puppet avatar locally only. forceRenderingOff keeps transforms/IK live (networked
    // pose still works) but drops the overlapping second body from view.
    private void HidePuppetLocally(CVRPlayerEntity puppet)
    {
        if (puppet.AvatarHolder == null) return;
        _puppetRenderers = puppet.AvatarHolder.GetComponentsInChildren<Renderer>(true);
        foreach (var r in _puppetRenderers) if (r != null) r.forceRenderingOff = true;
    }

    internal void Update()
    {
        // Re-assert the puppet stays hidden locally (culling controllers can re-enable renderers).
        if (_puppetRenderers != null)
            foreach (var r in _puppetRenderers) if (r != null) r.forceRenderingOff = true;

        if (!_localAvatarReady) return;
        _headHide.Update();
    }

    internal void Exit()
    {
        if (Active == this) Active = null;
        _headHide.Disable();

        // Show the puppet's real avatar again locally.
        if (_puppetRenderers != null)
            foreach (var r in _puppetRenderers) if (r != null) r.forceRenderingOff = false;
        _puppetRenderers = null;

        // Don't reload the avatar here. ControlSession owns the networked avatar and switches back on
        // Stop. That switch also reloads locally, replacing this "_PLAYERLOCAL" copy. Reloading here
        // too would fire a second AvatarSwitch API call.
        _localAvatarReady = false;
        PlayerRemoteControlMod.Log.Msg("Wear ended (head-hide restored; networked avatar restored by ControlSession).");
    }
}