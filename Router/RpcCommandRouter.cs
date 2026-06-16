using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MpRpc.Protocol;

namespace MpRpc.Router
{
    public class RpcCommandRouter
    {
        private readonly Dictionary<string, Func<RpcEnvelope, Task<Dictionary<string, object>>>> _handlers =
            new Dictionary<string, Func<RpcEnvelope, Task<Dictionary<string, object>>>>(StringComparer.OrdinalIgnoreCase);

        public RpcCommandRouter()
        {
            Register("system.ping", request =>
            {
                var payload = new Dictionary<string, object>
                {
                    { "ok", true },
                    { "utc", DateTime.UtcNow.ToString("O") }
                };
                return Task.FromResult(payload);
            });
        }

        public void Register(string method, Func<RpcEnvelope, Task<Dictionary<string, object>>> handler)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                throw new ArgumentException("Method must be provided.", nameof(method));
            }

            _handlers[method] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public async Task<RpcEnvelope> RouteAsync(RpcEnvelope request)
        {
            var validationError = ValidateRequest(request);
            if (validationError != null)
            {
                return validationError;
            }

            Func<RpcEnvelope, Task<Dictionary<string, object>>> handler;
            if (!_handlers.TryGetValue(request.Method, out handler))
            {
                return BuildError(request.Id, "method-not-found", $"Method '{request.Method}' is not registered.");
            }

            try
            {
                var payload = await handler(request).ConfigureAwait(false) ?? new Dictionary<string, object>();
                return new RpcEnvelope
                {
                    Id = request.Id,
                    Type = RpcMessageTypes.Response,
                    Method = request.Method,
                    Encoding = "json",
                    Payload = payload
                };
            }
            catch (TimeoutException ex)
            {
                return BuildError(request.Id, "timeout", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return BuildError(request.Id, "no-link", ex.Message);
            }
            catch (ArgumentException ex)
            {
                return BuildError(request.Id, "invalid-arg", ex.Message);
            }
            catch (Exception ex)
            {
                return BuildError(request.Id, "internal-error", ex.Message);
            }
        }

        private static RpcEnvelope ValidateRequest(RpcEnvelope request)
        {
            if (request == null)
            {
                return BuildError(null, "invalid-request", "Request is null.");
            }

            if (!string.Equals(request.Type, RpcMessageTypes.Request, StringComparison.OrdinalIgnoreCase))
            {
                return BuildError(request.Id, "invalid-type", "Only 'request' envelopes are accepted.");
            }

            if (string.IsNullOrWhiteSpace(request.Id))
            {
                return BuildError(null, "invalid-request", "Request id is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Method))
            {
                return BuildError(request.Id, "invalid-request", "Request method is required.");
            }

            if (!string.IsNullOrWhiteSpace(request.Encoding) &&
                !string.Equals(request.Encoding, "json", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(request.Encoding, "protobuf", StringComparison.OrdinalIgnoreCase))
            {
                return BuildError(request.Id, "unsupported-encoding", "Supported encodings: json, protobuf.");
            }

            return null;
        }

        private static RpcEnvelope BuildError(string id, string code, string message)
        {
            return new RpcEnvelope
            {
                Id = id,
                Type = RpcMessageTypes.Error,
                Method = "error",
                Encoding = "json",
                Payload = new Dictionary<string, object>
                {
                    { "error", new Dictionary<string, object>
                    {
                        { "code", code },
                        { "message", message }
                    }}
                }
            };
        }
    }
}
