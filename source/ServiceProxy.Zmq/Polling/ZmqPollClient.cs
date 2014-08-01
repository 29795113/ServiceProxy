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
    public class ZmqPollClient : IClient, IDisposable
    {
        private readonly string brokerFrontendAddress;

        private readonly IZmqContext zmqContext;

        private long running;
        private volatile Task sendReceiveTask;

        private long nextId;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ResponseData>> requestCallbacks;

        private readonly BlockingCollection<byte[]> requestsQueue;

        public ZmqPollClient(IZmqContext zmqContext,
                         string brokerFrontendAddress)
        {
            this.zmqContext = zmqContext;

            this.brokerFrontendAddress = brokerFrontendAddress;

            this.nextId = 0;
            this.requestCallbacks = new ConcurrentDictionary<string, TaskCompletionSource<ResponseData>>();

            this.requestsQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>(), int.MaxValue);
        }

        public Task<ResponseData> Request(RequestData request, CancellationToken token)
        {
            this.EnsureIsRunning();

            var requestId = this.NextId();

            var callback = new TaskCompletionSource<ResponseData>();
            this.requestCallbacks[requestId] = callback;

            Task.Run(() =>
            {
                var zmqRequest = new ZmqRequest(requestId, request);
                var zmqRequestBytes = zmqRequest.ToBinary();

                this.requestsQueue.TryAdd(zmqRequestBytes);
            });

            if (token != CancellationToken.None)
            {
                token.Register(() =>
                {
                    TaskCompletionSource<ResponseData> _;
                    this.requestCallbacks.TryRemove(requestId, out _);
                });
            }

            return callback.Task;
        }

        private string NextId()
        {
            return Interlocked.Increment(ref this.nextId).ToString();
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
            using (var socket = this.zmqContext.CreateNonBlockingSocket(SocketType.Dealer, TimeSpan.FromMilliseconds(1)))
            {
                //Connect to outbound address
                socket.Connect(this.brokerFrontendAddress);

                //these variables are a hack to ensure safe usage inside the socket events.
                var canSend = false;
                var canReceive = false;
                int? tryTakeTimeout = null;

                var poller = new Castle.Zmq.Polling(PollingEvents.RecvReady | PollingEvents.SendReady, socket);

                poller.SendReady = s =>
                {
                    if (canSend)
                    {
                        byte[] request;
                        if (
                            tryTakeTimeout.HasValue ?
                                this.requestsQueue.TryTake(out request, tryTakeTimeout.Value) :
                                this.requestsQueue.TryTake(out request))
                        {
                            s.Send(request);
                        }
                    }
                };

                poller.RecvReady = s =>
                {
                    if (canReceive)
                    {
                        byte[] response = s.Recv();

                        if (response != null)
                        {
                            this.OnResponse(response);
                        }
                    }
                };

                var pollTimeout = 10; //ms

                while (Interlocked.Read(ref this.running) == 1)
                {
                    tryTakeTimeout = null;

                    //idle
                    if (this.requestsQueue.Count == 0 && this.requestCallbacks.Count == 0)
                    {
                        //can't do anything except wait for a request to be sent
                        canReceive = false;
                        canSend = true;
                        tryTakeTimeout = 1000;
                    }
                    else if (this.requestsQueue.Count == 0 && this.requestCallbacks.Count > 0)
                    {
                        canSend = false;
                        canReceive = true;
                    }
                    else if (this.requestsQueue.Count > 0 && this.requestCallbacks.Count == 0)
                    {
                        canSend = true;
                        canReceive = false;
                    }
                    else
                    {
                        canSend = true;
                        canReceive = true;
                    }

                    poller.Poll(pollTimeout);
                }
            }
        }

        private void OnResponse(byte[] response)
        {
            Task.Run(() =>
            {
                var zmqResponse = ZmqResponse.FromBinary(response);

                TaskCompletionSource<ResponseData> callback;
                if (this.requestCallbacks.TryRemove(zmqResponse.RequestId, out callback))
                {
                    callback.SetResult(zmqResponse.Response);
                }
            });
        }

        public void Dispose()
        {
            this.EnsureIsNotRunning();
        }
    }
}
