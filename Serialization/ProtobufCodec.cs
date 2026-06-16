using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using MpRpc.Protocol;

namespace MpRpc.Serialization
{
    internal static class ProtobufCodec
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        // message RpcEnvelopeProto {
        //   string id = 1;
        //   string type = 2;
        //   string method = 3;
        //   string encoding = 4;
        //   string payload_json = 5;
        //   bytes payload_bin = 6; // typed payload for selected methods
        // }

        public static byte[] Encode(RpcEnvelope envelope)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            using (var ms = new MemoryStream())
            {
                WriteStringField(ms, 1, envelope.Id);
                WriteStringField(ms, 2, envelope.Type);
                WriteStringField(ms, 3, envelope.Method);
                WriteStringField(ms, 4, envelope.Encoding ?? "protobuf");
                WriteStringField(ms, 5, Json.Serialize(envelope.Payload ?? new Dictionary<string, object>()));
                var typedPayload = BuildTypedPayload(envelope);
                WriteBytesField(ms, 6, typedPayload);
                return ms.ToArray();
            }
        }

        public static RpcEnvelope Decode(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            var envelope = new RpcEnvelope
            {
                Payload = new Dictionary<string, object>(),
                Encoding = "protobuf"
            };

            using (var ms = new MemoryStream(bytes))
            {
                byte[] typedPayload = null;
                while (ms.Position < ms.Length)
                {
                    var tag = ReadVarint(ms);
                    if (tag == 0)
                    {
                        break;
                    }

                    var fieldNumber = (int)(tag >> 3);
                    var wireType = (int)(tag & 0x07);
                    if (wireType != 2)
                    {
                        SkipField(ms, wireType);
                        continue;
                    }

                    switch (fieldNumber)
                    {
                        case 1:
                            envelope.Id = ReadString(ms);
                            break;
                        case 2:
                            envelope.Type = ReadString(ms);
                            break;
                        case 3:
                            envelope.Method = ReadString(ms);
                            break;
                        case 4:
                            var value = ReadString(ms);
                            envelope.Encoding = string.IsNullOrWhiteSpace(value) ? "protobuf" : value;
                            break;
                        case 5:
                            envelope.Payload = DeserializePayload(ReadString(ms));
                            break;
                        case 6:
                            typedPayload = ReadBytes(ms);
                            break;
                        default:
                            SkipField(ms, wireType);
                            break;
                    }
                }

                if (typedPayload != null && typedPayload.Length > 0)
                {
                    TryApplyTypedPayload(envelope, typedPayload);
                }
            }

            return envelope;
        }

        private static Dictionary<string, object> DeserializePayload(string payloadJson)
        {
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return new Dictionary<string, object>();
            }

            var obj = Json.DeserializeObject(payloadJson) as Dictionary<string, object>;
            return obj ?? new Dictionary<string, object>();
        }

        private static void WriteStringField(Stream stream, int fieldNumber, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            WriteVarint(stream, (ulong)((fieldNumber << 3) | 2));
            var data = Encoding.UTF8.GetBytes(value);
            WriteVarint(stream, (ulong)data.Length);
            stream.Write(data, 0, data.Length);
        }

        private static void WriteBytesField(Stream stream, int fieldNumber, byte[] value)
        {
            if (value == null || value.Length == 0)
            {
                return;
            }

            WriteVarint(stream, (ulong)((fieldNumber << 3) | 2));
            WriteVarint(stream, (ulong)value.Length);
            stream.Write(value, 0, value.Length);
        }

        private static string ReadString(Stream stream)
        {
            var len = (int)ReadVarint(stream);
            if (len <= 0)
            {
                return string.Empty;
            }

            var data = new byte[len];
            var read = stream.Read(data, 0, len);
            if (read != len)
            {
                throw new EndOfStreamException("Unexpected end of protobuf frame.");
            }

            return Encoding.UTF8.GetString(data);
        }

        private static byte[] ReadBytes(Stream stream)
        {
            var len = (int)ReadVarint(stream);
            if (len <= 0)
            {
                return new byte[0];
            }

            var data = new byte[len];
            var read = stream.Read(data, 0, len);
            if (read != len)
            {
                throw new EndOfStreamException("Unexpected end of protobuf bytes.");
            }

            return data;
        }

        private static void WriteVarint(Stream stream, ulong value)
        {
            while (value > 127)
            {
                stream.WriteByte((byte)((value & 0x7F) | 0x80));
                value >>= 7;
            }

            stream.WriteByte((byte)value);
        }

        private static ulong ReadVarint(Stream stream)
        {
            ulong result = 0;
            var shift = 0;
            while (shift < 64)
            {
                var b = stream.ReadByte();
                if (b < 0)
                {
                    throw new EndOfStreamException("Unexpected end of protobuf varint.");
                }

                result |= ((ulong)(b & 0x7F)) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }

            throw new InvalidDataException("Invalid protobuf varint.");
        }

        private static void SkipField(Stream stream, int wireType)
        {
            switch (wireType)
            {
                case 0:
                    ReadVarint(stream);
                    return;
                case 1:
                    stream.Position += 8;
                    return;
                case 2:
                    var len = (int)ReadVarint(stream);
                    stream.Position += len;
                    return;
                case 5:
                    stream.Position += 4;
                    return;
                default:
                    throw new InvalidDataException("Unsupported protobuf wire type.");
            }
        }

        private static byte[] BuildTypedPayload(RpcEnvelope envelope)
        {
            if (envelope == null || envelope.Payload == null)
            {
                return null;
            }

            if (!string.Equals(envelope.Method, "telemetry.quick", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return EncodeQuickTelemetryPayload(envelope.Payload);
        }

        private static void TryApplyTypedPayload(RpcEnvelope envelope, byte[] typedPayload)
        {
            if (envelope == null || typedPayload == null || typedPayload.Length == 0)
            {
                return;
            }

            if (!string.Equals(envelope.Method, "telemetry.quick", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            envelope.Payload = DecodeQuickTelemetryPayload(typedPayload);
        }

        // message QuickTelemetryProto
        // { string timestampUtc=1; string mode=2; bool armed=3; double lat=4; double lng=5;
        //   float alt=6; float groundspeed=7; float airspeed=8; int32 batteryRemaining=9;
        //   double batteryVoltage=10; float satcount=11; float wpno=12; }
        private static byte[] EncodeQuickTelemetryPayload(Dictionary<string, object> payload)
        {
            using (var ms = new MemoryStream())
            {
                WriteStringField(ms, 1, AsString(payload, "timestampUtc"));
                WriteStringField(ms, 2, AsString(payload, "mode"));
                WriteBoolField(ms, 3, AsBool(payload, "armed"));
                WriteDoubleField(ms, 4, AsDouble(payload, "lat"));
                WriteDoubleField(ms, 5, AsDouble(payload, "lng"));
                WriteFloatField(ms, 6, AsFloat(payload, "alt"));
                WriteFloatField(ms, 7, AsFloat(payload, "groundspeed"));
                WriteFloatField(ms, 8, AsFloat(payload, "airspeed"));
                WriteInt32Field(ms, 9, AsInt(payload, "batteryRemaining"));
                WriteDoubleField(ms, 10, AsDouble(payload, "batteryVoltage"));
                WriteFloatField(ms, 11, AsFloat(payload, "satcount"));
                WriteFloatField(ms, 12, AsFloat(payload, "wpno"));
                return ms.ToArray();
            }
        }

        private static Dictionary<string, object> DecodeQuickTelemetryPayload(byte[] bytes)
        {
            var result = new Dictionary<string, object>();
            using (var ms = new MemoryStream(bytes))
            {
                while (ms.Position < ms.Length)
                {
                    var tag = ReadVarint(ms);
                    if (tag == 0)
                    {
                        break;
                    }

                    var fieldNumber = (int)(tag >> 3);
                    var wireType = (int)(tag & 0x07);
                    switch (fieldNumber)
                    {
                        case 1: result["timestampUtc"] = ReadString(ms); break;
                        case 2: result["mode"] = ReadString(ms); break;
                        case 3: result["armed"] = ReadVarint(ms) != 0; break;
                        case 4: result["lat"] = ReadDouble(ms); break;
                        case 5: result["lng"] = ReadDouble(ms); break;
                        case 6: result["alt"] = ReadFloat(ms); break;
                        case 7: result["groundspeed"] = ReadFloat(ms); break;
                        case 8: result["airspeed"] = ReadFloat(ms); break;
                        case 9: result["batteryRemaining"] = (int)ReadVarint(ms); break;
                        case 10: result["batteryVoltage"] = ReadDouble(ms); break;
                        case 11: result["satcount"] = ReadFloat(ms); break;
                        case 12: result["wpno"] = ReadFloat(ms); break;
                        default: SkipField(ms, wireType); break;
                    }
                }
            }

            return result;
        }

        private static void WriteBoolField(Stream stream, int fieldNumber, bool value)
        {
            WriteVarint(stream, (ulong)((fieldNumber << 3) | 0));
            WriteVarint(stream, value ? 1UL : 0UL);
        }

        private static void WriteInt32Field(Stream stream, int fieldNumber, int value)
        {
            WriteVarint(stream, (ulong)((fieldNumber << 3) | 0));
            WriteVarint(stream, (ulong)value);
        }

        private static void WriteFloatField(Stream stream, int fieldNumber, float value)
        {
            WriteVarint(stream, (ulong)((fieldNumber << 3) | 5));
            var bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static void WriteDoubleField(Stream stream, int fieldNumber, double value)
        {
            WriteVarint(stream, (ulong)((fieldNumber << 3) | 1));
            var bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static float ReadFloat(Stream stream)
        {
            var data = new byte[4];
            if (stream.Read(data, 0, 4) != 4)
            {
                throw new EndOfStreamException("Unexpected end of protobuf float.");
            }

            return BitConverter.ToSingle(data, 0);
        }

        private static double ReadDouble(Stream stream)
        {
            var data = new byte[8];
            if (stream.Read(data, 0, 8) != 8)
            {
                throw new EndOfStreamException("Unexpected end of protobuf double.");
            }

            return BitConverter.ToDouble(data, 0);
        }

        private static string AsString(Dictionary<string, object> payload, string key)
        {
            object value;
            return payload.TryGetValue(key, out value) && value != null ? value.ToString() : null;
        }

        private static bool AsBool(Dictionary<string, object> payload, string key)
        {
            object value;
            bool parsed;
            return payload.TryGetValue(key, out value) && value != null && bool.TryParse(value.ToString(), out parsed) && parsed;
        }

        private static int AsInt(Dictionary<string, object> payload, string key)
        {
            object value;
            int parsed;
            return payload.TryGetValue(key, out value) && value != null && int.TryParse(value.ToString(), out parsed) ? parsed : 0;
        }

        private static float AsFloat(Dictionary<string, object> payload, string key)
        {
            object value;
            float parsed;
            return payload.TryGetValue(key, out value) && value != null && float.TryParse(value.ToString(), out parsed) ? parsed : 0;
        }

        private static double AsDouble(Dictionary<string, object> payload, string key)
        {
            object value;
            double parsed;
            return payload.TryGetValue(key, out value) && value != null && double.TryParse(value.ToString(), out parsed) ? parsed : 0;
        }
    }
}
