using System.IO;
using UnityEngine;
using ABI_RC.Core.Player;

namespace PlayerRemoteControl.Transport;

// Compact (de)serialization of a PlayerAvatarMovementData snapshot: the whole per-tick
// control payload. Lossless float32 lands ~665 bytes, under the ~1100-byte ModNetwork cap. The AAS
// blob goes separately (AasCodec) since advanced avatars can push it past the cap alone. Field order
// mirrors PlayerAvatarMovementData so this stays easy to audit against the game struct.
internal static class PoseCodec
{
    // The controller's viewpoint rides alongside the pose so the puppet anchors its camera to what the
    // controller sees. Stable across emotes, unlike sampling the driven head bone locally.
    internal static byte[] Encode(PlayerAvatarMovementData d, Vector3 viewPos, Quaternion viewRot)
    {
        using var ms = new MemoryStream(768);
        using var w = new BinaryWriter(ms);
        w.Write((byte)PacketType.Pose);

        for (int i = 0; i < PlayerAvatarMovementData.TotalMuscles; i++) w.Write(d.MuscleValues[i]);
        for (int i = 0; i < d.FaceTrackingData.Length; i++) w.Write(d.FaceTrackingData[i]);

        WriteV3(w, d.RootPosition);
        WriteV3(w, d.RootRotation);
        WriteV3(w, d.BodyPosition);
        WriteV3(w, d.BodyRotation);
        WriteV3(w, d.RelativeHipRotation);

        w.Write(d.AnimatorMovementX);
        w.Write(d.AnimatorMovementY);
        w.Write(d.AnimatorEmote);
        w.Write(d.AnimatorGestureLeft);
        w.Write(d.AnimatorGestureRight);
        w.Write(d.AnimatorToggle);

        byte flags = 0;
        if (d.AnimatorGrounded)  flags |= 1 << 0;
        if (d.AnimatorSitting)   flags |= 1 << 1;
        if (d.AnimatorCrouching) flags |= 1 << 2;
        if (d.AnimatorFlying)    flags |= 1 << 3;
        if (d.AnimatorProne)     flags |= 1 << 4;
        if (d.AnimatorCancelEmote) flags |= 1 << 5;
        if (d.UseIndividualFingers) flags |= 1 << 6;
        if (d.FaceTrackingEnabled)  flags |= 1 << 7;
        w.Write(flags);

        byte eyeCam = 0;
        if (d.EyeTrackingOverride) eyeCam |= 1 << 0;
        if (d.EyeBlinkingOverride) eyeCam |= 1 << 1;
        if (d.CameraEnabled)       eyeCam |= 1 << 2;
        w.Write(eyeCam);

        WriteV3(w, d.EyeTrackingPosition);
        w.Write(d.EyeTrackingBlinkProgressLeft);
        w.Write(d.EyeTrackingBlinkProgressRight);

        WriteV3(w, d.CameraPosition);
        WriteV3(w, d.CameraRotation);

        w.Write((byte)d.DeviceType);
        w.Write(d.ContinuityVersion);

        WriteV3(w, viewPos);
        w.Write(viewRot.x); w.Write(viewRot.y); w.Write(viewRot.z); w.Write(viewRot.w);

        return ms.ToArray();
    }

    // Decode into an existing buffer (reused to avoid per-frame allocs), skipping payload[0]
    // (the PacketType). Also yields the controller's viewpoint.
    internal static void Decode(byte[] payload, PlayerAvatarMovementData d, out Vector3 viewPos, out Quaternion viewRot)
    {
        using var ms = new MemoryStream(payload);
        using var r = new BinaryReader(ms);
        r.ReadByte(); // PacketType.Pose

        for (int i = 0; i < PlayerAvatarMovementData.TotalMuscles; i++) d.MuscleValues[i] = r.ReadSingle();
        for (int i = 0; i < d.FaceTrackingData.Length; i++) d.FaceTrackingData[i] = r.ReadSingle();

        d.RootPosition = ReadV3(r);
        d.RootRotation = ReadV3(r);
        d.BodyPosition = ReadV3(r);
        d.BodyRotation = ReadV3(r);
        d.RelativeHipRotation = ReadV3(r);

        d.AnimatorMovementX = r.ReadSingle();
        d.AnimatorMovementY = r.ReadSingle();
        d.AnimatorEmote = r.ReadSingle();
        d.AnimatorGestureLeft = r.ReadSingle();
        d.AnimatorGestureRight = r.ReadSingle();
        d.AnimatorToggle = r.ReadSingle();

        byte flags = r.ReadByte();
        d.AnimatorGrounded    = (flags & (1 << 0)) != 0;
        d.AnimatorSitting     = (flags & (1 << 1)) != 0;
        d.AnimatorCrouching   = (flags & (1 << 2)) != 0;
        d.AnimatorFlying      = (flags & (1 << 3)) != 0;
        d.AnimatorProne       = (flags & (1 << 4)) != 0;
        d.AnimatorCancelEmote = (flags & (1 << 5)) != 0;
        d.UseIndividualFingers= (flags & (1 << 6)) != 0;
        d.FaceTrackingEnabled = (flags & (1 << 7)) != 0;

        byte eyeCam = r.ReadByte();
        d.EyeTrackingOverride = (eyeCam & (1 << 0)) != 0;
        d.EyeBlinkingOverride = (eyeCam & (1 << 1)) != 0;
        d.CameraEnabled       = (eyeCam & (1 << 2)) != 0;

        d.EyeTrackingPosition = ReadV3(r);
        d.EyeTrackingBlinkProgressLeft = r.ReadSingle();
        d.EyeTrackingBlinkProgressRight = r.ReadSingle();

        d.CameraPosition = ReadV3(r);
        d.CameraRotation = ReadV3(r);

        d.DeviceType = (PlayerAvatarMovementData.UsingDeviceType)r.ReadByte();
        d.ContinuityVersion = r.ReadByte();

        viewPos = ReadV3(r);
        viewRot = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    }

    private static void WriteV3(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    private static Vector3 ReadV3(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}