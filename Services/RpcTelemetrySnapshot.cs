using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using MissionPlanner;
using MissionPlanner.Attributes;

namespace MpRpc.Services
{
    /// <summary>
    /// Builds telemetry.quick payloads from CurrentState via reflection (no per-field hard-coding).
    /// </summary>
    internal static class RpcTelemetrySnapshot
    {
        private static readonly Lazy<List<PropertyInfo>> CachedProperties = new Lazy<List<PropertyInfo>>(Discover);

        public static Dictionary<string, object> Build(CurrentState currentState)
        {
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                {
                    "timestampUtc",
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                }
            };

            if (currentState == null)
            {
                return payload;
            }

            foreach (var property in CachedProperties.Value)
            {
                object value;
                try
                {
                    value = property.GetValue(currentState, null);
                }
                catch
                {
                    continue;
                }

                if (value == null)
                {
                    continue;
                }

                var normalized = NormalizeValue(value);
                if (normalized == null)
                {
                    continue;
                }

                payload[GetPayloadKey(property.Name)] = normalized;
            }

            return payload;
        }

        private static List<PropertyInfo> Discover()
        {
            var result = new List<PropertyInfo>();
            var flags = BindingFlags.Instance | BindingFlags.Public;

            foreach (var property in typeof(CurrentState).GetProperties(flags))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (HasIgnoreAttribute(property) || !IsTelemetryProperty(property))
                {
                    continue;
                }

                result.Add(property);
            }

            return result;
        }

        private static bool IsTelemetryProperty(PropertyInfo property)
        {
            if (!IsSerializableScalarType(property.PropertyType))
            {
                return false;
            }

            if (property.Name.StartsWith("customfield", StringComparison.Ordinal))
            {
                return CurrentState.custom_field_names != null
                    && CurrentState.custom_field_names.ContainsKey(property.Name);
            }

            return HasRawAttribute(property, typeof(GroupText))
                || HasRawAttribute(property, typeof(DisplayTextAttribute))
                || HasRawAttribute(property, typeof(DisplayFieldNameAttribute));
        }

        private static bool IsSerializableScalarType(Type type)
        {
            if (type == typeof(string) || type == typeof(bool) || type == typeof(decimal) || type == typeof(DateTime))
            {
                return true;
            }

            return type.IsPrimitive || type.IsEnum;
        }

        private static bool HasIgnoreAttribute(PropertyInfo property)
        {
            foreach (var data in property.GetCustomAttributesData())
            {
                var name = data.AttributeType.Name;
                if (name == "JsonIgnoreAttribute" || name == "IgnoreDataMemberAttribute")
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRawAttribute(PropertyInfo property, Type attributeType)
        {
            foreach (var data in property.GetCustomAttributesData())
            {
                if (data.AttributeType == attributeType)
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetPayloadKey(string propertyName)
        {
            switch (propertyName)
            {
                case "battery_remaining":
                    return "batteryRemaining";
                case "battery_voltage":
                    return "batteryVoltage";
                default:
                    return propertyName;
            }
        }

        private static object NormalizeValue(object value)
        {
            if (value is float f)
            {
                return float.IsNaN(f) || float.IsInfinity(f) ? null : (object)f;
            }

            if (value is double d)
            {
                return double.IsNaN(d) || double.IsInfinity(d) ? null : (object)d;
            }

            if (value is Enum)
            {
                return value.ToString();
            }

            if (value is DateTime dateTime)
            {
                return dateTime.ToString("O", CultureInfo.InvariantCulture);
            }

            return value;
        }
    }
}
