using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Web.Script.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MpRpc.Protocol;
using MpRpc.Router;
using MpRpc.Serialization;

namespace MpRpc.Transport
{
    public sealed class WebSocketGateway : IDisposable
    {
        private const string QuickTelemetryTopic = "quick";
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly HttpListener _listener;
        private readonly RpcCommandRouter _router;
        private CancellationTokenSource _cts;
        private Task _acceptLoopTask;

        public WebSocketGateway(string prefix, RpcCommandRouter router)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                throw new ArgumentException("Prefix is required.", nameof(prefix));
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _router = router ?? throw new ArgumentNullException(nameof(router));
        }

        public void Start()
        {
            if (_listener.IsListening)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _listener.Start();
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        }

        public void Stop()
        {
            if (!_listener.IsListening)
            {
                return;
            }

            _cts.Cancel();
            _listener.Stop();

            try
            {
                _acceptLoopTask?.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Ignore during shutdown.
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context = null;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (context == null)
                {
                    continue;
                }

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                _ = Task.Run(() => HandleClientAsync(context, token), token);
            }
        }

        private async Task HandleClientAsync(HttpListenerContext context, CancellationToken token)
        {
            WebSocketContext wsContext;
            try
            {
                wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            }
            catch
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
                return;
            }

            var socket = wsContext.WebSocket;
            var outboundQueue = new BlockingCollection<OutgoingFrame>(new ConcurrentQueue<OutgoingFrame>());
            var session = new ClientSession();
            var senderTask = Task.Run(() => SenderLoopAsync(socket, outboundQueue, token), token);
            var telemetryTask = Task.Run(() => TelemetryLoopAsync(outboundQueue, session, token), token);

            try
            {
                await ReceiveLoopAsync(socket, outboundQueue, session, token).ConfigureAwait(false);
            }
            finally
            {
                outboundQueue.CompleteAdding();
                try
                {
                    await senderTask.ConfigureAwait(false);
                }
                catch
                {
                    // ignore background send loop errors on close
                }

                try
                {
                    await telemetryTask.ConfigureAwait(false);
                }
                catch
                {
                    // ignore telemetry loop errors on close
                }

                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None)
                        .ConfigureAwait(false);
                }

                socket.Dispose();
                outboundQueue.Dispose();
            }
        }

        private async Task ReceiveLoopAsync(
            WebSocket socket,
            BlockingCollection<OutgoingFrame> outboundQueue,
            ClientSession session,
            CancellationToken token)
        {
            var buffer = new byte[16 * 1024];

            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var binary = new List<byte>(16 * 1024);
                WebSocketReceiveResult result;
                var messageType = WebSocketMessageType.Text;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    for (var i = 0; i < result.Count; i++)
                    {
                        binary.Add(buffer[i]);
                    }
                    messageType = result.MessageType;
                } while (!result.EndOfMessage);

                var response = await ProcessMessageAsync(binary.ToArray(), messageType, session).ConfigureAwait(false);
                outboundQueue.Add(SerializeEnvelope(response, session), token);
            }
        }

        private async Task<RpcEnvelope> ProcessMessageAsync(byte[] rawBytes, WebSocketMessageType messageType, ClientSession session)
        {
            try
            {
                RpcEnvelope request;
                if (messageType == WebSocketMessageType.Binary)
                {
                    request = ProtobufCodec.Decode(rawBytes);
                    session.PreferredEncoding = "protobuf";
                }
                else
                {
                    var rawMessage = Encoding.UTF8.GetString(rawBytes);
                    request = _json.Deserialize<RpcEnvelope>(rawMessage);
                    session.PreferredEncoding = (request?.Encoding ?? "json").ToLowerInvariant();
                }

                ProcessSubscriptionCommand(request, session);
                var response = await _router.RouteAsync(request).ConfigureAwait(false);
                response.Encoding = session.PreferredEncoding;
                return response;
            }
            catch (Exception ex)
            {
                return BuildError("invalid-request", ex.Message);
            }
        }

        private void ProcessSubscriptionCommand(RpcEnvelope request, ClientSession session)
        {
            if (request == null || request.Payload == null)
            {
                return;
            }

            var topic = QuickTelemetryTopic;
            if (request.Payload.ContainsKey("topic") && request.Payload["topic"] != null)
            {
                topic = request.Payload["topic"].ToString().ToLowerInvariant();
            }

            if (!string.Equals(topic, QuickTelemetryTopic, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(request.Method, "telemetry.subscribe", StringComparison.OrdinalIgnoreCase))
            {
                var rate = 5;
                if (request.Payload.ContainsKey("rateHz") && request.Payload["rateHz"] != null)
                {
                    int parsed;
                    if (int.TryParse(request.Payload["rateHz"].ToString(), out parsed))
                    {
                        rate = parsed;
                    }
                }

                rate = Math.Max(1, Math.Min(20, rate));
                session.QuickTelemetryRateHz = rate;
            }
            else if (string.Equals(request.Method, "telemetry.unsubscribe", StringComparison.OrdinalIgnoreCase))
            {
                session.QuickTelemetryRateHz = 0;
            }
        }

        private async Task TelemetryLoopAsync(
            BlockingCollection<OutgoingFrame> outboundQueue,
            ClientSession session,
            CancellationToken token)
        {
            while (!token.IsCancellationRequested && !outboundQueue.IsAddingCompleted)
            {
                var rateHz = session.QuickTelemetryRateHz;
                if (rateHz <= 0)
                {
                    await Task.Delay(150, token).ConfigureAwait(false);
                    continue;
                }

                var nextDelayMs = Math.Max(50, 1000 / rateHz);
                var request = new RpcEnvelope
                {
                    Id = "telemetry:" + Guid.NewGuid().ToString("N"),
                    Type = RpcMessageTypes.Request,
                    Method = "telemetry.quick",
                    Encoding = "json",
                    Payload = new Dictionary<string, object>()
                };

                RpcEnvelope response;
                try
                {
                    response = await _router.RouteAsync(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response = BuildError("telemetry-error", ex.Message);
                }

                var eventEnvelope = new RpcEnvelope
                {
                    Id = null,
                    Type = RpcMessageTypes.Event,
                    Method = "telemetry.quick",
                    Encoding = session.PreferredEncoding,
                    Payload = response.Payload ?? new Dictionary<string, object>()
                };

                // Backpressure: if consumer is slow, skip adding another update.
                if (outboundQueue.Count <= 2)
                {
                    outboundQueue.Add(SerializeEnvelope(eventEnvelope, session), token);
                }

                await Task.Delay(nextDelayMs, token).ConfigureAwait(false);
            }
        }

        private OutgoingFrame SerializeEnvelope(RpcEnvelope envelope, ClientSession session)
        {
            var encoding = (session?.PreferredEncoding ?? "json").ToLowerInvariant();
            if (encoding == "protobuf")
            {
                var protoBytes = ProtobufCodec.Encode(envelope);
                return new OutgoingFrame
                {
                    MessageType = WebSocketMessageType.Binary,
                    Bytes = protoBytes
                };
            }

            return new OutgoingFrame
            {
                MessageType = WebSocketMessageType.Text,
                Bytes = Encoding.UTF8.GetBytes(_json.Serialize(envelope))
            };
        }

        private static RpcEnvelope BuildError(string code, string message)
        {
            return new RpcEnvelope
            {
                Id = null,
                Type = RpcMessageTypes.Error,
                Method = "error",
                Encoding = "json",
                Payload = new Dictionary<string, object>
                {
                    {
                        "error",
                        new Dictionary<string, object>
                        {
                            { "code", code },
                            { "message", message }
                        }
                    }
                }
            };
        }

        private static async Task SenderLoopAsync(WebSocket socket, BlockingCollection<OutgoingFrame> outboundQueue, CancellationToken token)
        {
            foreach (var frame in outboundQueue.GetConsumingEnumerable(token))
            {
                if (socket.State != WebSocketState.Open)
                {
                    break;
                }

                await socket.SendAsync(new ArraySegment<byte>(frame.Bytes), frame.MessageType, true, token)
                    .ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            Stop();
            _listener.Close();
            _cts?.Dispose();
        }

        private sealed class ClientSession
        {
            public int QuickTelemetryRateHz { get; set; }
            public string PreferredEncoding { get; set; } = "json";
        }

        private sealed class OutgoingFrame
        {
            public WebSocketMessageType MessageType { get; set; }
            public byte[] Bytes { get; set; }
        }
    }
}
