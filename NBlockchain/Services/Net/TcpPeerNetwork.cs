﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NBlockchain.Interfaces;
using NBlockchain.Models;
using NetMQ;
using NetMQ.Sockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;

namespace NBlockchain.Services.Net
{
    public class TcpPeerNetwork : IPeerNetwork, IDisposable
    {
        //TODO: break this class up into smaller pieces
        private const int TargetOutgoingCount = 8;
        private readonly uint _port;

        private IBlockReceiver _blockReciever;
        private ITransactionReceiver _transactionReciever;

        private readonly IBlockRepository _blockRepository;
        private readonly IEnumerable<IPeerDiscoveryService> _discoveryServices;
        private readonly ILogger _logger;
        private readonly IOwnAddressResolver _ownAddressResolver;

        private readonly ConcurrentQueue<KnownPeer> _peerRoundRobin = new ConcurrentQueue<KnownPeer>();
        private readonly ConcurrentDictionary<string, Guid> _outgoingConnectionStrings = new ConcurrentDictionary<string, Guid>();
        private readonly ConcurrentDictionary<Guid, OutgoingPeer> _outgoingPeers = new ConcurrentDictionary<Guid, OutgoingPeer>();
        private readonly ConcurrentDictionary<Guid, ConnectedPeer> _incomingPeers = new ConcurrentDictionary<Guid, ConnectedPeer>();

        private readonly AutoResetEvent _incomingEvent = new AutoResetEvent(true);

        private Timer _sharePeersTimer;

        private readonly RouterSocket _incomingSocket = new RouterSocket();
        private readonly NetMQPoller _poller = new NetMQPoller();
        private NetMQTimer _houseKeeper;
        private string _internalConnsctionString;
        private string _externalConnsctionString;

        public Guid NodeId { get; private set; }

        public TcpPeerNetwork(uint port, IBlockRepository blockRepository, IEnumerable<IPeerDiscoveryService> discoveryServices, ILoggerFactory loggerFactory, IOwnAddressResolver ownAddressResolver)
        {
            _port = port;
            _logger = loggerFactory.CreateLogger<TcpPeerNetwork>();
            _blockRepository = blockRepository;
            _discoveryServices = discoveryServices;
            _ownAddressResolver = ownAddressResolver;
            NodeId = Guid.NewGuid();
            _sharePeersTimer = new Timer(SharePeers, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            _incomingSocket.ReceiveReady += IncomingSocketReceiveReady;
        }

        private void RunProtected(AutoResetEvent evt, Action action)
        {
            evt.WaitOne();
            try
            {
                action();
            }
            finally
            {
                evt.Set();
            }
        }

        private async void IncomingSocketReceiveReady(object sender, NetMQSocketEventArgs e)
        {
            try
            {
                NetMQMessage message = null;
                RunProtected(_incomingEvent, () => message = e.Socket.ReceiveMultipartMessage());

                if (message == null)
                    return;

                if (message.FrameCount < 2)
                {
                    return;
                }

                var clientId = new Guid(message[0].Buffer);
                var op = (MessageOp) (message[1].Buffer.First());
                _incomingPeers[clientId] = new ConnectedPeer(clientId, e.Socket.Options.LastEndpoint);

                switch (op)
                {
                    case MessageOp.Block:
                        await ProcessBlock(message, clientId, false);
                        break;
                    case MessageOp.Tail:
                        await ProcessBlock(message, clientId, true);
                        break;
                    case MessageOp.Txn:
                        await ProcessTransaction(message, clientId);
                        break;
                    case MessageOp.BlockRequest:
                        await ProcessBlockRequest(message, e.Socket, _incomingEvent, clientId);
                        break;
                    case MessageOp.PeerShare:
                        if (IsSharablePeer(message[2].ConvertToString()))
                            AddPeer(new KnownPeer() { ConnectionString = message[2].ConvertToString() });
                        break;
                    case MessageOp.Connect:
                        _logger.LogDebug("Recv connect from {0}", clientId);
                        
                        RunProtected(_incomingEvent, () => 
                        {
                            _incomingSocket.SendMoreFrame(message[0].Buffer)
                            .SendMoreFrame(NodeId.ToByteArray())
                            .SendMoreFrame(ConvertOp(MessageOp.Identify))
                            .TrySendFrame(message[2].Buffer);
                        });                        
                        break;

                    case MessageOp.Disconnect:
                        _logger.LogDebug("Recv disconnect from {0}", clientId);
                        _incomingPeers.TryRemove(clientId, out var v);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error processing serv message, {ex.Message}");
            }
        }

        private async void Peer_ReceiveReady(object sender, NetMQSocketEventArgs e)
        {
            try
            {
                var message = e.Socket.ReceiveMultipartMessage();
                if (message.FrameCount < 2)
                {
                    return;
                }

                var serverId = new Guid(message[0].Buffer);
                var op = (MessageOp) (message[1].Buffer.First());

                switch (op)
                {
                    case MessageOp.Block:
                        await ProcessBlock(message, serverId, false);
                        break;
                    case MessageOp.Tail:
                        await ProcessBlock(message, serverId, true);
                        break;
                    case MessageOp.Txn:
                        await ProcessTransaction(message, serverId);
                        break;
                    case MessageOp.BlockRequest:
                        await ProcessBlockRequest(message, _outgoingPeers[serverId].Socket, _outgoingPeers[serverId].ResetEvent, null);
                        break;
                    case MessageOp.PeerShare:
                        if (IsSharablePeer(message[2].ConvertToString()))
                            AddPeer(new KnownPeer() { ConnectionString = message[2].ConvertToString() });
                        break;

                    case MessageOp.Identify:
                        _logger.LogDebug("Recv identify from {0}", serverId);
                        var peerConStr = message[2].ConvertToString();
                        _outgoingPeers[serverId] = new OutgoingPeer(e.Socket, serverId, peerConStr);                        
                        _outgoingConnectionStrings[peerConStr] = serverId;
                        foreach (var prr in _peerRoundRobin.Where(x => x.ConnectionString == peerConStr).ToList())
                            prr.LastContact = DateTime.Now;
                        break;

                    case MessageOp.Disconnect:
                        _logger.LogDebug("Recv disconnect from {0}", serverId);
                        _outgoingPeers.TryRemove(serverId, out var sock);
                        _poller.Remove(e.Socket);
                        e.Socket.Close();                        
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error processing peer message, {ex.Message}");
            }
        }

        private async Task ProcessBlockRequest(NetMQMessage message, IOutgoingSocket socket, AutoResetEvent evt, Guid? peerId)
        {
            var prevBlockId = message[2].Buffer;
            var block = await _blockRepository.GetNextBlock(prevBlockId);
            if (block != null)
            {
                _logger.LogDebug("Responding to block request");
                var data = SerializeObject(block);                
                SendBlock(socket, evt, false, peerId, data, -1);
            }
            else
            {
                _logger.LogDebug("Unable to respond to block request");
            }
        }

        private async Task ProcessBlock(NetMQMessage message, Guid originId, bool tail)
        {
            _logger.LogDebug($"Processing block {tail}");
            var hopCount = message[2].ConvertToInt32();
            var block = DeserializeObject<Block>(message[3].Buffer);
            var result = PeerDataResult.Ignore;
            if (tail)
                result = await _blockReciever.RecieveTail(block);
            else
                result = await _blockReciever.RecieveBlock(block);

            if ((tail) && (result == PeerDataResult.Relay) && (hopCount > -1))
            {
                var incomingTask = Task.Factory.StartNew(() =>
                {
                    var peerList = GetIncomingPeers().Where(x => x != originId);
                    Parallel.ForEach(peerList, peerId =>
                    {
                        SendBlock(_incomingSocket, _incomingEvent, tail, peerId, message[3].Buffer, hopCount + 1);
                    });
                });

                var outgoingTask = Task.Factory.StartNew(() =>
                {
                    var peerList = GetOutgoingPeers().Where(x => x != originId);
                    Parallel.ForEach(peerList, peerId =>
                    {
                        SendBlock(_outgoingPeers[peerId].Socket, _outgoingPeers[peerId].ResetEvent, tail, null, message[3].Buffer, hopCount + 1);
                    });
                });
            }
        }

        private async Task ProcessTransaction(NetMQMessage message, Guid originId)
        {
            var hopCount = message[2].ConvertToInt32();
            var txn = DeserializeObject<TransactionEnvelope>(message[3].Buffer);
            var result = await _transactionReciever.RecieveTransaction(txn);

            if ((result == PeerDataResult.Relay) && (hopCount > -1))
            {
                var outgoingTask = Task.Factory.StartNew(() =>
                {
                    var peerList = GetOutgoingPeers().Where(x => x != originId);
                    Parallel.ForEach(peerList, peerId =>
                    {
                        SendTxn(_outgoingPeers[peerId].Socket, _outgoingPeers[peerId].ResetEvent, null, message[3].Buffer, hopCount + 1);
                    });
                });

                var incomingTask = Task.Factory.StartNew(() =>
                {
                    var peerList = GetIncomingPeers().Where(x => x != originId);
                    Parallel.ForEach(peerList, peerId =>
                    {
                        SendTxn(_incomingSocket, _incomingEvent, peerId, message[3].Buffer, hopCount + 1);
                    });
                });
            }
        }

        public void Open()
        {
            _incomingSocket.Bind($"tcp://*:{_port}");            
            _poller.Add(_incomingSocket);
            _poller.RunAsync();            
            _houseKeeper = new NetMQTimer(TimeSpan.FromSeconds(30));
            _houseKeeper.Elapsed += HouseKeeper_Elapsed;
            _poller.Add(_houseKeeper);
            _houseKeeper.Enable = true;
            DiscoverOwnConnectionStrings();
            DiscoverPeers();
            AdvertiseToPeers();
        }

        private void OnboardPeer(string connStr)
        {
            try
            {
                var peer = new DealerSocket();
                peer.Options.Identity = NodeId.ToByteArray();
                peer.ReceiveReady += Peer_ReceiveReady;
                peer.Connect(connStr);
                _poller.Add(peer);
                peer.SendMoreFrame(ConvertOp(MessageOp.Connect))                    
                    .TrySendFrame(connStr);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error connecting to {connStr} - {ex.Message}");
            }
        }

        public void Close()
        {
            foreach (var peerId in GetIncomingPeers())
            {
                RunProtected(_incomingEvent, () => 
                {
                    _incomingSocket
                        .SendMoreFrame(peerId.ToByteArray())
                        .SendMoreFrame(NodeId.ToByteArray())
                        .TrySendFrame(ConvertOp(MessageOp.Disconnect));
                });                
            }

            foreach (var peerId in GetOutgoingPeers())
            {
                RunProtected(_outgoingPeers[peerId].ResetEvent, () =>
                {
                    _outgoingPeers[peerId].Socket
                        .TrySendFrame(ConvertOp(MessageOp.Disconnect));

                    _poller.Remove(_outgoingPeers[peerId].Socket);
                    _outgoingPeers[peerId].Socket.Close();
                });                
            }

            _outgoingPeers.Clear();

            _poller.Stop();
            _poller.Remove(_incomingSocket);
            _poller.Remove(_houseKeeper);
            _houseKeeper.Enable = false;
            _incomingSocket.Close();
        }

        public void DiscoverPeers()
        {
            foreach (var discovery in _discoveryServices)
            {
                Task.Factory.StartNew(async () =>
                {
                    try
                    {
                        var newPeers = await discovery.DiscoverPeers();
                        foreach (var np in newPeers)
                            AddPeer(np);
                        ConnectOut();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex.Message);
                    }
                });
            }
        }

        private void AddPeer(KnownPeer newPeer)
        {
            if (_peerRoundRobin.All(x => x.ConnectionString != newPeer.ConnectionString))
                _peerRoundRobin.Enqueue(newPeer);
        }

        private async void SharePeers(object state)
        {
            foreach (var ds in _discoveryServices)
                await ds.SharePeers(_peerRoundRobin.ToList());

            //
        }

        private void HouseKeeper_Elapsed(object sender, NetMQTimerEventArgs e)
        {
            _logger.LogDebug("Performing house keeping");
            DiscoverPeers();
            AdvertiseToPeers();
            ConnectOut();
        }

        private void ConnectOut()
        {
            var target = (TargetOutgoingCount - _outgoingPeers.Count);
            if (target <= 0)
                return;

            var actual = 0;
            var counter = 0;
            lock (_peerRoundRobin)
            {
                while ((actual < target) && (counter < _peerRoundRobin.Count))
                {
                    if (_peerRoundRobin.TryDequeue(out var kp))
                    {
                        _peerRoundRobin.Enqueue(kp);
                        counter++;
                        if (_outgoingConnectionStrings.ContainsKey(kp.ConnectionString))
                        {
                            if (_outgoingPeers.ContainsKey(_outgoingConnectionStrings[kp.ConnectionString]))
                                continue;
                        }
                        _logger.LogDebug($"Connecting to {kp.ConnectionString}");
                        OnboardPeer(kp.ConnectionString);
                        actual++;
                    }
                }
            }
        }

        public void RegisterBlockReceiver(IBlockReceiver blockReceiver)
        {
            _blockReciever = blockReceiver;
        }

        public void RegisterTransactionReceiver(ITransactionReceiver transactionReciever)
        {
            _transactionReciever = transactionReciever;
        }
        

        public void BroadcastTail(Block block)
        {
            var data = SerializeObject(block);
            var incoming = GetIncomingPeers();
            var outgoing = GetOutgoingPeers();
            
            Task.Factory.StartNew(() =>
            {
                Parallel.ForEach(incoming, peerId =>
                {
                    SendBlock(_incomingSocket, _incomingEvent, true, peerId, data, 0);
                });
            });

            Task.Factory.StartNew(() =>
            {
                Parallel.ForEach(outgoing, peerId =>
                {
                    SendBlock(_outgoingPeers[peerId].Socket, _outgoingPeers[peerId].ResetEvent, true, null, data, 0);
                });
            });
        }

        private void SendBlock(IOutgoingSocket socket, AutoResetEvent resetEvt, bool tail, Guid? peerId, byte[] data, int hopCount)
        {
            try
            {
                var op = ConvertOp(MessageOp.Block);
                if (tail)
                    op = ConvertOp(MessageOp.Tail);

                var msg = new NetMQMessage();

                if ((peerId.HasValue))
                {
                    msg.Append(peerId.Value.ToByteArray());
                    msg.Append(NodeId.ToByteArray());
                }

                msg.Append(op);
                msg.Append(BitConverter.GetBytes(hopCount));
                msg.Append(data);

                RunProtected(resetEvt, () => socket.TrySendMultipartMessage(msg));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error sending block {ex.Message}");
            }
        }

        public void BroadcastTransaction(TransactionEnvelope transaction)
        {
            var data = SerializeObject(transaction);
            
            Task.Factory.StartNew(() =>
            {
                var incoming = GetIncomingPeers();
                Parallel.ForEach(incoming, peerId =>
                {
                    SendTxn(_incomingSocket, _incomingEvent, peerId, data, 0);
                });
            });

            Task.Factory.StartNew(() =>
            {
                var outgoing = GetOutgoingPeers();
                Parallel.ForEach(outgoing, peerId =>
                {
                    SendTxn(_outgoingPeers[peerId].Socket, _outgoingPeers[peerId].ResetEvent, null, data, 0);
                });
            });
        }

        private void SendTxn(IOutgoingSocket socket, AutoResetEvent resetEvt, Guid? peerId, byte[] data, int hopCount)
        {
            try
            {
                var op = ConvertOp(MessageOp.Txn);

                var msg = new NetMQMessage();

                if ((peerId.HasValue) && (socket is RouterSocket))
                {
                    msg.Append(peerId.Value.ToByteArray());
                    msg.Append(NodeId.ToByteArray());
                }

                msg.Append(op);
                msg.Append(BitConverter.GetBytes(hopCount));
                msg.Append(data);

                RunProtected(resetEvt, () => socket.TrySendMultipartMessage(msg));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error sending txn {ex.Message}");
            }
        }

        public void RequestNextBlock(byte[] blockId)
        {
            Task.Factory.StartNew(async () =>
            {
                var incoming = GetIncomingPeers().Where(x => x != NodeId);
                foreach (var peerId in incoming)
                {
                    _logger.LogDebug($"Requesting block from incoming peer {peerId}");
                    RunProtected(_incomingEvent, () => 
                    {
                        _incomingSocket
                            .SendMoreFrame(peerId.ToByteArray())
                            .SendMoreFrame(NodeId.ToByteArray())
                            .SendMoreFrame(ConvertOp(MessageOp.BlockRequest))
                            .TrySendFrame(blockId);
                    });                    

                    await Task.Delay(TimeSpan.FromSeconds(5));

                    if ((await _blockRepository.GetNextBlock(blockId)) != null)
                        return;
                }
            });

            Task.Factory.StartNew(async () =>
            {                
                var outgoing = GetOutgoingPeers().Where(x => x != NodeId);
                foreach (var peerId in outgoing)
                {
                    _logger.LogDebug($"Requesting block from outgoing peer {peerId}");
                    RunProtected(_outgoingPeers[peerId].ResetEvent, () => 
                    {
                        _outgoingPeers[peerId].Socket
                            .SendMoreFrame(ConvertOp(MessageOp.BlockRequest))
                            .TrySendFrame(blockId);
                    });                    

                    await Task.Delay(TimeSpan.FromSeconds(5));

                    if ((await _blockRepository.GetNextBlock(blockId)) != null)
                        return;
                }
            });
        }

        public void Dispose()
        {
            
        }

        private ICollection<Guid> GetIncomingPeers()
        {
            return _incomingPeers.Select(x => x.Key).ToList();
        }

        private ICollection<Guid> GetOutgoingPeers()
        {
            return _outgoingPeers.Keys;
        }

        private void AdvertiseToPeers()
        {
            foreach (var ds in _discoveryServices)
                Task.Factory.StartNew(() => 
                {
                    try
                    {
                        if (_internalConnsctionString != null)
                            ds.AdvertiseLocal(_internalConnsctionString);

                        if (_externalConnsctionString != null)
                            ds.AdvertiseGlobal(_externalConnsctionString);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex.Message);
                    }
                });
        }

        private void DiscoverOwnConnectionStrings()
        {
            var ownAddress = _ownAddressResolver.ResolvePreferredLocalAddress();
            if (ownAddress != null)
                _internalConnsctionString = $"tcp://{ownAddress}:{_port}";

            //TODO: external addresses
        }

        private static byte[] SerializeObject(object data)
        {
            using (var bw = new MemoryStream())
            {
                var writer = new BsonDataWriter(bw);
                var serializer = new JsonSerializer();
                serializer.TypeNameHandling = TypeNameHandling.Objects;
                serializer.Serialize(writer, data);
                writer.Close();
                bw.TryGetBuffer(out var result);
                return result.Array;
            }
        }

        private static T DeserializeObject<T>(byte[] bson)
        {
            using (var ms = new MemoryStream(bson))
            {
                var bdr = new BsonDataReader(ms);
                var serializer = new JsonSerializer();
                serializer.TypeNameHandling = TypeNameHandling.Objects;
                var result = serializer.Deserialize<T>(bdr);
                bdr.Close();
                return result;
            }
        }

        private static bool IsSharablePeer(string connectionUri)
        {
            var uri = new Uri(connectionUri);
            switch (uri.HostNameType)
            {
                case UriHostNameType.Dns:
                    var ipAddr = Dns.GetHostAddressesAsync(uri.DnsSafeHost).Result;
                    if (ipAddr.Length == 0)
                        return false;
                    return IsSharablePeer($"{uri.Scheme}://{ipAddr[0]}:{uri.Port}");
                case UriHostNameType.IPv4:
                    var ip = IPAddress.Parse(uri.Host).GetAddressBytes();
                    switch (ip[0])
                    {
                        case 10:
                        case 127:
                            return true;
                        case 172:
                            return ip[1] >= 16 && ip[1] < 32;
                        case 192:
                            return ip[1] == 168;
                        default:
                            return false;
                    }
                case UriHostNameType.IPv6:
                    var ipv6 = IPAddress.Parse(uri.Host);
                    return (!ipv6.IsIPv6LinkLocal && !ipv6.IsIPv6SiteLocal);
                default:
                    return false;
            }
        }

        private byte[] ConvertOp(MessageOp op)
        {
            byte[] result = new byte[1];
            result[0] = (byte)op;
            return result;
        }

        public ICollection<ConnectedPeer> GetPeersIn()
        {
            return _incomingPeers.Values;
        }

        public ICollection<ConnectedPeer> GetPeersOut()
        {
            return _outgoingPeers.Values.Cast<ConnectedPeer>().ToList();
        }

        enum MessageOp { Disconnect = 0, Tail = 1, Block = 2, Txn = 3, BlockRequest = 4, PeerShare = 5, Connect = 6, Identify = 7 }
                
    }


    internal class OutgoingPeer : ConnectedPeer
    {        
        public NetMQSocket Socket { get; set; }
        public AutoResetEvent ResetEvent { get; set; }

        public OutgoingPeer(NetMQSocket socket, Guid nodeId, string address)
            : base(nodeId, address)
        {            
            Socket = socket;            
            ResetEvent = new AutoResetEvent(true);
        }
    }
        
}
