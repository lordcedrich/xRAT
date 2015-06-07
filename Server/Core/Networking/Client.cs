﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ProtoBuf;
using ProtoBuf.Meta;
using xServer.Core.Compression;
using xServer.Core.Encryption;
using xServer.Core.Extensions;
using xServer.Core.Packets;
using xServer.Settings;

namespace xServer.Core.Networking
{
    public class Client
    {
        /// <summary>
        /// Occurs when the state of the client changes.
        /// </summary>
        public event ClientStateEventHandler ClientState;

        /// <summary>
        /// Represents the method that will handle a change in a client's state.
        /// </summary>
        /// <param name="s">The client which changed its state.</param>
        /// <param name="connected">The new connection state of the client.</param>
        public delegate void ClientStateEventHandler(Client s, bool connected);

        /// <summary>
        /// Fires an event that informs subscribers that the state of the client has changed.
        /// </summary>
        /// <param name="connected">The new connection state of the client.</param>
        private void OnClientState(bool connected)
        {
            if (Connected == connected) return;

            Connected = connected;
            if (ClientState != null)
            {
                ClientState(this, connected);
            }

            if (!connected && !_parentServer.Processing)
                _parentServer.RemoveClient(this);
        }

        /// <summary>
        /// Occurs when a packet is received from the client.
        /// </summary>
        public event ClientReadEventHandler ClientRead;

        /// <summary>
        /// Represents the method that will handle a packet received from the client.
        /// </summary>
        /// <param name="s">The client that has received the packet.</param>
        /// <param name="packet">The packet that received by the client.</param>
        public delegate void ClientReadEventHandler(Client s, IPacket packet);

        /// <summary>
        /// Fires an event that informs subscribers that a packet has been
        /// received from the client.
        /// </summary>
        /// <param name="packet">The packet that received by the client.</param>
        private void OnClientRead(IPacket packet)
        {
            if (ClientRead != null)
            {
                ClientRead(this, packet);
            }
        }

        /// <summary>
        /// Occurs when a packet is sent by the client.
        /// </summary>
        public event ClientWriteEventHandler ClientWrite;

        /// <summary>
        /// Represents the method that will handle the sent packet.
        /// </summary>
        /// <param name="s">The client that has sent the packet.</param>
        /// <param name="packet">The packet that has been sent by the client.</param>
        /// <param name="length">The length of the packet.</param>
        /// <param name="rawData">The packet in raw bytes.</param>
        public delegate void ClientWriteEventHandler(Client s, IPacket packet, long length, byte[] rawData);

        /// <summary>
        /// Fires an event that informs subscribers that the client has sent a packet.
        /// </summary>
        /// <param name="packet">The packet that has been sent by the client.</param>
        /// <param name="length">The length of the packet.</param>
        /// <param name="rawData">The packet in raw bytes.</param>
        private void OnClientWrite(IPacket packet, long length, byte[] rawData)
        {
            if (ClientWrite != null)
            {
                ClientWrite(this, packet, length, rawData);
            }
        }

        /// <summary>
        /// Checks whether the clients are equal.
        /// </summary>
        /// <param name="c">Client to compare with.</param>
        /// <returns></returns>
        public bool Equals(Client c)
        {
            return this.EndPoint.Port == c.EndPoint.Port; // this port is always unique for each client
        }

        /// <summary>
        /// The type of the packet received.
        /// </summary>
        public enum ReceiveType
        {
            Header,
            Payload
        }

        /// <summary>
        /// Handle of the Client Socket.
        /// </summary>
        private Socket _handle;

        /// <summary>
        /// Lock object for the Client Socket.
        /// </summary>
        private readonly object _handleLock = new object();

        /// <summary>
        /// The internal index of the packet type.
        /// </summary>
        private int _typeIndex;

        //receive info
        private int _readOffset;
        private int _writeOffset;
        private int _readableDataLen;
        private int _payloadLen;
        private ReceiveType _receiveState = ReceiveType.Header;

        //Connection info
        public bool Connected { get; private set; }
        public UserState Value { get; set; }
        public IPEndPoint EndPoint { get; private set; }
        private Server _parentServer;

        /// <summary>
        /// The buffer for the client's incoming packets.
        /// </summary>
        private byte[] _readBuffer;

        /// <summary>
        /// Lock object for the client's incoming packets buffer.
        /// </summary>
        private readonly object _readBufferLock = new object();

        /// <summary>
        /// The buffer for the client's incoming payload.
        /// </summary>
        private byte[] _payloadBuffer;

        private const bool encryptionEnabled = true;
        private const bool compressionEnabled = true;

        public Client()
        {
        }

        internal Client(Server server, Socket sock, Type[] packets)
        {
            try
            {
                AddTypesToSerializer(typeof (IPacket), packets);
                _parentServer = server;
                Initialize();

                _handle = sock;
                _handle.SetKeepAliveEx(_parentServer.KEEP_ALIVE_INTERVAL, _parentServer.KEEP_ALIVE_TIME);
                _handle.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.NoDelay, true);
                _handle.NoDelay = true;

                _readBuffer = Server.BufferManager.GetBuffer();

                _handle.BeginReceive(_readBuffer, 0, _readBuffer.Length, SocketFlags.None, AsyncReceive, null);
                EndPoint = (IPEndPoint) _handle.RemoteEndPoint;
                OnClientState(true);
            }
            catch
            {
                Disconnect();
            }
        }

        private void Initialize()
        {
            AddTypeToSerializer(typeof (IPacket), typeof (UnknownPacket));
        }

        private void AsyncReceive(IAsyncResult result)
        {
            int bytesTransferred = -1;
            try
            {
                bytesTransferred = _handle.EndReceive(result);

                if (bytesTransferred <= 0)
                {
                    OnClientState(false);
                    return;
                }
            }
            catch (Exception)
            {
                OnClientState(false);
                return;
            }

            _parentServer.BytesReceived += bytesTransferred;

            _readableDataLen += bytesTransferred;
            bool process = true;

            while (process)
            {
                switch (_receiveState)
                {
                    case ReceiveType.Header:
                        {
                            process = _readableDataLen >= _parentServer.HEADER_SIZE;
                            if (process)
                            {
                                try
                                {
                                    lock (_readBufferLock)
                                    {
                                        _payloadLen = BitConverter.ToInt32(_readBuffer, _readOffset);
                                    }
                                }
                                catch (Exception)
                                {
                                    break;
                                }
                                if (_payloadLen < 0)
                                {
                                    process = false;
                                    break;
                                }

                                _readableDataLen -= _parentServer.HEADER_SIZE;
                                _readOffset += _parentServer.HEADER_SIZE;
                                _receiveState = ReceiveType.Payload;
                            }
                            break;
                        }
                    case ReceiveType.Payload:
                        {
                            process = _readableDataLen >= _payloadLen;
                            if (process)
                            {
                                if (_payloadBuffer == null || _payloadBuffer.Length != _payloadLen)
                                    _payloadBuffer = new byte[_payloadLen];
                                try
                                {
                                    lock (_readBufferLock)
                                    {
                                        Array.Copy(_readBuffer, _readOffset, _payloadBuffer, 0, _payloadBuffer.Length);
                                    }
                                }
                                catch
                                {
                                    Disconnect();
                                }

                                if (encryptionEnabled)
                                    _payloadBuffer = AES.Decrypt(_payloadBuffer, Encoding.UTF8.GetBytes(XMLSettings.Password));

                                if (_payloadBuffer.Length > 0)
                                {
                                    if (compressionEnabled)
                                        _payloadBuffer = new SafeQuickLZ().Decompress(_payloadBuffer, 0, _payloadBuffer.Length);

                                    using (MemoryStream deserialized = new MemoryStream(_payloadBuffer))
                                    {
                                        IPacket packet = Serializer.DeserializeWithLengthPrefix<IPacket>(deserialized,
                                            PrefixStyle.Fixed32);

                                        OnClientRead(packet);
                                    }
                                }

                                _readOffset += _payloadLen;
                                _readableDataLen -= _payloadLen;
                                _receiveState = ReceiveType.Header;
                            }
                            else // handle payload that does not fit in one buffer
                            {
                                if (_payloadBuffer == null || _payloadBuffer.Length != _payloadLen)
                                    _payloadBuffer = new byte[_payloadLen];
                                try
                                {
                                    lock (_readBufferLock)
                                    {
                                        Array.Copy(_readBuffer, _readOffset, _payloadBuffer, _writeOffset,
                                            _readableDataLen);
                                    }
                                }
                                catch
                                {
                                    Disconnect();
                                }

                                _writeOffset += _readableDataLen;
                                _readOffset += _readableDataLen;

                                if (_writeOffset == _payloadLen)
                                {
                                    if (encryptionEnabled)
                                        _payloadBuffer = AES.Decrypt(_payloadBuffer, Encoding.UTF8.GetBytes(XMLSettings.Password));

                                    if (_payloadBuffer.Length > 0)
                                    {
                                        if (compressionEnabled)
                                            _payloadBuffer = new SafeQuickLZ().Decompress(_payloadBuffer, 0, _payloadBuffer.Length);

                                        using (MemoryStream deserialized = new MemoryStream(_payloadBuffer))
                                        {
                                            IPacket packet = Serializer.DeserializeWithLengthPrefix<IPacket>(deserialized,
                                                PrefixStyle.Fixed32);

                                            OnClientRead(packet);
                                        }
                                    }

                                    _receiveState = ReceiveType.Header;
                                }
                            }
                            break;
                        }
                }
            }

            int len = (_receiveState == ReceiveType.Header) ? _parentServer.HEADER_SIZE : _payloadLen;
            if (len < _readOffset)
            {
                _writeOffset = 0; // prepare for next packet
            }
            _readOffset = 0;
            _readableDataLen = 0;

            try
            {
                lock (_readBufferLock)
                {
                    _handle.BeginReceive(_readBuffer, 0, _readBuffer.Length, SocketFlags.None, AsyncReceive, null);
                }
            }
            catch
            {
                Disconnect();
            }
        }

        public void Send<T>(T packet) where T : IPacket
        {
            lock (_handleLock)
            {
                if (!Connected)
                    return;

                try
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        Serializer.SerializeWithLengthPrefix<T>(ms, packet, PrefixStyle.Fixed32);

                        byte[] data = ms.ToArray();

                        Send(data);
                        OnClientWrite(packet, data.LongLength, data);
                    }
                }
                catch
                {
                }
            }
        }

        private void Send(byte[] data)
        {
            if (!Connected)
                return;

            if (compressionEnabled)
                data = new SafeQuickLZ().Compress(data, 0, data.Length, 3);

            if (encryptionEnabled)
                data = AES.Encrypt(data, Encoding.UTF8.GetBytes(XMLSettings.Password));

            byte[] temp = BitConverter.GetBytes(data.Length);

            byte[] payload = new byte[data.Length + 4];
            Array.Copy(temp, payload, temp.Length);
            Array.Copy(data, 0, payload, 4, data.Length);

            _parentServer.BytesSent += payload.Length;

            try
            {
                _handle.Send(payload);
            }
            catch
            {
                Disconnect();
            }
        }

        /// <summary>
        /// Disconnect the client from the server and dispose of
        /// resources associated with the client.
        /// </summary>
        public void Disconnect()
        {
            OnClientState(false);

            if (_handle != null)
            {
                _handle.Close();
                _readOffset = 0;
                _writeOffset = 0;
                _readableDataLen = 0;
                _payloadLen = 0;
                if (Server.BufferManager != null)
                    Server.BufferManager.ReturnBuffer(_readBuffer);
            }
        }

        /// <summary>
        /// Adds a Type to the serializer so a message can be properly serialized.
        /// </summary>
        /// <param name="parent">The parent type, i.e.: IPacket</param>
        /// <param name="type">Type to be added</param>
        public void AddTypeToSerializer(Type parent, Type type)
        {
            if (type == null || parent == null)
                throw new ArgumentNullException();

            bool isAlreadyAdded = RuntimeTypeModel.Default[parent].GetSubtypes().Any(subType => subType.DerivedType.Type == type);

            if (!isAlreadyAdded)
                RuntimeTypeModel.Default[parent].AddSubType(_typeIndex += 1, type);
        }

        /// <summary>
        /// Adds Types to the serializer.
        /// </summary>
        /// <param name="parent">The parent type, i.e.: IPacket</param>
        /// <param name="types">Types to add.</param>
        public void AddTypesToSerializer(Type parent, params Type[] types)
        {
            foreach (Type type in types)
                AddTypeToSerializer(parent, type);
        }
    }
}