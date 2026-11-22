using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace BatteryTray
{
    // csc из .NET Framework не умеет await для IAsyncOperation<T> напрямую, 
    // зовём WindowsRuntimeSystemExtensions.AsTask через рефлексию.
    internal static class WinRtAsync
    {
        private static MethodInfo _asTaskOp;
        private static readonly object Lock = new object();
        private static readonly Dictionary<Type, MethodInfo> MethodCache = new Dictionary<Type, MethodInfo>();

        private static MethodInfo GetMethod(Type t)
        {
            lock (Lock)
            {
                MethodInfo mi;
                if (MethodCache.TryGetValue(t, out mi)) return mi;
                if (_asTaskOp == null)
                {
                    Assembly asm = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                    Type ext = asm.GetType("System.WindowsRuntimeSystemExtensions");
                    if (ext == null) throw new InvalidOperationException("WindowsRuntimeSystemExtensions not found");
                    foreach (MethodInfo m in ext.GetMethods())
                    {
                        if (m.Name != "AsTask") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType.Name == "IAsyncOperation`1") { _asTaskOp = m; break; }
                    }
                    if (_asTaskOp == null) throw new InvalidOperationException("AsTask(IAsyncOperation<T>) not found");
                }
                mi = _asTaskOp.MakeGenericMethod(t);
                MethodCache[t] = mi;
                return mi;
            }
        }

        public static Task<T> AsTask<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            return (Task<T>)GetMethod(typeof(T)).Invoke(null, new object[] { op });
        }
    }
}
