using System;
using System.Text;
using System.Web.Script.Serialization;
using MpRpc.Protocol;

namespace MpRpc.Serialization
{
    internal static class JsonCodec
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static byte[] Encode(RpcEnvelope envelope)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            return Encoding.UTF8.GetBytes(Json.Serialize(envelope));
        }

        public static RpcEnvelope Decode(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            var raw = Encoding.UTF8.GetString(bytes);
            return Json.Deserialize<RpcEnvelope>(raw);
        }
    }
}
