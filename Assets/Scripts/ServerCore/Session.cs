using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace ServerCore
{
	public abstract class PacketSession : Session
	{
		public static readonly int HeaderSize = 2;

		// [size(2)][packetId(2)][ ... ][size(2)][packetId(2)][ ... ]
		/*
		public sealed override int OnRecv(ArraySegment<byte> buffer)
		{
			int processLen = 0;

			while (true)
			{
				// 최소한 헤더는 파싱할 수 있는지 확인
				if (buffer.Count < HeaderSize)
					break;

				// 패킷이 완전체로 도착했는지 확인
				ushort dataSize = BitConverter.ToUInt16(buffer.Array, buffer.Offset);
				if (buffer.Count < dataSize)
					break;

				// 여기까지 왔으면 패킷 조립 가능
				OnRecvPacket(new ArraySegment<byte>(buffer.Array, buffer.Offset, dataSize));
				processLen += dataSize;
				buffer = new ArraySegment<byte>(buffer.Array, buffer.Offset + dataSize, buffer.Count - dataSize);
			}

		
			return processLen;
		}
		*/

		public sealed override int OnRecv(ArraySegment<byte> buffer)
		{
			int processLen = 0;

			while (true)
			{
				if (buffer.Count < HeaderSize)
					break;

				ushort dataSize = BitConverter.ToUInt16(buffer.Array, buffer.Offset);
				if (buffer.Count < dataSize)
					break;

				// 암호화 ON -> 복호화 처리
				if (Session.EncryptionEnabled && _crypto != null)
				{
					int encryptedPayloadSize = dataSize - 2;
					byte[] encryptedPayload = new byte[encryptedPayloadSize];
					Array.Copy(buffer.Array, buffer.Offset + 2, encryptedPayload, 0, encryptedPayloadSize);

					byte[] decrypted = _crypto.Decrypt(encryptedPayload);
					if (decrypted == null)
						return -1;  // 복호화 실패

					// 복호화된 패킷 재구성: [size(2)][id(2)][data...]
					int decryptedPacketSize = 2 + decrypted.Length;
					byte[] decryptedPacket = new byte[decryptedPacketSize];
					Array.Copy(BitConverter.GetBytes((ushort)decryptedPacketSize), 0, decryptedPacket, 0, 2);
					Array.Copy(decrypted, 0, decryptedPacket, 2, decrypted.Length);

					OnRecvPacket(new ArraySegment<byte>(decryptedPacket, 0, decryptedPacketSize));
				}
				else
				{
					OnRecvPacket(new ArraySegment<byte>(buffer.Array, buffer.Offset, dataSize));
				}

				processLen += dataSize;
				buffer = new ArraySegment<byte>(buffer.Array, buffer.Offset + dataSize, buffer.Count - dataSize);
			}

			return processLen;
		}

		public abstract void OnRecvPacket(ArraySegment<byte> buffer);
	}
	


	public abstract class Session
	{
		Socket _socket;
		int _disconnected = 0;

		RecvBuffer _recvBuffer = new RecvBuffer(65535);

		object _lock = new object();
		Queue<ArraySegment<byte>> _sendQueue = new Queue<ArraySegment<byte>>();
		List<ArraySegment<byte>> _pendingList = new List<ArraySegment<byte>>();
		SocketAsyncEventArgs _sendArgs = new SocketAsyncEventArgs();
		SocketAsyncEventArgs _recvArgs = new SocketAsyncEventArgs();

		public abstract void OnConnected(EndPoint endPoint);
		public abstract int  OnRecv(ArraySegment<byte> buffer);
		public abstract void OnSend(int numOfBytes);
		public abstract void OnDisconnected(EndPoint endPoint);

		public static bool EncryptionEnabled = true;  // 암호화 활성화 플래그
		protected AESCrypto _crypto;

		void Clear()
		{
			lock (_lock)
			{
				_sendQueue.Clear();
				_pendingList.Clear();
			}
		}

		public void Start(Socket socket)
		{
			_socket = socket;

			if (EncryptionEnabled)
			{
				_crypto = new AESCrypto();
				_crypto.Init(AESCrypto.DefaultKey);
			}

			_recvArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnRecvCompleted);
			_sendArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnSendCompleted);

			RegisterRecv();
		}

		public void Send(List<ArraySegment<byte>> sendBuffList)
		{
			if (sendBuffList.Count == 0)
				return;

			lock (_lock)
			{
				foreach (ArraySegment<byte> sendBuff in sendBuffList)
					_sendQueue.Enqueue(sendBuff);

				if (_pendingList.Count == 0)
					RegisterSend();
			}
		}

		public void Send(ArraySegment<byte> sendBuff)
		{
			if (EncryptionEnabled && _crypto != null)
			{
				sendBuff = EncryptBuffer(sendBuff);
				if (sendBuff.Array == null)
					return;
			}

			lock (_lock)
			{
				_sendQueue.Enqueue(sendBuff);
				if (_pendingList.Count == 0)
					RegisterSend();
			}
		}

		private ArraySegment<byte> EncryptBuffer(ArraySegment<byte> buffer)
		{
			// 원본: [size(2)][id(2)][data...]
			// 암호화: [size(2)][encrypted(id+data)...]

			int plainSize = buffer.Count;
			if (plainSize < 2)
				return buffer;

			// id+data 부분만 암호화 (size 제외)
			int payloadSize = plainSize - 2;
			byte[] payload = new byte[payloadSize];
			Array.Copy(buffer.Array, buffer.Offset + 2, payload, 0, payloadSize);

			byte[] encrypted = _crypto.Encrypt(payload);
			if (encrypted == null)
				return new ArraySegment<byte>(null, 0, 0);

			// 새 버퍼: [size(2)][encrypted]
			int totalSize = 2 + encrypted.Length;
			byte[] result = new byte[totalSize];

			// size 기록
			Array.Copy(BitConverter.GetBytes((ushort)totalSize), 0, result, 0, 2);
			// encrypted 복사
			Array.Copy(encrypted, 0, result, 2, encrypted.Length);

			return new ArraySegment<byte>(result);
		}

		public void Disconnect()
		{
			if (Interlocked.Exchange(ref _disconnected, 1) == 1)
				return;

			OnDisconnected(_socket.RemoteEndPoint);
			_socket.Shutdown(SocketShutdown.Both);
			_socket.Close();
			Clear();
		}

		#region 네트워크 통신

		void RegisterSend()
		{
			if (_disconnected == 1)
				return;

			while (_sendQueue.Count > 0)
			{
				ArraySegment<byte> buff = _sendQueue.Dequeue();
				_pendingList.Add(buff);
			}
			_sendArgs.BufferList = _pendingList;

			try
			{
				bool pending = _socket.SendAsync(_sendArgs);
				if (pending == false)
					OnSendCompleted(null, _sendArgs);
			}
			catch (Exception e)
			{
				Debug.Log($"RegisterSend Failed {e}");
			}
		}

		void OnSendCompleted(object sender, SocketAsyncEventArgs args)
		{
			lock (_lock)
			{
				if (args.BytesTransferred > 0 && args.SocketError == SocketError.Success)
				{
					try
					{
						_sendArgs.BufferList = null;
						_pendingList.Clear();

						OnSend(_sendArgs.BytesTransferred);

						if (_sendQueue.Count > 0)
							RegisterSend();
					}
					catch (Exception e)
					{
						Debug.Log($"OnSendCompleted Failed {e}");
					}
				}
				else
				{
					Disconnect();
				}
			}
		}

		void RegisterRecv()
		{
			if (_disconnected == 1)
				return;

			_recvBuffer.Clean();
			ArraySegment<byte> segment = _recvBuffer.WriteSegment;
			_recvArgs.SetBuffer(segment.Array, segment.Offset, segment.Count);

			try
			{
				bool pending = _socket.ReceiveAsync(_recvArgs);
				if (pending == false)
					OnRecvCompleted(null, _recvArgs);
			}
			catch (Exception e)
			{
				Debug.Log($"RegisterRecv Failed {e}");
			}
		}

		void OnRecvCompleted(object sender, SocketAsyncEventArgs args)
		{
			if (args.BytesTransferred > 0 && args.SocketError == SocketError.Success)
			{
				try
				{
					// Write 커서 이동
					if (_recvBuffer.OnWrite(args.BytesTransferred) == false)
					{
						Disconnect();
						return;
					}

					// 컨텐츠 쪽으로 데이터를 넘겨주고 얼마나 처리했는지 받는다
					int processLen = OnRecv(_recvBuffer.ReadSegment);
					if (processLen < 0 || _recvBuffer.DataSize < processLen)
					{
						Disconnect();
						return;
					}

					// Read 커서 이동
					if (_recvBuffer.OnRead(processLen) == false)
					{
						Disconnect();
						return;
					}

					RegisterRecv();
				}
				catch (Exception e)
				{
					Debug.Log($"OnRecvCompleted Failed {e}");
				}
			}
			else
			{
				Disconnect();
			}
		}

		#endregion
	}
}
