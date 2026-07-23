using System;

namespace PlayerRemoteControl.Transport;

// How control frames travel controller→puppet. Default is ModNetworkTransport over
// CVR's in-instance channel. Every payload's first byte is a PacketType, so
// control/pose/AAS/voice all share one channel.
internal interface ITransport
{
    bool IsReady { get; }

    void Init();
    void Shutdown();

    // Send a payload to one player. Payload[0] = PacketType.
    void Send(string targetUuid, byte[] payload);

    // Receiving client: (senderUuid, payload).
    event Action<string, byte[]> OnData;
}

// First byte of every control-channel payload.
internal enum PacketType : byte
{
    Pose = 1,             // PlayerAvatarMovementData snapshot
    Aas = 2,              // advanced-avatar-settings blob (float[]/int[]/byte[])
    ControllerVoice = 3,  // controller → puppet: one Opus frame [ushort seq][byte[] data]
    PuppetVoice = 4,      // puppet → controller: the puppet's OWN Opus frame (side-channel monitor)
    RequestControl = 10, // controller → puppet: "may I control you?" (scope resolved puppet-side)
    AcceptControl = 11,  // puppet → controller: consent granted
    StopControl = 12,    // either side: end the session
    DeclineControl = 15, // puppet → controller: mod present but consent (AllowBeingControlled) is off
    SetMute = 13,        // controller → puppet: mute toggle (1 byte on/off)
    SetVoiceOverride = 14, // controller → puppet: voice-override toggle (1 byte on/off)
}