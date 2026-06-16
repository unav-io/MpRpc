using MissionPlanner;
using MissionPlanner.ArduPilot;
using MissionPlanner.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MpRpc.Protocol;
using MpRpc.Router;
using static MAVLink;

namespace MpRpc.Services
{
    internal sealed class RpcServiceRegistry
    {
        public void RegisterAll(RpcCommandRouter router)
        {
            if (router == null)
            {
                throw new ArgumentNullException(nameof(router));
            }

            router.Register("waypoints.read", request => Task.FromResult(ReadWaypoints(request)));
            router.Register("waypoints.write", request => Task.FromResult(WriteWaypoints(request)));
            router.Register("waypoints.setCurrent", request => Task.FromResult(SetCurrentWaypoint(request)));
            router.Register("params.get", request => Task.FromResult(GetParam(request)));
            router.Register("params.set", request => Task.FromResult(SetParam(request)));
            router.Register("params.list", request => Task.FromResult(ListParams(request)));
            router.Register("flightmode.set", request => Task.FromResult(SetFlightMode(request)));
            router.Register("flightmode.list", request => Task.FromResult(ListFlightModes(request)));
            router.Register("vehicle.info", request => Task.FromResult(GetVehicleInfo(request)));
            router.Register("actions.exec", request => Task.FromResult(ExecAction(request)));
            router.Register("telemetry.quick", request => Task.FromResult(GetQuickTelemetry(request)));
            router.Register("telemetry.subscribe", request => Task.FromResult(SubscribeTelemetry(request)));
            router.Register("telemetry.unsubscribe", request => Task.FromResult(UnsubscribeTelemetry(request)));
            router.Register("mavlink.send", request => Task.FromResult(SendMavLinkMessage(request)));
            router.Register("mavlink.last", request => Task.FromResult(GetLastMavLinkMessage(request)));
        }

        private static MAVLinkInterface GetComPort()
        {
            var port = MainV2.comPort;
            if (port == null)
            {
                throw new InvalidOperationException("MissionPlanner comPort is not available.");
            }

            return port;
        }

        private static void EnsureConnected(MAVLinkInterface port)
        {
            if (port.BaseStream == null || !port.BaseStream.IsOpen)
            {
                throw new InvalidOperationException("Vehicle is not connected.");
            }
        }

        private static Dictionary<string, object> ReadWaypoints(RpcEnvelope request)
        {
            var port = GetComPort();
            EnsureConnected(port);

            var sysid = (byte)port.sysidcurrent;
            var compid = (byte)port.compidcurrent;
            var count = port.getWPCount(sysid, compid, MAV_MISSION_TYPE.MISSION);
            var items = new List<Dictionary<string, object>>();

            for (ushort i = 0; i < count; i++)
            {
                var wp = port.getWP(sysid, compid, i, MAV_MISSION_TYPE.MISSION);
                items.Add(WpToDictionary(i, wp));
            }

            return new Dictionary<string, object>
            {
                { "count", items.Count },
                { "items", items }
            };
        }

        private static Dictionary<string, object> WriteWaypoints(RpcEnvelope request)
        {
            var payload = request.Payload ?? new Dictionary<string, object>();
            var itemsRaw = RpcPayload.GetArray(payload, "items", required: true);
            var port = GetComPort();
            EnsureConnected(port);

            var sysid = (byte)port.sysidcurrent;
            var compid = (byte)port.compidcurrent;
            var missionItems = new List<Locationwp>();

            foreach (var itemRaw in itemsRaw)
            {
                var item = RpcPayload.GetObject(itemRaw);
                missionItems.Add(DictionaryToWp(item));
            }

            port.setWPTotal((ushort)missionItems.Count, MAV_MISSION_TYPE.MISSION);
            for (ushort i = 0; i < missionItems.Count; i++)
            {
                var wp = missionItems[i];
                var frame = (MAV_FRAME)wp.frame;
                var result = port.setWP(sysid, compid, wp, i, frame);
                if (result != MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED)
                {
                    throw new InvalidOperationException($"setWP failed at index {i}: {result}");
                }
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "written", missionItems.Count }
            };
        }

        private static Dictionary<string, object> SetCurrentWaypoint(RpcEnvelope request)
        {
            var payload = request.Payload ?? new Dictionary<string, object>();
            var index = RpcPayload.GetInt(payload, "index");
            if (index < 0)
            {
                throw new ArgumentException("Waypoint index must be zero or greater.");
            }

            var port = GetComPort();
            EnsureConnected(port);

            port.setWPCurrent((byte)port.sysidcurrent, (byte)port.compidcurrent, (ushort)index);
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "index", index }
            };
        }

        private static Dictionary<string, object> GetParam(RpcEnvelope request)
        {
            var payload = request.Payload ?? new Dictionary<string, object>();
            var name = RpcPayload.GetString(payload, "name", required: true);
            var requireResponse = RpcPayload.GetBool(payload, "requireResponse", true);

            var port = GetComPort();
            EnsureConnected(port);

            var value = port.GetParam(name, -1, requireResponse);
            return new Dictionary<string, object>
            {
                { "name", name },
                { "value", value }
            };
        }

        private static Dictionary<string, object> SetParam(RpcEnvelope request)
        {
            var payload = request.Payload ?? new Dictionary<string, object>();
            var name = RpcPayload.GetString(payload, "name", required: true);
            var value = RpcPayload.GetDouble(payload, "value");
            var force = RpcPayload.GetBool(payload, "force");

            var port = GetComPort();
            EnsureConnected(port);

            var ok = port.setParam(name, value, force);
            return new Dictionary<string, object>
            {
                { "ok", ok },
                { "name", name },
                { "value", value }
            };
        }

        private static Dictionary<string, object> ListParams(RpcEnvelope request)
        {
            var port = GetComPort();
            EnsureConnected(port);

            var list = new List<Dictionary<string, object>>();
            foreach (var kv in port.MAV.param)
            {
                // param list is shared; keep payload lightweight and serializable
                list.Add(new Dictionary<string, object>
                {
                    { "name", kv.Name },
                    { "value", kv.Value }
                });
            }

            return new Dictionary<string, object>
            {
                { "count", list.Count },
                { "items", list }
            };
        }

        private static Dictionary<string, object> SetFlightMode(RpcEnvelope request)
        {
            var payload = request.Payload ?? new Dictionary<string, object>();
            var mode = RpcPayload.GetString(payload, "mode", required: true);

            var port = GetComPort();
            EnsureConnected(port);
            port.setMode((byte)port.sysidcurrent, (byte)port.compidcurrent, mode);

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "mode", port.MAV.cs.mode }
            };
        }

        private static Dictionary<string, object> ListFlightModes(RpcEnvelope request)
        {
            var port = GetComPort();
            EnsureConnected(port);

            var firmware = port.MAV.cs.firmware;
            var modes = MissionPlanner.ArduPilot.Common.getModesList(firmware) ?? new List<KeyValuePair<int, string>>();
            var items = modes.Select(m => new Dictionary<string, object>
            {
                { "id", m.Key },
                { "name", m.Value }
            }).Cast<object>().ToList();

            return new Dictionary<string, object>
            {
                { "firmware", firmware.ToString() },
                { "currentMode", port.MAV.cs.mode },
                { "items", items }
            };
        }

        private static Dictionary<string, object> GetVehicleInfo(RpcEnvelope request)
        {
            var port = GetComPort();
            EnsureConnected(port);
            var mav = port.MAV;

            return new Dictionary<string, object>
            {
                { "firmware", mav.cs.firmware.ToString() },
                { "type", mav.aptype.ToString() },
                { "versionString", mav.VersionString ?? string.Empty },
                { "softwareVersions", mav.SoftwareVersions ?? string.Empty },
                { "serial", mav.SerialString ?? string.Empty },
                { "frame", mav.FrameString ?? string.Empty },
                { "sysid", (int)mav.sysid }
            };
        }

        private static Dictionary<string, object> ExecAction(RpcEnvelope request)
        {
            var payload = request.Payload ?? new Dictionary<string, object>();
            var commandName = RpcPayload.GetString(payload, "command", required: true);
            var p1 = (float)RpcPayload.GetDouble(payload, "p1");
            var p2 = (float)RpcPayload.GetDouble(payload, "p2");
            var p3 = (float)RpcPayload.GetDouble(payload, "p3");
            var p4 = (float)RpcPayload.GetDouble(payload, "p4");
            var p5 = (float)RpcPayload.GetDouble(payload, "p5");
            var p6 = (float)RpcPayload.GetDouble(payload, "p6");
            var p7 = (float)RpcPayload.GetDouble(payload, "p7");

            MAV_CMD cmd;
            if (!Enum.TryParse(commandName, true, out cmd))
            {
                throw new ArgumentException($"Unknown MAV_CMD '{commandName}'.");
            }

            if (!IsAllowedAction(cmd))
            {
                throw new InvalidOperationException($"Action '{cmd}' is not allowed in v1.");
            }

            var port = GetComPort();
            EnsureConnected(port);
            var ok = port.doCommand(cmd, p1, p2, p3, p4, p5, p6, p7, true);

            return new Dictionary<string, object>
            {
                { "ok", ok },
                { "command", cmd.ToString() }
            };
        }

        private static Dictionary<string, object> GetQuickTelemetry(RpcEnvelope request)
        {
            var port = GetComPort();
            return RpcTelemetrySnapshot.Build(port.MAV?.cs);
        }

        private static Dictionary<string, object> SubscribeTelemetry(RpcEnvelope request)
        {
            var payload = request.Payload ?? new Dictionary<string, object>();
            var topic = RpcPayload.GetString(payload, "topic") ?? "quick";
            var rateHz = RpcPayload.GetInt(payload, "rateHz", 5);
            if (rateHz < 1)
            {
                rateHz = 1;
            }

            if (rateHz > 20)
            {
                rateHz = 20;
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "topic", topic },
                { "rateHz", rateHz },
                { "mode", "push-v1" }
            };
        }

        private static Dictionary<string, object> UnsubscribeTelemetry(RpcEnvelope request)
        {
            var payload = request.Payload ?? new Dictionary<string, object>();
            var topic = RpcPayload.GetString(payload, "topic") ?? "quick";
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "topic", topic }
            };
        }

        private static Dictionary<string, object> SendMavLinkMessage(RpcEnvelope request)
        {
            var payload = request.Payload ?? new Dictionary<string, object>();
            var typeName = RpcPayload.GetString(payload, "type", required: true);

            MAVLINK_MSG_ID msgId;
            if (!Enum.TryParse(typeName, true, out msgId))
            {
                throw new ArgumentException($"Unknown MAVLink message type '{typeName}'.");
            }

            var structName = "mavlink_" + typeName.ToLowerInvariant() + "_t";
            var structType = typeof(MAVLink).GetNestedType(structName, BindingFlags.Public | BindingFlags.NonPublic);
            if (structType == null)
            {
                throw new ArgumentException($"MAVLink struct '{structName}' not found.");
            }

            var packet = Activator.CreateInstance(structType);
            var fieldsDict = ExtractMavLinkFields(payload);

            foreach (var fi in structType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object raw;
                if (!fieldsDict.TryGetValue(fi.Name, out raw) || raw == null)
                {
                    continue;
                }

                try
                {
                    fi.SetValue(packet, Convert.ChangeType(raw, fi.FieldType, CultureInfo.InvariantCulture));
                }
                catch
                {
                    // Skip fields that cannot be converted
                }
            }

            var port = GetComPort();
            EnsureConnected(port);
            MissionPlannerReflector.InvokeMethod(port, "sendPacket", packet, (byte)port.sysidcurrent, (byte)port.compidcurrent);

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "type", typeName.ToUpperInvariant() }
            };
        }

        private static Dictionary<string, object> GetLastMavLinkMessage(RpcEnvelope request)
        {
            var payload = request.Payload ?? new Dictionary<string, object>();
            var typeName = RpcPayload.GetString(payload, "type", required: true);

            MAVLINK_MSG_ID msgId;
            if (!Enum.TryParse(typeName, true, out msgId))
            {
                throw new ArgumentException($"Unknown MAVLink message type '{typeName}'.");
            }

            var port = GetComPort();

            object packetsRaw;
            try
            {
                packetsRaw = MissionPlannerReflector.GetMemberValue(port.MAV, "packets");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Cannot read MAVLink packet buffer: " + ex.Message);
            }

            var packetArray = packetsRaw as Array;
            var msgIdx = (int)(uint)msgId;

            if (packetArray == null || msgIdx < 0 || msgIdx >= packetArray.Length)
            {
                return MavLastNotFound(typeName);
            }

            var lastPacket = packetArray.GetValue(msgIdx);
            if (lastPacket == null)
            {
                return MavLastNotFound(typeName);
            }

            object structData = null;
            try
            {
                structData = MissionPlannerReflector.GetMemberValue(lastPacket, "data");
            }
            catch
            {
                // data unavailable — return empty fields
            }

            return new Dictionary<string, object>
            {
                { "type", typeName.ToUpperInvariant() },
                { "fields", MavStructToDict(structData) },
                { "found", true },
                { "timestampUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }
            };
        }

        private static Dictionary<string, object> MavLastNotFound(string typeName)
        {
            return new Dictionary<string, object>
            {
                { "type", typeName.ToUpperInvariant() },
                { "fields", new Dictionary<string, object>() },
                { "found", false }
            };
        }

        private static Dictionary<string, object> MavStructToDict(object structData)
        {
            var result = new Dictionary<string, object>();
            if (structData == null)
            {
                return result;
            }

            foreach (var fi in structData.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value;
                try
                {
                    value = fi.GetValue(structData);
                }
                catch
                {
                    continue;
                }

                if (value == null)
                {
                    continue;
                }

                result[fi.Name] = value is Enum ? value.ToString() : value;
            }

            return result;
        }

        private static Dictionary<string, object> ExtractMavLinkFields(Dictionary<string, object> payload)
        {
            object raw;
            if (!payload.TryGetValue("fields", out raw) || raw == null)
            {
                return new Dictionary<string, object>();
            }

            var dict = raw as Dictionary<string, object>;
            return dict ?? new Dictionary<string, object>();
        }

        private static bool IsAllowedAction(MAV_CMD cmd)
        {
            switch (cmd)
            {
                case MAV_CMD.DO_SET_HOME:
                case MAV_CMD.DO_SET_SERVO:
                case MAV_CMD.DO_REPEAT_SERVO:
                case MAV_CMD.DO_REPEAT_RELAY:
                case MAV_CMD.DO_DIGICAM_CONTROL:
                case MAV_CMD.MISSION_START:
                case MAV_CMD.DO_TRIGGER_CONTROL:
                case MAV_CMD.DO_SET_RELAY:
                case MAV_CMD.PREFLIGHT_REBOOT_SHUTDOWN:
                    return true;
                default:
                    return false;
            }
        }

        private static Dictionary<string, object> WpToDictionary(int index, Locationwp wp)
        {
            return new Dictionary<string, object>
            {
                { "index", index },
                { "command", ((MAV_CMD)wp.id).ToString() },
                { "commandId", wp.id },
                { "frame", wp.frame },
                { "p1", wp.p1 },
                { "p2", wp.p2 },
                { "p3", wp.p3 },
                { "p4", wp.p4 },
                { "lat", wp.lat },
                { "lng", wp.lng },
                { "alt", wp.alt }
            };
        }

        private static Locationwp DictionaryToWp(Dictionary<string, object> input)
        {
            var wp = new Locationwp();
            var commandName = RpcPayload.GetString(input, "command");
            var commandId = RpcPayload.GetInt(input, "commandId", (int)MAV_CMD.WAYPOINT);
            ushort id;

            if (!string.IsNullOrWhiteSpace(commandName))
            {
                MAV_CMD cmd;
                if (!Enum.TryParse(commandName, true, out cmd))
                {
                    throw new ArgumentException($"Unknown waypoint command '{commandName}'.");
                }

                id = (ushort)cmd;
            }
            else
            {
                id = (ushort)commandId;
            }

            wp.id = id;
            wp.frame = (byte)RpcPayload.GetInt(input, "frame", (int)MAV_FRAME.GLOBAL_RELATIVE_ALT);
            wp.p1 = (float)RpcPayload.GetDouble(input, "p1");
            wp.p2 = (float)RpcPayload.GetDouble(input, "p2");
            wp.p3 = (float)RpcPayload.GetDouble(input, "p3");
            wp.p4 = (float)RpcPayload.GetDouble(input, "p4");
            wp.lat = RpcPayload.GetDouble(input, "lat");
            wp.lng = RpcPayload.GetDouble(input, "lng");
            wp.alt = (float)RpcPayload.GetDouble(input, "alt");
            return wp;
        }
    }
}
