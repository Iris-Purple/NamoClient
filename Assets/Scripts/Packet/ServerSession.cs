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
		ushort size = (ushort)packet.CalculateSize();
		byte[] sendBuffer = new byte[size + 4];
		Array.Copy(BitConverter.GetBytes((ushort)(size + 4)), 0, sendBuffer, 0, sizeof(ushort));
		Array.Copy(BitConverter.GetBytes((ushort)msgId), 0, sendBuffer, 2, sizeof(ushort));
		Array.Copy(packet.ToByteArray(), 0, sendBuffer, 4, size);
		Send(new ArraySegment<byte>(sendBuffer));
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
				// 첫 글자 또는 숫자 바로 뒤 글자는 대문자
				if (i == 0 || char.IsDigit(part[i - 1]))
					result.Append(char.ToUpper(part[i]));
				else
					result.Append(char.ToLower(part[i]));
			}
		}
		return result.ToString();
	}
}