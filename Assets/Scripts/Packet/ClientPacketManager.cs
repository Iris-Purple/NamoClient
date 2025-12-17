using Google.Protobuf;
using Google.Protobuf.Protocol;
using ServerCore;
using System;
using System.Collections.Generic;

class PacketManager
{
	#region Singleton
	static PacketManager _instance = new PacketManager();
	public static PacketManager Instance { get { return _instance; } }
	#endregion

	PacketManager()
	{
		Register();
	}

	Dictionary<ushort, Action<PacketSession, ArraySegment<byte>, ushort>> _onRecv = new Dictionary<ushort, Action<PacketSession, ArraySegment<byte>, ushort>>();
	Dictionary<ushort, Action<PacketSession, IMessage>> _handler = new Dictionary<ushort, Action<PacketSession, IMessage>>();
		
	public Action<PacketSession, IMessage, ushort> CustomHandler { get; set; }

	public void Register()
	{		
		_onRecv.Add((ushort)PacketId.PktS2CEnterGame, MakePacket<S2C_ENTER_GAME>);
		_handler.Add((ushort)PacketId.PktS2CEnterGame, PacketHandler.S_EnterGameHandler);		
		_onRecv.Add((ushort)PacketId.PktS2CLeaveGame, MakePacket<S2C_LEAVE_GAME>);
		_handler.Add((ushort)PacketId.PktS2CLeaveGame, PacketHandler.S_LeaveGameHandler);		
		_onRecv.Add((ushort)PacketId.PktS2CSpawn, MakePacket<S2C_SPAWN>);
		_handler.Add((ushort)PacketId.PktS2CSpawn, PacketHandler.S_SpawnHandler);		
		_onRecv.Add((ushort)PacketId.PktS2CDespawn, MakePacket<S2C_DESPAWN>);
		_handler.Add((ushort)PacketId.PktS2CDespawn, PacketHandler.S_DespawnHandler);		
		_onRecv.Add((ushort)PacketId.PktS2CMove, MakePacket<S2C_MOVE>);
		_handler.Add((ushort)PacketId.PktS2CMove, PacketHandler.S_MoveHandler);		
		_onRecv.Add((ushort)PacketId.PktS2CSkill, MakePacket<S2C_SKILL>);
		_handler.Add((ushort)PacketId.PktS2CSkill, PacketHandler.S_SkillHandler);
	}

	public void OnRecvPacket(PacketSession session, ArraySegment<byte> buffer)
	{
		ushort count = 0;

		ushort size = BitConverter.ToUInt16(buffer.Array, buffer.Offset);
		count += 2;
		ushort id = BitConverter.ToUInt16(buffer.Array, buffer.Offset + count);
		count += 2;

		Action<PacketSession, ArraySegment<byte>, ushort> action = null;
		if (_onRecv.TryGetValue(id, out action))
			action.Invoke(session, buffer, id);
	}

	void MakePacket<T>(PacketSession session, ArraySegment<byte> buffer, ushort id) where T : IMessage, new()
	{
		T pkt = new T();
		pkt.MergeFrom(buffer.Array, buffer.Offset + 4, buffer.Count - 4);

		if (CustomHandler != null)
		{
			CustomHandler.Invoke(session, pkt, id);
		}
		else
		{
			Action<PacketSession, IMessage> action = null;
			if (_handler.TryGetValue(id, out action))
				action.Invoke(session, pkt);
		}
	}

	public Action<PacketSession, IMessage> GetPacketHandler(ushort id)
	{
		Action<PacketSession, IMessage> action = null;
		if (_handler.TryGetValue(id, out action))
			return action;
		return null;
	}
}