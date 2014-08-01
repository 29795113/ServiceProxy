﻿using Castle.Zmq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceProxy.Zmq.Polling
{
    public class ZmqPollServer : IDisposable
    {
        private readonly string brokerBackendAddress;

        private readonly IZmqContext zmqContext;

        private long running;
        private volatile Task sendReceiveTask;

        private readonly BlockingCollection<Tuple<byte[], byte[]>> responsesQueue;

        private readonly IServiceFactory serviceFactory;

        public ZmqPollServer(IZmqContext zmqContext,
                         string brokerBackendAddress,
                         IServiceFactory serviceFactory)
        {
            this.zmqContext = zmqContext;

            this.brokerBackendAddress = brokerBackendAddress;

            this.responsesQueue = new BlockingCollection<Tuple<byte[], byte[]>>(new ConcurrentQueue<Tuple<byte[], byte[]>>(), int.MaxValue);

            this.serviceFactory = serviceFactory;
        }

        public void Listen()
        {
            this.EnsureIsRunning();
        }

        private void EnsureIsRunning()
        {
            if (Interlocked.CompareExchange(ref this.running, 1, 0) == 0)
            {
                this.sendReceiveTask = Task.Factory.StartNew(this.SendReceive, TaskCreationOptions.LongRunning);
            }
        }

        private void EnsureIsNotRunning()
        {
            if (Interlocked.CompareExchange(ref this.running, 0, 1) == 1)
            {
                this.sendReceiveTask.Wait();
                this.sendReceiveTask = null;
            }
        }

        private void SendReceive()
        {
            using (var socket = this.zmqContext.CreateNonBlockingSocket(SocketType.Dealer, TimeSpan.FromMilliseconds(100)))
            {
                //Connect to inbound address
                socket.Connect(this.brokerBackendAddress);

                //these variables are a hack to ensure safe usage inside the socket events.
                var sendTimeoutIncr = 10;
                var sendTimeoutMax = 100;
                var sendTimeout = 0;

                var poller = new Castle.Zmq.Polling(PollingEvents.RecvReady | PollingEvents.SendReady, socket);

                poller.RecvReady = s =>
                {
                    byte[] callerId;
                    byte[] requestBytes;

                    callerId = s.Recv();
                    if (callerId != null && s.HasMoreToRecv())
                    {
                        requestBytes = s.Recv();
                        this.OnRequest(callerId, requestBytes);
                    }

                    sendTimeout = 0;
                };

                poller.SendReady = s =>
                {
                    //This delegate shouldn't need to be invoked when there are no items in the responsesQueue

                    //Tweaking the sendTimeout and blocking for longer periods on TryTake
                    //is a hack because the current clrzmq binding version doesn't support
                    //polling on the socket's output event directly. The previous version supported it.
                    Tuple<byte[], byte[]> response;
                    if (sendTimeout != 0 ?
                        this.responsesQueue.TryTake(out response, sendTimeout) :
                        this.responsesQueue.TryTake(out response))
                    {
                        s.Send(response.Item1, hasMoreToSend: true); //callerId
                        s.Send(response.Item2); //response
                    }
                    else
                    {
                        if (sendTimeout < sendTimeoutMax)
                        {
                            sendTimeout += sendTimeoutIncr;
                        }
                    }
                };

                //previous clrzmq binding supported different pollitems, per socket-event type and not per socket only
                var pollTimeout = 10; //ms

                while (Interlocked.Read(ref this.running) == 1)
                {
                    poller.Poll(pollTimeout);
                }
            }
        }

        private void OnRequest(byte[] callerId, byte[] requestBytes)
        {
            Task.Run(() =>
            {
                var zmqRequest = ZmqRequest.FromBinary(requestBytes);

                var service = this.serviceFactory.CreateService(zmqRequest.Request.Service);

                service.Process(zmqRequest.Request)
                       .ContinueWith(t =>
                       {
                           var response = t.Result;

                           var zmqResponse = new ZmqResponse(zmqRequest.Id, response);
                           var zmqResponseBytes = zmqResponse.ToBinary();

                           this.responsesQueue.TryAdd(new Tuple<byte[], byte[]>(callerId, zmqResponseBytes));
                       });
            });
        }

        public void Dispose()
        {
            this.EnsureIsNotRunning();
        }
    }
}
