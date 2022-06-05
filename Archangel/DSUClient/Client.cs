using Archangel.DSUClient.Protocol;
using Durandal.Common.Cache;
using Durandal.Common.Compression;
using Durandal.Common.Logger;
using Durandal.Common.Time;
using Durandal.Common.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Vanara.Extensions;

namespace Archangel.DSUClient
{
    public class Client : IDisposable
    {
        public const uint   Magic   = 0x43555344; // DSUC
        public const ushort Version = 1001;

        private bool _active;

        private readonly ILogger _logger;
        private readonly Dictionary<int, IPEndPoint> _hosts;
        private readonly Dictionary<int, Dictionary<int, ControllerState>> _controllerStates;
        private readonly Dictionary<int, UdpClient> _clients;
        private readonly LockFreeCache<CRC32> _crcs;

        private readonly bool[] _clientErrorStatus = new bool[8];
        private readonly long[] _clientRetryTimer  = new long[8];

        public Client(ILogger logger)
        {
            _logger = logger.AssertNonNull(nameof(logger));
            _crcs = new LockFreeCache<CRC32>(8);
            _hosts = new Dictionary<int, IPEndPoint>();
            _controllerStates = new Dictionary<int, Dictionary<int, ControllerState>>();
            _clients = new Dictionary<int, UdpClient>();

            CloseClients();
        }

        public void CloseClients()
        {
            _active = false;

            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    try
                    {
                        client.Value?.Dispose();
                    }
                    catch (SocketException socketException)
                    {
                        _logger.Log($"Unable to dispose motion client. Error: {socketException.ErrorCode}", LogLevel.Wrn);
                    }
                }

                _hosts.Clear();
                _clients.Clear();
                _controllerStates.Clear();
            }
        }

        public void RegisterClient(int player, string host, int port)
        {
            if (_clients.ContainsKey(player) || !CanConnect(player))
            {
                return;
            }

            lock (_clients)
            {
                if (_clients.ContainsKey(player) || !CanConnect(player))
                {
                    return;
                }

                UdpClient client = null;

                try
                {
                    IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(host), port);
                    client = new UdpClient(host, port);
                    _clients.Add(player, client);
                    _hosts.Add(player, endPoint);

                    _active = true;

                    Task.Run(() =>
                    {
                        ReceiveLoop(player);
                    });
                }
                catch (FormatException formatException)
                {
                    if (!_clientErrorStatus[player])
                    {
                        _logger.Log($"Unable to connect to motion source at {host}:{port}. Error: {formatException.Message}", LogLevel.Wrn);
                        _clientErrorStatus[player] = true;
                    }
                }
                catch (SocketException socketException)
                {
                    if (!_clientErrorStatus[player])
                    {
                        _logger.Log($"Unable to connect to motion source at {host}:{port}. Error: {socketException.ErrorCode}", LogLevel.Wrn);
                        _clientErrorStatus[player] = true;
                    }

                    RemoveClient(player);
                    client?.Dispose();
                    SetRetryTimer(player);
                }
                catch (Exception exception)
                {
                    _logger.Log($"Unable to register motion client. Error: {exception.Message}", LogLevel.Wrn);
                    _clientErrorStatus[player] = true;
                    RemoveClient(player);
                    client?.Dispose();
                    SetRetryTimer(player);
                }
            }
        }

        public bool TryGetData(int player, int slot, out ControllerState input)
        {
            lock (_controllerStates)
            {
                if (_controllerStates.ContainsKey(player))
                {
                    if (_controllerStates[player].TryGetValue(slot, out input))
                    {
                        return true;
                    }
                }
            }

            input = null;

            return false;
        }

        private void RemoveClient(int clientId)
        {
            _clients?.Remove(clientId);

            _hosts?.Remove(clientId);
        }

        private void Send(byte[] data, int clientId)
        {
            if (_clients.TryGetValue(clientId, out UdpClient _client))
            {
                if (_client != null && _client.Client != null && _client.Client.Connected)
                {
                    try
                    {
                        _client?.Send(data, data.Length);
                    }
                    catch (SocketException socketException)
                    {
                        if (!_clientErrorStatus[clientId])
                        {
                            _logger.Log($"Unable to send data request to motion source at {_client.Client.RemoteEndPoint}. Error: {socketException.ErrorCode}", LogLevel.Wrn);
                        }

                        _clientErrorStatus[clientId] = true;

                        RemoveClient(clientId);

                        _client?.Dispose();

                        SetRetryTimer(clientId);
                    }
                    catch (ObjectDisposedException)
                    {
                        _clientErrorStatus[clientId] = true;

                        RemoveClient(clientId);

                        _client?.Dispose();

                        SetRetryTimer(clientId);
                    }
                }
            }
        }

        private byte[] Receive(int clientId, int timeout = 0)
        {
            if (_hosts.TryGetValue(clientId, out IPEndPoint endPoint) && _clients.TryGetValue(clientId, out UdpClient _client))
            {
                if (_client != null && _client.Client != null && _client.Client.Connected)
                {
                    _client.Client.ReceiveTimeout = timeout;

                    var result = _client?.Receive(ref endPoint);

                    if (result.Length > 0)
                    {
                        _clientErrorStatus[clientId] = false;
                    }

                    return result;
                }
            }

            throw new Exception($"Client {clientId} is not registered.");
        }

        private void SetRetryTimer(int clientId)
        {
            long elapsedMs = HighPrecisionTimer.GetCurrentTicks() / 1000000L;
            _clientRetryTimer[clientId] = elapsedMs;
        }

        private void ResetRetryTimer(int clientId)
        {
            _clientRetryTimer[clientId] = 0;
        }

        private bool CanConnect(int clientId)
        {
            return _clientRetryTimer[clientId] == 0 || (HighPrecisionTimer.GetCurrentTicks() / 1000000L) - 5000 > _clientRetryTimer[clientId];
        }

        public void ReceiveLoop(int clientId)
        {
            if (_hosts.TryGetValue(clientId, out IPEndPoint endPoint) && _clients.TryGetValue(clientId, out UdpClient _client))
            {
                if (_client != null && _client.Client != null && _client.Client.Connected)
                {
                    try
                    {
                        while (_active)
                        {
                            byte[] data = Receive(clientId);

                            if (data.Length == 0)
                            {
                                continue;
                            }

                            Task.Run(() => HandleResponse(data, clientId));
                        }
                    }
                    catch (SocketException socketException)
                    {
                        if (!_clientErrorStatus[clientId])
                        {
                            _logger.Log($"Unable to receive data from motion source at {endPoint}. Error: {socketException.ErrorCode}", LogLevel.Wrn);
                        }

                        _clientErrorStatus[clientId] = true;
                        RemoveClient(clientId);
                        _client?.Dispose();
                        SetRetryTimer(clientId);
                    }
                    catch (ObjectDisposedException)
                    {
                        _clientErrorStatus[clientId] = true;
                        RemoveClient(clientId);
                        _client?.Dispose();
                        SetRetryTimer(clientId);
                    }
                }
            }
        }

        public void HandleResponse(byte[] data, int clientId)
        {
            ResetRetryTimer(clientId);
            MessageType type = (MessageType)BitConverter.ToUInt32(data, 16);
            using (MemoryStream stream = new MemoryStream(data, 16, data.Length - 16, false))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                switch (type)
                {
                    case MessageType.Protocol:
                        break;
                    case MessageType.Info:
                        ControllerInfoResponse contollerInfo = reader.Read<ControllerInfoResponse>();
                        break;
                    case MessageType.Data:
                        ControllerDataResponse inputData = reader.Read<ControllerDataResponse>();

                        Vector3 accelerometer = new Vector3()
                        {
                            X = -inputData.AccelerometerX,
                            Y = inputData.AccelerometerZ,
                            Z = -inputData.AccelerometerY
                        };

                        Vector3 gyroscrope = new Vector3()
                        {
                            X = inputData.GyroscopePitch,
                            Y = inputData.GyroscopeRoll,
                            Z = -inputData.GyroscopeYaw
                        };

                        ulong timestamp = inputData.MotionTimestamp;
                        ControllerState state;
                        lock (_controllerStates)
                        {
                            int slot = inputData.Shared.Slot;

                            // Create new state or find the existing state obect
                            if (_controllerStates.ContainsKey(clientId))
                            {
                                if (_controllerStates[clientId].ContainsKey(slot))
                                {
                                    state = _controllerStates[clientId][slot];
                                }
                                else
                                {
                                    state = new ControllerState();
                                    _controllerStates[clientId].Add(slot, state);
                                }
                            }
                            else
                            {
                                state = new ControllerState();
                                _controllerStates.Add(clientId, new Dictionary<int, ControllerState>() { { slot, state } });
                            }

                            // Now put the actual updated readings into the state
                            state.Accelerometer = accelerometer;
                            state.Gyroscope = gyroscrope;
                        }
                        break;
                }
            }
        }

        private uint CalculateCRC32(byte[] data)
        {
            CRC32 crc = _crcs.TryDequeue();
            if (crc == null)
            {
                crc = new CRC32();
            }

            try
            {
                for (int c = 0; c < data.Length; c++)
                {
                    crc.UpdateCRC(data[c]);
                }

                return (uint)crc.Crc32Result;
            }
            finally
            {
                crc.Reset();
                _crcs.TryEnqueue(crc);
            }
        }

        public void RequestInfo(int clientId, int slot)
        {
            if (!_active)
            {
                return;
            }

            Header header = GenerateHeader(clientId);

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(header);

                ControllerInfoRequest request = new ControllerInfoRequest()
                {
                    Type       = MessageType.Info,
                    PortsCount = 4
                };

                request.PortIndices[0] = (byte)slot;

                writer.Write(request);

                header.Length = (ushort)(stream.Length - 16);

                writer.Seek(6, SeekOrigin.Begin);
                writer.Write(header.Length);

                header.Crc32 = CalculateCRC32(stream.ToArray());

                writer.Seek(8, SeekOrigin.Begin);
                writer.Write(header.Crc32);

                byte[] data = stream.ToArray();

                Send(data, clientId);
            }
        }

        public unsafe void RequestData(int clientId, int slot)
        {
            if (!_active)
            {
                return;
            }

            Header header = GenerateHeader(clientId);

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(header);

                ControllerDataRequest request = new ControllerDataRequest()
                {
                    Type           = MessageType.Data,
                    Slot           = (byte)slot,
                    SubscriberType = SubscriberType.Slot
                };

                writer.Write(request);

                header.Length = (ushort)(stream.Length - 16);

                writer.Seek(6, SeekOrigin.Begin);
                writer.Write(header.Length);

                header.Crc32 = CalculateCRC32(stream.ToArray());

                writer.Seek(8, SeekOrigin.Begin);
                writer.Write(header.Crc32);

                byte[] data = stream.ToArray();

                Send(data, clientId);
            }
        }

        private Header GenerateHeader(int clientId)
        {
            Header header = new Header()
            {
                Id          = (uint)clientId,
                MagicString = Magic,
                Version     = Version,
                Length      = 0,
                Crc32       = 0
            };

            return header;
        }

        public void Dispose()
        {
            _active = false;

            CloseClients();
        }
    }
}