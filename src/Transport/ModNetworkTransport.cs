using System;
using ABI_RC.Systems.ModNetwork;

namespace PlayerRemoteControl.Transport;

// Default transport over CVR's in-instance ModNetwork channel. Both clients subscribe to the same
// message id and exchange addressed messages, with no external server. Payloads are capped around
// 1100 bytes, which the pose and AAS codecs stay under.
internal sealed class ModNetworkTransport : ITransport
{
    public bool IsReady { get; private set; }

    public event Action<string, byte[]> OnData;

    public void Init()
    {
        try
        {
            if (!ModNetworkManager.IsSubscribed(Settings.MessageId))
                ModNetworkManager.Subscribe(Settings.MessageId, OnMessage);
            IsReady = true;
            PlayerRemoteControlMod.Log.Msg("ModNetwork transport ready.");
        }
        catch (Exception e)
        {
            IsReady = false;
            PlayerRemoteControlMod.Log.Warning($"ModNetwork subscribe failed: {e.Message}");
        }
    }

    public void Shutdown()
    {
        try { if (ModNetworkManager.IsSubscribed(Settings.MessageId)) ModNetworkManager.Unsubscribe(Settings.MessageId); }
        catch { /* leaving the instance already tore it down */ }
        IsReady = false;
    }

    public void Send(string targetUuid, byte[] payload)
    {
        if (!IsReady || string.IsNullOrEmpty(targetUuid)) return;
        try
        {
            using var msg = new ModNetworkMessage(Settings.MessageId, targetUuid);
            msg.Write(payload);
            msg.Send();
        }
        catch (Exception e)
        {
            PlayerRemoteControlMod.Log.Warning($"ModNetwork send failed: {e.Message}");
        }
    }

    private void OnMessage(ModNetworkMessage msg)
    {
        try
        {
            msg.Read(out byte[] payload);
            if (payload != null && payload.Length > 0)
                OnData?.Invoke(msg.Sender, payload);
        }
        catch (Exception e)
        {
            PlayerRemoteControlMod.Log.Warning($"ModNetwork receive failed: {e.Message}");
        }
    }
}