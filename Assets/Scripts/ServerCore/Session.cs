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
        public static readonly int HeaderSize = 9;  // size(2) + id(2) + flags(1) + seq(4)
        public const byte PKT_FLAG_HAS_SEQUENCE = 0x01;

        // Sequence 카운터 (리플레이 공격 방지)
        protected uint _recvSeq = 0;

        public sealed override int OnRecv(ArraySegment<byte> buffer)
        {
            int processLen = 0;

            while (true)
            {
                if (buffer.Count < 2)
                    break;

                ushort dataSize = BitConverter.ToUInt16(buffer.Array, buffer.Offset);

                // 최소 크기 검증 (암호화 여부에 따라 다름)
                int minSize = EncryptionEnabled
                    ? (2 + 16 + AESCrypto.HMAC_SIZE)  // size + AES블록 + HMAC
                    : HeaderSize;                      // 평문 헤더

                if (dataSize < minSize)
                {
                    Debug.LogError($"Invalid packet size: {dataSize}");
                    return -1;
                }

                if (buffer.Count < dataSize)
                    break;

                // 암호화 ON -> HMAC 검증 + 복호화
                if (EncryptionEnabled && _crypto != null)
                {
                    // 패킷 구조: [size(2)][encrypted][HMAC(32)]
                    int encryptedPayloadSize = dataSize - 2 - AESCrypto.HMAC_SIZE;

                    if (encryptedPayloadSize < AESCrypto.BLOCK_SIZE)
                    {
                        Debug.LogError("Invalid packet size for HMAC");
                        return -1;
                    }

                    int encryptedOffset = buffer.Offset + 2;
                    int hmacOffset = buffer.Offset + 2 + encryptedPayloadSize;

                    // HMAC 검증
                    if (!_crypto.VerifyHMAC(buffer.Array, encryptedOffset, encryptedPayloadSize,
                                           buffer.Array, hmacOffset))
                    {
                        Debug.LogError("HMAC verification failed - packet tampered!");
                        return -1;
                    }

                    // 복호화 진행
                    byte[] encryptedPayload = new byte[encryptedPayloadSize];
                    Array.Copy(buffer.Array, encryptedOffset, encryptedPayload, 0, encryptedPayloadSize);

                    byte[] decrypted = _crypto.Decrypt(encryptedPayload);
                    if (decrypted == null)
                    {
                        Debug.LogError("Decryption failed");
                        return -1;
                    }

                    // 복호화된 패킷 크기 검증
                    int decryptedPacketSize = 2 + decrypted.Length;
                    if (decryptedPacketSize < HeaderSize)
                    {
                        Debug.LogError($"Decrypted packet too small: {decryptedPacketSize}");
                        return -1;
                    }

                    // 복호화된 패킷 재구성
                    byte[] decryptedPacket = new byte[decryptedPacketSize];
                    Array.Copy(BitConverter.GetBytes((ushort)decryptedPacketSize), 0, decryptedPacket, 0, 2);
                    Array.Copy(decrypted, 0, decryptedPacket, 2, decrypted.Length);

                    // Sequence 검증
                    byte flags = decryptedPacket[4];
                    if ((flags & PKT_FLAG_HAS_SEQUENCE) != 0)
                    {
                        uint sequence = BitConverter.ToUInt32(decryptedPacket, 5);
                        if (sequence <= _recvSeq)
                        {
                            Debug.LogError($"Replay attack detected: seq={sequence}, lastSeq={_recvSeq}");
                            return -1;
                        }
                        _recvSeq = sequence;
                    }

                    OnRecvPacket(new ArraySegment<byte>(decryptedPacket, 0, decryptedPacketSize));
                }
                else
                {
                    // 평문 - Sequence 검증
                    byte flags = buffer.Array[buffer.Offset + 4];
                    if ((flags & PKT_FLAG_HAS_SEQUENCE) != 0)
                    {
                        uint sequence = BitConverter.ToUInt32(buffer.Array, buffer.Offset + 5);
                        if (sequence <= _recvSeq)
                        {
                            Debug.LogError($"Replay attack detected: seq={sequence}, lastSeq={_recvSeq}");
                            return -1;
                        }
                        _recvSeq = sequence;
                    }

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
        public abstract int OnRecv(ArraySegment<byte> buffer);
        public abstract void OnSend(int numOfBytes);
        public abstract void OnDisconnected(EndPoint endPoint);

        // 암호화 설정
        public static bool EncryptionEnabled = true;
        protected AESCrypto _crypto;

        // Sequence (리플레이 공격 방지)
        private uint _sendSeq = 0;
        private const byte PKT_FLAG_HAS_SEQUENCE = 0x01;

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
            // Sequence 설정 (암호화 전에)
            if (sendBuff.Count >= 9)
            {
                byte flags = sendBuff.Array[sendBuff.Offset + 4];
                if ((flags & PKT_FLAG_HAS_SEQUENCE) != 0)
                {
                    _sendSeq++;
                    byte[] seqBytes = BitConverter.GetBytes(_sendSeq);
                    Array.Copy(seqBytes, 0, sendBuff.Array, sendBuff.Offset + 5, 4);
                }
            }

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

        // 서버 Session::EncryptBuffer와 동일 구조
        private ArraySegment<byte> EncryptBuffer(ArraySegment<byte> buffer)
        {
            // 원본: [size(2)][id(2)][data...]
            // 암호화+HMAC: [size(2)][encrypted(id+data)][HMAC(32)]

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

            // HMAC 계산 (암호화된 데이터에 대해)
            byte[] hmac = _crypto.ComputeHMAC(encrypted, 0, encrypted.Length);
            if (hmac == null)
                return new ArraySegment<byte>(null, 0, 0);

            // 새 버퍼: [size(2)][encrypted][HMAC(32)]
            int totalSize = 2 + encrypted.Length + AESCrypto.HMAC_SIZE;
            byte[] result = new byte[totalSize];

            Array.Copy(BitConverter.GetBytes((ushort)totalSize), 0, result, 0, 2);
            Array.Copy(encrypted, 0, result, 2, encrypted.Length);
            Array.Copy(hmac, 0, result, 2 + encrypted.Length, AESCrypto.HMAC_SIZE);

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
                    if (_recvBuffer.OnWrite(args.BytesTransferred) == false)
                    {
                        Disconnect();
                        return;
                    }

                    int processLen = OnRecv(_recvBuffer.ReadSegment);
                    if (processLen < 0 || _recvBuffer.DataSize < processLen)
                    {
                        Disconnect();
                        return;
                    }

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
