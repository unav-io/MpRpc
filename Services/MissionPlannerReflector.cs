using System;
using System.Reflection;

namespace MpRpc.Services
{
    internal static class MissionPlannerReflector
    {
        public static object GetMemberValue(object target, string memberName)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var type = target.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var prop = type.GetProperty(memberName, flags);
            if (prop != null)
            {
                return prop.GetValue(target, null);
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                return field.GetValue(target);
            }

            throw new MissingMemberException(type.FullName, memberName);
        }

        public static object InvokeMethod(object target, string methodName, params object[] args)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return target.GetType().InvokeMember(
                methodName,
                BindingFlags.InvokeMethod | flags,
                null,
                target,
                args);
        }
    }
}
