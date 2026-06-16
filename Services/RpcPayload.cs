using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace MpRpc.Services
{
    internal static class RpcPayload
    {
        public static string GetString(Dictionary<string, object> payload, string key, bool required = false)
        {
            object value;
            if (!TryGet(payload, key, out value) || value == null)
            {
                if (required)
                {
                    throw new ArgumentException($"Missing required field '{key}'.");
                }

                return null;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static double GetDouble(Dictionary<string, object> payload, string key, double defaultValue = 0)
        {
            object value;
            if (!TryGet(payload, key, out value) || value == null)
            {
                return defaultValue;
            }

            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        public static int GetInt(Dictionary<string, object> payload, string key, int defaultValue = 0)
        {
            object value;
            if (!TryGet(payload, key, out value) || value == null)
            {
                return defaultValue;
            }

            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }

        public static bool GetBool(Dictionary<string, object> payload, string key, bool defaultValue = false)
        {
            object value;
            if (!TryGet(payload, key, out value) || value == null)
            {
                return defaultValue;
            }

            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }

        public static ArrayList GetArray(Dictionary<string, object> payload, string key, bool required = false)
        {
            object value;
            if (!TryGet(payload, key, out value) || value == null)
            {
                if (required)
                {
                    throw new ArgumentException($"Missing required field '{key}'.");
                }

                return new ArrayList();
            }

            var list = value as ArrayList;
            if (list == null)
            {
                throw new ArgumentException($"Field '{key}' must be an array.");
            }

            return list;
        }

        public static Dictionary<string, object> GetObject(object value)
        {
            var obj = value as Dictionary<string, object>;
            if (obj == null)
            {
                throw new ArgumentException("Expected object payload item.");
            }

            return obj;
        }

        private static bool TryGet(Dictionary<string, object> payload, string key, out object value)
        {
            value = null;
            if (payload == null)
            {
                return false;
            }

            return payload.TryGetValue(key, out value);
        }
    }
}
