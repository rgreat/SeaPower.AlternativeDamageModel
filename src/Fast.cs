using System;
using System.Reflection;
using System.Reflection.Emit;

namespace AlternativeDamageModel
{
    /// <summary>
    /// Field / property access of the game's objects without the boxing and the slow path of FieldInfo.GetValue: a small dynamic method per member.
    /// Any trouble (the member has another type, a value-type owner, a failure of the dynamic method) switches that accessor for good to plain
    /// reflection, which behaves exactly as before - so a game update can slow it down but never break it.
    /// </summary>
    public sealed class Fld<T>
    {
        readonly FieldInfo _f;
        Func<object, T> _get; Action<object, T> _set;
        bool _slowGet, _slowSet;

        public Fld(FieldInfo f)
        {
            _f = f;
            _slowGet = _slowSet = !Fits(f);
            if (_slowGet) return;
            try
            {
                var gm = new DynamicMethod("adm_get", typeof(T), new[] { typeof(object) }, typeof(Fld<T>).Module, true);
                var il = gm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, f.DeclaringType); il.Emit(OpCodes.Ldfld, f); il.Emit(OpCodes.Ret);
                _get = (Func<object, T>)gm.CreateDelegate(typeof(Func<object, T>));
                var sm = new DynamicMethod("adm_set", typeof(void), new[] { typeof(object), typeof(T) }, typeof(Fld<T>).Module, true);
                il = sm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, f.DeclaringType); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, f); il.Emit(OpCodes.Ret);
                _set = (Action<object, T>)sm.CreateDelegate(typeof(Action<object, T>));
            }
            catch { _slowGet = _slowSet = true; }
        }

        static bool Fits(FieldInfo f)
        {
            if (f == null || f.IsStatic || f.DeclaringType == null || f.DeclaringType.IsValueType || f.DeclaringType.IsGenericTypeDefinition) return false;
            var ft = f.FieldType;
            return ft == typeof(T) || (!ft.IsValueType && !typeof(T).IsValueType && typeof(T).IsAssignableFrom(ft));
        }

        public T Get(object o)
        {
            if (!_slowGet)
            {
                try { return _get(o); }
                catch { _slowGet = true; }
            }
            object v = _f.GetValue(o);
            return v is T ? (T)v : default(T);
        }

        public void Set(object o, T v)
        {
            if (!_slowSet)
            {
                try { _set(o, v); return; }
                catch { _slowSet = true; }
            }
            _f.SetValue(o, v);
        }
    }

    /// <summary>An instance property getter (no index), same rules as Fld.</summary>
    public sealed class Prop<T>
    {
        readonly PropertyInfo _p;
        Func<object, T> _get;
        bool _slow;

        public Prop(PropertyInfo p)
        {
            _p = p;
            var m = p == null ? null : p.GetGetMethod(true);
            _slow = m == null || m.IsStatic || p.GetIndexParameters().Length != 0 || p.DeclaringType == null || p.DeclaringType.IsValueType || p.DeclaringType.IsGenericTypeDefinition
                || !(p.PropertyType == typeof(T) || (!p.PropertyType.IsValueType && !typeof(T).IsValueType && typeof(T).IsAssignableFrom(p.PropertyType)));
            if (_slow) return;
            try
            {
                var gm = new DynamicMethod("adm_pget", typeof(T), new[] { typeof(object) }, typeof(Prop<T>).Module, true);
                var il = gm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, p.DeclaringType); il.Emit(OpCodes.Callvirt, m); il.Emit(OpCodes.Ret);
                _get = (Func<object, T>)gm.CreateDelegate(typeof(Func<object, T>));
            }
            catch { _slow = true; }
        }

        public T Get(object o)
        {
            if (!_slow)
            {
                try { return _get(o); }
                catch { _slow = true; }
            }
            object v = _p.GetValue(o, null);
            return v is T ? (T)v : default(T);
        }
    }

    /// <summary>A static numeric property (float or double) read as double; plain reflection when it is neither.</summary>
    public sealed class StaticNum
    {
        readonly PropertyInfo _p;
        Func<double> _d; Func<float> _f;

        public StaticNum(PropertyInfo p)
        {
            _p = p;
            var m = p == null ? null : p.GetGetMethod(true);
            if (m == null || !m.IsStatic) return;
            try
            {
                if (p.PropertyType == typeof(double)) _d = (Func<double>)Delegate.CreateDelegate(typeof(Func<double>), m);
                else if (p.PropertyType == typeof(float)) _f = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), m);
            }
            catch { _d = null; _f = null; }
        }

        public double Get()
        {
            try
            {
                if (_d != null) return _d();
                if (_f != null) return _f();
            }
            catch { _d = null; _f = null; }
            return Convert.ToDouble(_p.GetValue(null, null), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
