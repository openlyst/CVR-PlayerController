using System.Collections.Generic;
using UnityEngine;
using ABI_RC.Core.Player;
using PlayerRemoteControl.Puppet;

namespace PlayerRemoteControl.Puppet;

// Anchors the puppet's camera to the controller's transmitted viewpoint each frame, so the puppet
// sees what the controller sees. Hooks Camera.onPreCull and, for the local view cameras, stamps the
// received camera pose after the normal HMD/desktop pose driver has run. The target is fed in via
// SetTarget (from the pose packet's viewpoint).
internal sealed class PuppetViewOverride
{
    private int _anchorFrame = -1;
    private bool _hooked;

    // The desktop view camera is a leaf under desktopCameraRig/Pivot/Camera. A world-space
    // SetPositionAndRotation back-solves a new local offset on the leaf that nothing resets, so the
    // skew persists after overriding stops. (VR self-heals: the HMD driver rewrites the local transform
    // each frame.) Snapshot each camera's local transform once before the first override and restore it
    // on Disable. Keyed by Camera, and restoring the VR camera is a harmless no-op.
    private readonly Dictionary<Camera, (Vector3 pos, Quaternion rot)> _camSaved = new();

    private Vector3 _targetPos;
    private Quaternion _targetRot = Quaternion.identity;
    internal bool HasView;

    // Just enough smoothing to ride over a dropped network frame without visible hitching. Advanced at
    // a fixed 60 Hz for consistent feel. Tau is tiny (~30 ms), so lag is barely perceptible.
    private Vector3 _smoothPos;
    private Quaternion _smoothRot = Quaternion.identity;
    private bool _smoothInit;
    private float _accum;
    private const float StepHz = 60f;
    private const float SmoothTau = 0.03f;

    // When false (free-look), still anchor the view position to the controller but leave rotation to
    // the puppet's own look, so they can look around independently.
    internal bool RotationLocked = true;

    // Comfort: when locked, drop the controller's head roll so the puppet gets only pitch and yaw. A
    // tilted horizon is the worst offender for VR sim-sickness. This keeps it level while still
    // following where the controller looks.
    internal bool LevelHorizon;

    internal void SetTarget(Vector3 pos, Quaternion rot)
    {
        _targetPos = pos;
        _targetRot = LevelHorizon ? StripRoll(rot) : rot;
        HasView = true;
    }

    // Rebuild rotation from its forward direction with world-up, discarding roll. Guard the
    // straight-up/down singularity where forward is parallel to up and LookRotation degenerates.
    private static Quaternion StripRoll(Quaternion rot)
    {
        Vector3 fwd = rot * Vector3.forward;
        if (Mathf.Abs(Vector3.Dot(fwd, Vector3.up)) > 0.9999f) return rot;
        return Quaternion.LookRotation(fwd, Vector3.up);
    }

    // Force the next pre-cull to re-stamp the camera even if it already ran this frame, so
    // toggling free-look resumes driving immediately instead of lingering on the last orientation.
    internal void ForceReanchor() { _anchorFrame = -1; _smoothInit = false; }

    // Advance the smoothed camera transform toward the latest target in fixed 60 Hz steps.
    private void AdvanceSmoothing()
    {
        if (!_smoothInit) { _smoothPos = _targetPos; _smoothRot = _targetRot; _smoothInit = true; return; }
        _accum += Time.deltaTime;
        float step = 1f / StepHz;
        float k = 1f - Mathf.Exp(-step / SmoothTau);
        int guard = 0;
        while (_accum >= step && guard++ < 8)
        {
            _smoothPos = Vector3.Lerp(_smoothPos, _targetPos, k);
            _smoothRot = Quaternion.Slerp(_smoothRot, _targetRot, k);
            _accum -= step;
        }
        if (_accum > step) _accum = 0f; // drop backlog after a hitch instead of fast-forwarding
    }

    internal void Enable()
    {
        if (_hooked) return;
        Camera.onPreCull += OnCameraPreCull;
        _hooked = true;
    }

    internal void Disable()
    {
        if (!_hooked) return;
        Camera.onPreCull -= OnCameraPreCull;
        _hooked = false;
        RestoreCameras();
        HasView = false;
        _smoothInit = false;
    }

    private void OnCameraPreCull(Camera cam)
    {
        if (!HasView) return;
        var setup = PlayerSetup.Instance;
        if (setup == null) return;
        bool isView = (setup.vrCamera != null && cam.gameObject == setup.vrCamera)
                   || (setup.desktopCamera != null && cam.gameObject == setup.desktopCamera);
        if (!isView) return;
        if (Time.frameCount == _anchorFrame) return;
        _anchorFrame = Time.frameCount;

        AdvanceSmoothing();

        // Capture the pristine local transform once, before the first world-space write on this cam.
        if (!_camSaved.ContainsKey(cam))
            _camSaved[cam] = (cam.transform.localPosition, cam.transform.localRotation);

        if (RotationLocked)
            cam.transform.SetPositionAndRotation(_smoothPos, _smoothRot);
        else
            cam.transform.position = _smoothPos; // position only; keep the puppet's own look

        // Keep the AudioListener glued to the driven view. It has its own HMD-following driver, so
        // moving only the camera leaves it at the puppet's real head: view and audio disagree, and the
        // two drivers beat against each other and pulse spatialized voices. CVR re-drives it from the
        // HMD each frame, so this self-heals when the session ends (no restore needed).
        var listener = setup.activeAudioListener;
        if (listener != null)
        {
            if (RotationLocked)
                listener.transform.SetPositionAndRotation(_smoothPos, _smoothRot);
            else
                listener.transform.position = _smoothPos;
        }
    }

    // Restore each touched camera's leaf-local transform. Local, not world: by exit the rig/pivot has
    // moved, so only the offset relative to the live pivot is meaningful. Two assignments avoid relying
    // on SetLocalPositionAndRotation (not guaranteed on this Unity).
    private void RestoreCameras()
    {
        foreach (var kv in _camSaved)
        {
            if (kv.Key == null) continue;
            kv.Key.transform.localPosition = kv.Value.pos;
            kv.Key.transform.localRotation = kv.Value.rot;
        }
        _camSaved.Clear();
    }
}