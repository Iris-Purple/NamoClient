using Google.Protobuf;
using Google.Protobuf.Protocol;
using ServerCore;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Text;
using UnityEngine;

public class ServerSession : PacketSession
{
    public void Send(IMessage packet)
    {
        string msgName = "Pkt" + ToPascalCase(packet.GetType().Name);
        PacketId msgId = (PacketId)Enum.Parse(typeof(PacketId), msgName);

        ushort dataSize = (ushort)packet.CalculateSize();
        ushort packetSize = (ushort)(dataSize + HeaderSize);
        byte[] sendBuffer = new byte[packetSize];
        // size (2 bytes)
        Array.Copy(BitConverter.GetBytes(packetSize), 0, sendBuffer, 0, 2);
        // id (2 bytes)
        Array.Copy(BitConverter.GetBytes((ushort)msgId), 0, sendBuffer, 2, 2);
        // flags (1 byte)
        sendBuffer[4] = NeedsSequence(msgId) ? PKT_FLAG_HAS_SEQUENCE : (byte)0;
        // sequence (4 bytes) - Send()에서 설정됨
        Array.Copy(BitConverter.GetBytes((uint)0), 0, sendBuffer, 5, 4);
        // data
        Array.Copy(packet.ToByteArray(), 0, sendBuffer, HeaderSize, dataSize);

        Send(new ArraySegment<byte>(sendBuffer));
    }

    // Sequence가 필요한 패킷 여부
    private bool NeedsSequence(PacketId packetId)
    {
        switch (packetId)
        {
            case PacketId.PktC2SSkill:
                return true;
            default:
                return false;
        }
    }

    public override void OnConnected(EndPoint endPoint)
    {
        Debug.Log($"OnConnected : {endPoint}");

        C2S_ENTER_GAME enterPacket = new C2S_ENTER_GAME();
        Managers.Network.Send(enterPacket);

        PacketManager.Instance.CustomHandler = (s, m, i) =>
        {
            PacketQueue.Instance.Push(i, m);
        };
    }

    public override void OnDisconnected(EndPoint endPoint)
    {
        Debug.Log($"OnDisconnected : {endPoint}");
    }

    public override void OnRecvPacket(ArraySegment<byte> buffer)
    {
        PacketManager.Instance.OnRecvPacket(this, buffer);
    }

    public override void OnSend(int numOfBytes)
    {
        //Console.WriteLine($"Transferred bytes: {numOfBytes}");
    }

    private string ToPascalCase(string input)
    {
        var parts = input.Split('_');
        var result = new StringBuilder();

        foreach (var part in parts)
        {
            for (int i = 0; i < part.Length; i++)
            {
                if (i == 0 || char.IsDigit(part[i - 1]))
                    result.Append(char.ToUpper(part[i]));
                else
                    result.Append(char.ToLower(part[i]));
            }
        }
        return result.ToString();
    }
}
