using System.Collections.Generic;
using UnityEngine;
using ABI_RC.Core.Player;

namespace PlayerRemoteControl;

// First-person head-hide for the local player's avatar. Registers head-hide for only the local view
// cameras (desktopCam, vrCam, and the VR eye cams), exactly like CVR's own
// PlayerCloneDistanceDisabler. Mirror/photo cameras are deliberately left out, so the head still
// shows in mirrors and only first person loses it.
// CVR's distance disabler toggles head-hide by camera-to-viewpoint distance, which misfires here
// (the view is overridden to the controller's camera on a custom-loaded avatar) and makes the head
// flicker. This disables that component and re-asserts the same camera set every frame instead.
internal sealed class HeadHide
{
    private PlayerCloneDistanceDisabler _distanceDisabler;
    private readonly List<Camera> _registered = new();
    private bool _active;

    internal void Enable() => _active = true;

    internal void Update()
    {
        if (!_active) return;
        var setup = PlayerSetup.Instance;
        if (setup == null) return;

        // Custom "_PLAYERLOCAL" loads may lack the clone the head is hidden on.
        var ao = setup.AvatarObject;
        if (ao != null && ao.GetComponent<AvatarClone>() == null) ao.AddComponent<AvatarClone>();

        // Stop CVR's distance-based toggling from fighting this (the source of the flicker).
        if (_distanceDisabler == null) _distanceDisabler = Object.FindObjectOfType<PlayerCloneDistanceDisabler>();
        if (_distanceDisabler != null) _distanceDisabler.enabled = false;

        // Exactly CVR's own set and nothing else, so mirrors keep the head.
        Register(setup.desktopCam);
        Register(setup.vrCam);
        var fmp = setup.FakeMultiPassHack;
        if (fmp != null && fmp.IsInitialized) { Register(fmp.LeftEyeCam); Register(fmp.RightEyeCam); }
    }

    private void Register(Camera cam)
    {
        if (cam == null) return;
        AvatarClone.SetHeadHiddenCamera(cam, true); // HashSet.Add, cheap to re-assert each frame
        if (!_registered.Contains(cam)) _registered.Add(cam);
    }

    internal void Disable()
    {
        _active = false;
        _registered.Clear();

        // Hand head-hide back to the distance disabler in a consistent state. Don't unregister the view
        // cameras: leaving them hidden matches IsDistanceDisabled = false, so it keeps the head hidden
        // in first person and only unregisters when the camera legitimately goes far (third person).
        // Unregistering here while re-enabling the disabler left it seeing no state change, so it never
        // re-hid the head and the head stayed visible after control ended.
        if (_distanceDisabler != null)
        {
            _distanceDisabler.IsDistanceDisabled = false;
            _distanceDisabler.enabled = true;
            _distanceDisabler = null;
        }
    }
}