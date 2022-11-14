﻿using NTDLS.MemQueue.Library;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NTDLS.MemQueue
{
    /// <summary>
    /// Client class for communicating with the server and thereby communicating with other clients.
    /// </summary>
    public class NMQClient : NMQBase
    {
        /// <summary>
        /// User data, use as you will.
        /// </summary>
        public object Tag { get; set; }

        /// <summary>
        /// The number of initiated async TCP sends that have not completed.
        /// </summary>
        public int TCPSendQueueDepth { get; private set; }

        /// <summary>
        /// The unique ID of this peer.
        /// </summary>
        public Guid PeerId { get; private set; } = Guid.NewGuid();

        /// <summary>
        /// The number of commands that have been dispatched and have not been acknoledgled by the server.
        /// </summary>
        public int PresumedDeadCommandCount { get; set; }

        /// <summary>
        /// The number mesages send that the server has yet to receive an acknowledgment to.
        /// </summary>
        public int OutstandingAcknowledgments
        {
            get
            {
                lock (_ackWaitEvents)
                    return _ackWaitEvents.Count();
            }
        }

        #region Backend Variables.

        private Dictionary<string, NMQWaitEvent> _ackWaitEvents = new Dictionary<string, NMQWaitEvent>();
        private bool _continueRunning = false;
        private Socket _connectSocket;
        private AsyncCallback _onDataReceivedCallback;
        private IPAddress _serverIpAddress;
        private int _serverPort;
        private List<string> _subscribedQueues = new List<string>();
        private object _monitorThreadLock = new object();
        private List<NMQQuerySubscription> messageQuerySubscriptions = new List<NMQQuerySubscription>();

        #endregion

        #region Events.
        public override void LogException(Exception ex)
        {
            OnExceptionOccured?.Invoke(this, ex);
        }

        /// <summary>
        /// Allows excpetions to be logged.
        /// </summary>
        public event ExceptionOccuredEvent OnExceptionOccured;
        public delegate void ExceptionOccuredEvent(NMQBase sender, Exception exception);

        /// <summary>
        /// Receives replies to a query which were sent to the queue [Query(...)].
        /// </summary>
        public event QueryReplyReceivedEvent OnQueryReplyReceived;
        public delegate void QueryReplyReceivedEvent(NMQClient sender, NMQReply reply, bool hasAssociatedOpenQuery);

        /// <summary>
        /// Receives queries which were sent to the queue via [Query(...)], if subscribed.
        /// </summary>
        public event QueryReceivedEvent OnQueryReceived;
        public delegate NMQQueryReplyResult QueryReceivedEvent(NMQClient sender, NMQQuery query);

        /// <summary>
        /// Receives messages which were sent to the queue via [EnqueueMessage(...)].
        /// </summary>
        public event MessageReceivedEvent OnMessageReceived;
        public delegate void MessageReceivedEvent(NMQClient sender, NMQNotification notification);

        /// <summary>
        /// Notify when the client connects to the server. The even is also called on subsequent automatically reconnects.
        /// </summary>
        public event ConnectedEvent OnConnected;
        public delegate void ConnectedEvent();

        /// <summary>
        /// Notify when the client disconnects from the server.
        /// </summary>
        public event DisconnectedEvent OnDisconnected;
        public delegate void DisconnectedEvent(NMQClient sender);

        /// <summary>
        /// Notify when an item is enqueued by this client.
        /// </summary>
        public event EnqueuedEvent OnEnqueued;
        public delegate void EnqueuedEvent(NMQClient sender, NMQMessageBase item);

        /// <summary>
        /// Notify when a queue is subscribed to by this client.
        /// </summary>
        public event QueueSubscribedEvent OnQueueSubscribed;
        public delegate void QueueSubscribedEvent(NMQClient sender, string queueName);

        /// <summary>
        /// Notify when a queue subscription is removed by this client.
        /// </summary>
        public event QueueUnsubscribedEvent OnQueueUnsubscribed;
        public delegate void QueueUnsubscribedEvent(NMQClient sender, string queueName);

        /// <summary>
        /// Notify when a queue is cleared by this client.
        /// </summary>
        public event QueueClearedEvent OnQueueCleared;
        public delegate void QueueClearedEvent(NMQClient sender, string queueName);

        #endregion

        #region Connect/Disconnect.

        /// <summary>
        /// Connect to the server.
        /// </summary>
        /// <param name="hostName">Host or IP address of queue server.</param>
        /// <param name="port">Port number of queue server.</param>
        /// <param name="retryInBackground">If false, the client will not retry to connect if the connection fails.</param>
        /// <returns></returns>
        public bool Connect(string hostName, int port, bool retryInBackground)
        {
            IPAddress ipAddress = SocketUtility.GetIPv4Address(hostName);
            return Connect(ipAddress, port, retryInBackground);
        }

        /// <summary>
        /// Connect to the server.
        /// </summary>
        /// <param name="hostName">Host or IP address of queue server.</param>
        /// <param name="retryInBackground">If false, the client will not retry to connect if the connection fails.</param>
        /// <returns></returns>
        public bool Connect(string hostName, bool retryInBackground)
        {
            IPAddress ipAddress = SocketUtility.GetIPv4Address(hostName);
            return Connect(ipAddress, NMQConstants.DEFAULT_PORT, retryInBackground);
        }

        /// <summary>
        /// Connect to the server.
        /// </summary>
        /// <param name="hostName">Host or IP address of queue server.</param>
        /// <returns></returns>
        public bool Connect(string hostName)
        {
            return Connect(hostName, NMQConstants.DEFAULT_PORT, true);
        }

        /// <summary>
        /// Connect to the server.
        /// </summary>
        /// <param name="hostName">Host or IP address of queue server.</param>
        /// <param name="port">Port number of queue server.</param>
        /// <returns></returns>
        public bool Connect(string hostName, int port)
        {
            return Connect(hostName, port, true);
        }

        /// <summary>
        /// Connect to the server.
        /// </summary>
        /// <param name="ipAddress">IP address of queue server.</param>
        /// <returns></returns>
        public bool Connect(IPAddress ipAddress)
        {
            return Connect(ipAddress, NMQConstants.DEFAULT_PORT, true);
        }

        /// <summary>
        /// Connect to the server.
        /// </summary>
        /// <param name="ipAddress">IP address of queue server.</param>
        /// <param name="port">Port number of queue server.</param>
        /// <returns></returns>
        public bool Connect(IPAddress ipAddress, int port)
        {
            return Connect(ipAddress, port, true);
        }


        /// <summary>
        /// Connect to the server.
        /// </summary>
        /// <param name="hostName">Host or IP address of queue server.</param>
        /// <param name="port">Port number of queue server.</param>
        /// <param name="retryInBackground">If false, the client will not retry to connect if the connection fails.</param>
        /// <returns></returns>
        private bool Connect(IPAddress ipAddress, int port, bool startMonitorThread)
        {
            bool result = ConnectEx(ipAddress, port);

            if (startMonitorThread)
            {
                new Thread(MonitorThreadProc).Start();
            }

            return result;
        }

        /// <summary>
        /// Connect to the server.
        /// </summary>
        /// <param name="hostName">Host or IP address of queue server.</param>
        /// <param name="port">Port number of queue server.</param>
        /// <param name="retryInBackground">If false, the client will not retry to connect if the connection fails.</param>
        /// <returns></returns>
        private bool ConnectEx(IPAddress ipAddress, int port)
        {
            _continueRunning = true;
            _serverIpAddress = ipAddress;
            _serverPort = port;

            try
            {
                _connectSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                _connectSocket.Connect(new IPEndPoint(ipAddress, port));
                if (_connectSocket != null && _connectSocket.Connected)
                {
                    var payload = new NMQCommand()
                    {
                        Message = new NMQMessageBase(PeerId, Guid.NewGuid()),
                        CommandType = PayloadCommandType.Hello
                    };

                    var messagePacket = Packetizer.AssembleMessagePacket(this, payload);

                    //Create wait event and wait for ACK.
                    NMQWaitEvent waitEvent = new NMQWaitEvent(payload.Message.MessageId);
                    lock (_ackWaitEvents) _ackWaitEvents.Add($"{waitEvent.MessageId}", waitEvent);

                    SendAsync(_connectSocket, messagePacket);

                    WaitForData(new Peer(_connectSocket));

                    if (waitEvent.WaitOne(NMQConstants.ACK_TIMEOUT_MS) == false)
                    {
                        lock (_ackWaitEvents) _ackWaitEvents.Remove($"{waitEvent.MessageId}");
                        _connectSocket?.Dispose();
                        _connectSocket = null;
                        return false;
                    }

                    OnConnected?.Invoke();

                    return true;
                }
            }
            catch
            {
                _connectSocket?.Dispose();
                _connectSocket = null;
            }

            return false;
        }

        /// <summary>
        /// Disconnects from the server. Shuts down the client and cancels any outstanding connection attempts.
        /// </summary>
        public void Disconnect()
        {
            _continueRunning = false;
            CloseSocket();
        }

        #endregion

        #region Socket Client.

        private void SendAsync(Socket socket, byte[] data)
        {
            try
            {
                TCPSendQueueDepth++;
                socket.BeginSend(data, 0, data.Length, 0, new AsyncCallback(SendCallback), socket);
                Thread.Sleep(1); //This is for everyones wellbeing, only on the client though.
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
        }

        private void SendCallback(IAsyncResult ar)
        {
            try
            {
                Socket socket = (Socket)ar.AsyncState;
                if (ar.IsCompleted && socket.Connected == true)
                {
                    int bytesSent = socket.EndSend(ar);
                }
            }
            catch (Exception ex)
            {
                LogException(ex);
            }

            TCPSendQueueDepth--;
        }

        private void CloseSocket()
        {
            try
            {
                if (_connectSocket != null)
                {
                    OnDisconnected?.Invoke(this);

                    try
                    {
                        _connectSocket?.Shutdown(SocketShutdown.Both);
                    }
                    finally
                    {
                        _connectSocket?.Close();
                        _connectSocket?.Dispose();
                    }
                }
            }
            catch
            {
                //Discard.
            }

            try
            {
                lock (messageQuerySubscriptions)
                {
                    for (int i = 0; i < messageQuerySubscriptions.Count; i++)
                    {
                        messageQuerySubscriptions[i].PayloadReceivedEvent.Set();
                        messageQuerySubscriptions[i].PayloadProcessedEvent.Set();
                    }
                }
            }
            catch
            {
                //Discard.
            }

            _connectSocket = null;
        }

        private void MonitorThreadProc(object data)
        {
            if (Monitor.TryEnter(_monitorThreadLock))
            {
                try
                {
                    while (_continueRunning)
                    {
                        try
                        {
                            DateTime askStaleTime = DateTime.UtcNow.AddMilliseconds(-NMQConstants.ACK_TIMEOUT_MS);
                            lock (_ackWaitEvents)
                            {
                                var ackKeys = _ackWaitEvents.Where(o => o.Value.CreatedDate < askStaleTime).Select(o => o.Key);
                                foreach (var key in ackKeys)
                                {
                                    PresumedDeadCommandCount++;
                                    _ackWaitEvents.Remove(key);
                                }
                            }
                        }
                        catch {/*Discard*/}

                        try
                        {
                            if (_connectSocket == null || _connectSocket.Connected == false)
                            {
                                if (Connect(_serverIpAddress, _serverPort, false))
                                {
                                    foreach (string queueName in _subscribedQueues)
                                    {
                                        Subscribe(queueName);
                                    }
                                }
                            }
                        }
                        catch {/*Discard*/}

                        Thread.Sleep(1000);
                    }
                }
                catch
                {
                    throw;
                }
                finally
                {
                    Monitor.Exit(_monitorThreadLock);
                }
            }
        }

        private void WaitForData(Peer peer)
        {
            try
            {
                if (_onDataReceivedCallback == null)
                {
                    _onDataReceivedCallback = new AsyncCallback(OnDataReceived);
                }
            }
            catch
            {
                //Discard.
            }

            try
            {
                if (peer.Socket.Connected)
                {
                    peer.Socket.BeginReceive(peer.Packet.Buffer, 0, peer.Packet.Buffer.Length, SocketFlags.None, _onDataReceivedCallback, peer);
                }
            }
            catch
            {
            }
        }

        private void OnDataReceived(IAsyncResult asyn)
        {
            Socket socket = null;
            try
            {
                Peer peer = (Peer)asyn.AsyncState;
                socket = peer.Socket;
                peer.Packet.BufferLength = peer.Socket.EndReceive(asyn);

                if (peer.Packet.BufferLength == 0)
                {
                    CloseSocket();
                    return;
                }

                Packetizer.DissasemblePacketData(this, peer, peer.Packet, PacketPayloadHandler);

                WaitForData(peer);
            }
            catch (ObjectDisposedException)
            {
                CloseSocket();
                return;
            }
            catch (SocketException)
            {
                CloseSocket();
                return;
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
        }

        #endregion

        #region Commands.

        /// <summary>
        /// Asynchronously enqueues a query and returns the reply. The query is broadcast to all queue subscribers.
        /// </summary>
        /// <param name="queueName">Name of the queue for which to broadcast to.</param>
        /// <param name="label">User data to be sent, typically just a string that provides context to the message.</param>
        /// <param name="message">User data to be sent.</param>
        /// <param name="timeout">The number of milliseconds to wait for a reply. Default 60000 (1 minute).</param>
        /// <returns></returns>
        public async Task<NMQMessageBase> QueryAsync(NMQQuery query, int timeout = 60000)
        {
            if (query == null || string.IsNullOrWhiteSpace(query.QueueName))
            {
                throw new Exception("No queue name was specified.");
            }

            if (_connectSocket == null || _connectSocket.Connected == false)
            {
                throw new Exception("The client is not connected to a server.");
            }

            try
            {
                Guid messageId = Guid.NewGuid();
                var querySubscription = new NMQQuerySubscription(messageId);

                lock (messageQuerySubscriptions)
                {
                    messageQuerySubscriptions.Add(querySubscription);
                }

                var queueItem = new NMQMessageBase(PeerId, query.QueueName, messageId, query.ExpireSeconds)
                {
                    Message = query.Message,
                    Label = query.Label,
                    IsQuery = true
                };

                EnqueueEx(
                        new NMQCommand()
                        {
                            Message = queueItem,
                            CommandType = PayloadCommandType.Enqueue
                        }
                    );
                await Task.Run(() =>
                {
                    querySubscription.PayloadReceivedEvent.WaitOne(timeout);
                });

                lock (messageQuerySubscriptions)
                {
                    messageQuerySubscriptions.Remove(querySubscription);
                }

                querySubscription.PayloadProcessedEvent.Set();

                return querySubscription.ReplyQueueItem;
            }
            catch (Exception ex)
            {
                LogException(ex);
            }

            return null;
        }

        /// <summary>
        /// Enqueues a query and returns immediately. The reply is expected to be handeled by OnQueryReplyReceived(). The query is broadcast to all queue subscribers.
        /// </summary>
        /// <param name="queueName">Name of the queue for which to broadcast to.</param>
        /// <param name="label">User data, typically just a string that provides context to the message.</param>
        /// <param name="message">User data to be sent.</param>
        public void QueryNoWait(NMQQuery query)
        {
            Query(query, 0);
        }

        /// <summary>
        /// Synchronously enqueues a query and returns the reply. The query is broadcast to all queue subscribers.
        /// </summary>
        /// <param name="queueName">Name of the queue for which to broadcast to.</param>
        /// <param name="label">User data, typically just a string that provides context to the message.</param>
        /// <param name="message">User data to be sent.</param>
        /// <param name="timeout">The number of milliseconds to wait for a reply. Default 60000 (1 minute).</param>
        /// <returns>Returns the reply to the query or null if timeout occured.</returns>
        public NMQMessageBase Query(NMQQuery query, int timeout = 60000)
        {
            if (query == null || string.IsNullOrWhiteSpace(query.QueueName))
            {
                throw new Exception("No queue name was specified.");
            }

            if (_connectSocket == null || _connectSocket.Connected == false)
            {
                throw new Exception("The client is not connected to a server.");
            }

            try
            {
                Guid messageId = Guid.NewGuid();
                var querySubscription = new NMQQuerySubscription(messageId);

                lock (messageQuerySubscriptions)
                {
                    messageQuerySubscriptions.Add(querySubscription);
                }

                var queueItem = new NMQMessageBase(PeerId, query.QueueName, messageId, query.ExpireSeconds)
                {
                    Message = query.Message,
                    Label = query.Label,
                    IsQuery = true,
                };

                EnqueueEx(
                        new NMQCommand()
                        {
                            Message = queueItem,
                            CommandType = PayloadCommandType.Enqueue
                        }
                    );

                querySubscription.PayloadReceivedEvent.WaitOne(timeout);

                lock (messageQuerySubscriptions)
                {
                    messageQuerySubscriptions.Remove(querySubscription);
                }

                querySubscription.PayloadProcessedEvent.Set();

                return querySubscription.ReplyQueueItem;
            }
            catch (Exception ex)
            {
                LogException(ex);
            }

            return null;
        }

        /// <summary>
        /// Enqueues a reply to a message which was received.
        /// </summary>
        /// <param name="queryItem">The queuy message which was received.</param>
        /// <param name="label">User data, typically just a string that provides context to the message.</param>
        /// <param name="message">User data to be sent.</param>
        /// <returns>Returns the reply result which is to be returned by the OnQueryReceived event.</returns>
        public NMQQueryReplyResult Reply(NMQQuery query, NMQReply reply)
        {
            if (query == null || string.IsNullOrWhiteSpace(query.QueueName))
            {
                throw new Exception("No queue name was specified.");
            }

            if (_connectSocket == null || _connectSocket.Connected == false)
            {
                throw new Exception("The client is not connected to a server.");
            }

            try
            {
                var queueItem = new NMQMessageBase(PeerId, query.QueueName, Guid.NewGuid(), query.ExpireSeconds)
                {
                    Message = reply.Message,
                    Label = reply.Label,
                    InReplyToMessageId = query.MessageId
                };

                EnqueueEx(
                    new NMQCommand()
                    {
                        Message = queueItem,
                        CommandType = PayloadCommandType.Enqueue
                    }
                );
            }
            catch (Exception ex)
            {
                LogException(ex);
            }

            return new NMQQueryReplyResult();
        }

        /// <summary>
        /// Enqueues an empty reply to a message which was received.
        /// </summary>
        /// <param name="queryItem">The queuy message which was received.</param>
        /// <returns>Returns the reply result which is to be returned by the OnQueryReceived event.</returns>
        public NMQQueryReplyResult Reply(NMQQuery query)
        {
            if (query == null || string.IsNullOrWhiteSpace(query.QueueName))
            {
                throw new Exception("No queue name was specified.");
            }

            if (_connectSocket == null || _connectSocket.Connected == false)
            {
                throw new Exception("The client is not connected to a server.");
            }

            try
            {
                var queueItem = new NMQMessageBase(PeerId, query.QueueName, Guid.NewGuid(), query.ExpireSeconds)
                {
                    Message = string.Empty,
                    Label = string.Empty,
                    InReplyToMessageId = query.MessageId
                };

                EnqueueEx(
                    new NMQCommand()
                    {
                        Message = queueItem,
                        CommandType = PayloadCommandType.Enqueue
                    }
                );
            }
            catch (Exception ex)
            {
                LogException(ex);
            }

            return new NMQQueryReplyResult();
        }

        /// <summary>
        /// Enqueues a message which will be broadcast to all queue subscribers.
        /// </summary>
        /// <returns>True upon successful enqueue.</returns>
        public bool Enqueue(NMQNotification notification)
        {
            if (notification == null || string.IsNullOrWhiteSpace(notification.QueueName))
            {
                throw new Exception("No queue name was specified.");
            }

            var queueItem = new NMQMessageBase(PeerId, notification.QueueName, Guid.NewGuid(), notification.ExpireSeconds)
            {
                Message = notification.Message,
                Label = notification.Label,
                MessageId = Guid.NewGuid()
            };

            return EnqueueEx(
                    new NMQCommand()
                    {
                        Message = queueItem,
                        CommandType = PayloadCommandType.Enqueue
                    }
                );
        }

        /// <summary>
        /// Private low-level enqueue method.
        /// </summary>
        /// <param name="payload">Payload to be enqueued</param>
        /// <returns>True upon successful enqueue.</returns>
        private bool EnqueueEx(NMQCommand payload)
        {
            if (_connectSocket == null || _connectSocket.Connected == false)
            {
                throw new Exception("The client is not connected to a server.");
            }

            var messagePacket = Packetizer.AssembleMessagePacket(this, payload);

            try
            {
                //Create wait event and wait for ACK.
                NMQWaitEvent waitEvent = null;
                //We dont wait on replies because we seem to deadlock if we do.
                waitEvent = new NMQWaitEvent(payload.Message.MessageId);

                lock (_ackWaitEvents) _ackWaitEvents.Add($"{waitEvent.MessageId}", waitEvent);

                SendAsync(_connectSocket, messagePacket);
                OnEnqueued?.Invoke(this, payload.Message);

                if (payload.Message.IsReply == false)
                {
                    if (waitEvent.WaitOne(NMQConstants.ACK_TIMEOUT_MS) == false)
                    {
                        lock (_ackWaitEvents) _ackWaitEvents.Remove($"{waitEvent.MessageId}");
                    }
                }
            }
            catch (SocketException)
            {
                CloseSocket();
                return false;
            }
            catch (Exception ex)
            {
                LogException(ex);
            }

            return true;
        }

        /// <summary>
        /// Subscribes to a queue and receives any messages and queuries which are enqueued to it.
        /// </summary>
        /// <param name="queueName">The name of the queue to subscribe to.</param>
        public void Subscribe(string queueName)
        {
            var payload = new NMQCommand()
            {
                Message = new NMQMessageBase(PeerId, queueName, Guid.NewGuid(), 0),
                CommandType = PayloadCommandType.Subscribe
            };

            byte[] messagePacket = Packetizer.AssembleMessagePacket(this, payload);

            try
            {
                if (_subscribedQueues.Contains(queueName) == false)
                {
                    _subscribedQueues.Add(queueName);
                }

                if (_connectSocket != null && _connectSocket.Connected)
                {
                    _connectSocket.Send(messagePacket);
                    OnQueueSubscribed?.Invoke(this, queueName);
                }
            }
            catch (SocketException)
            {
                CloseSocket();
                return;
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
        }

        /// <summary>
        /// Removes an existing subscription to a queue (if it exists) so that further messages and queuries will not be received.
        /// </summary>
        /// <param name="queueName">The name of the queue to unsubscribe to.</param>
        public void UnSubscribe(string queueName)
        {
            var payload = new NMQCommand()
            {
                Message = new NMQMessageBase(PeerId, queueName, Guid.NewGuid(), 0),
                CommandType = PayloadCommandType.UnSubscribe
            };

            byte[] messagePacket = Packetizer.AssembleMessagePacket(this, payload);

            try
            {
                _subscribedQueues.Remove(queueName);

                if (_connectSocket != null && _connectSocket.Connected)
                {
                    _connectSocket.Send(messagePacket);
                    OnQueueUnsubscribed?.Invoke(this, queueName);
                }
            }
            catch (SocketException)
            {
                CloseSocket();
                return;
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
        }

        /// <summary>
        /// Removes all items from the queue.
        /// </summary>
        /// <param name="queueName">Name of the queue for which to clear.</param>
        public void Clear(string queueName)
        {
            var payload = new NMQCommand()
            {
                Message = new NMQMessageBase(PeerId, queueName, Guid.NewGuid(), 0),
                CommandType = PayloadCommandType.Clear
            };

            byte[] messagePacket = Packetizer.AssembleMessagePacket(this, payload);

            try
            {
                if (_connectSocket != null && _connectSocket.Connected)
                {
                    _connectSocket.Send(messagePacket);
                    OnQueueCleared?.Invoke(this, queueName);
                }
            }
            catch (SocketException)
            {
                CloseSocket();
                return;
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
        }

        private void PacketPayloadHandler(Peer peer, Packet packet, NMQCommand payload)
        {
            try
            {
                //the client just connected, the client said hello and the server is just waving back.
                if (payload.CommandType == PayloadCommandType.Hello)
                {
                    //Gald to see you server!
                }
                //The server is acknowledging the receipt of a command.
                else if (payload.CommandType == PayloadCommandType.CommandAck)
                {
                    var key = $"{payload.Message.MessageId}";
                    if (_ackWaitEvents.ContainsKey(key))
                    {
                        _ackWaitEvents[key].Set();
                        lock (_ackWaitEvents) _ackWaitEvents.Remove(key);
                    }
                    else
                    {
                        //Server... what are you ack'ing?
                    }
                }
                //The command is a message. It could be a query, a reply to a query or just a message.
                else if (payload.CommandType == PayloadCommandType.ProcessMessage)
                {
                    //The received command is a query.
                    if (payload.Message.IsQuery)
                    {
                        OnQueryReceived?.Invoke(this, payload.Message.As<NMQQuery>());
                    }
                    //This message is in reply to a query - match them up.
                    else if (payload.Message.IsReply)
                    {
                        NMQQuerySubscription messageQuerySubscription = null;

                        lock (messageQuerySubscriptions)
                        {
                            messageQuerySubscription = (from o in messageQuerySubscriptions
                                                        where o.OriginalMessageId == payload.Message.InReplyToMessageId
                                                        select o).FirstOrDefault();

                            OnQueryReplyReceived?.Invoke(this, payload.Message.As<NMQReply>(), messageQuerySubscription != null);

                            if (messageQuerySubscription != null)
                            {
                                messageQuerySubscription.SetReply(payload.Message);
                            }
                            else
                            {
                                //If there is no open query for this reply, then discard it.
                            }
                        }

                        if (messageQuerySubscription != null)
                        {
                            messageQuerySubscription.PayloadProcessedEvent.WaitOne();
                        }
                    }
                    //This is just a plain ol' message.
                    else
                    {
                        OnMessageReceived?.Invoke(this, payload.Message.As<NMQNotification>());
                    }
                }
                else
                {
                    throw new Exception("Command type is not implemented.");
                }

                #region It doesnt hurt to sent an ACK for each message.
                if (payload.CommandType != PayloadCommandType.CommandAck)
                {
                    var ackPayload = new NMQCommand()
                    {
                        Message = new NMQMessageBase
                        {
                            PeerId = payload.Message.PeerId,
                            MessageId = payload.Message.MessageId,
                            QueueName = payload.Message.QueueName
                        },
                        CommandType = PayloadCommandType.CommandAck
                    };

                    byte[] messagePacket = Packetizer.AssembleMessagePacket(this, ackPayload);
                    SendAsync(peer.Socket, messagePacket);
                }
                #endregion

            }
            catch (SocketException)
            {
                CloseSocket();
                return;
            }
            catch (Exception ex)
            {
                LogException(ex);
            }
        }

        #endregion
    }
}
