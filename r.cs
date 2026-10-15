using System;
using System.Reflection;

namespace HangingHammock
{
    internal static class R
    {
        internal const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static FieldInfo Field(Type t, string name)
        {
            for (var x = t; x != null; x = x.BaseType)
            {
                var f = x.GetField(name, F | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        public static object Get(object o, string name)
        {
            var f = o == null ? null : Field(o.GetType(), name);
            return f?.GetValue(o);
        }

        public static bool Set(object o, string name, object v)
        {
            var f = o == null ? null : Field(o.GetType(), name);
            if (f == null) { Plugin.Log.LogWarning($"Field '{name}' not found on {o?.GetType().Name}"); return false; }
            try
            {
                if (f.FieldType.IsEnum && v is string s) v = Enum.Parse(f.FieldType, s, true);
                else if (v != null && !f.FieldType.IsInstanceOfType(v)) v = Convert.ChangeType(v, f.FieldType);
                f.SetValue(o, v);
                return true;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"Set {name}: {e.Message}"); return false; }
        }

        public static object Call(object o, string name, params object[] args)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
                foreach (var m in t.GetMethods(F | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != name) continue;
                    var ps = m.GetParameters();
                    if (ps.Length != args.Length) continue;
                    bool ok = true;
                    for (int i = 0; i < ps.Length; i++)
                        if (args[i] != null && !ps[i].ParameterType.IsInstanceOfType(args[i])) { ok = false; break; }
                    if (ok) return m.Invoke(o, args);
                }
            Plugin.Log.LogWarning($"Method '{name}'({args.Length}) not found on {o.GetType().Name}");
            return null;
        }
    }
}