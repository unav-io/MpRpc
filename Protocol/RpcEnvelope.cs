using System.Collections.Generic;

namespace MpRpc.Protocol
{
    public static class RpcMessageTypes
    {
        public const string Request = "request";
        public const string Response = "response";
        public const string Event = "event";
        public const string Error = "error";
    }

    public class RpcEnvelope
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Method { get; set; }
        public string Encoding { get; set; } = "json";
        public Dictionary<string, object> Payload { get; set; } = new Dictionary<string, object>();
    }

    public class RpcError
    {
        public string Code { get; set; }
        public string Message { get; set; }
    }
}
