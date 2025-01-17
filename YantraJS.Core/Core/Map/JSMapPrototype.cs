﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using YantraJS.Extensions;

namespace YantraJS.Core
{
    public partial class JSMap
    {

        internal static class JSMapPrototype
        {
            [Constructor]
            public static JSValue Constructor(in Arguments a)
            {
                return new JSMap();
            }

            private static JSMap ToMap(JSValue t)
            {
                if (!(t is JSMap m))
                    throw JSContext.Current.NewTypeError($"Receiver {t} is not a map");
                return m;
            }


            [GetProperty("size")]
            public static JSValue GetSize(in Arguments a)
            {
                return new JSNumber(ToMap(a.This).entries.Count);
            }

            [Prototype("clear")]
            public static JSValue Clear(in Arguments a)
            {
                var m = ToMap(a.This);
                m.entries.Clear();
                m.cache = new Core.Storage.StringMap<LinkedListNode<(JSValue key, JSValue value)>>();
                return JSUndefined.Value;
            }

            [Prototype("delete")]
            public static JSValue Delete(in Arguments a)
            {
                var m = ToMap(a.This);
                var key = a.Get1().ToUniqueID();
                if(m.cache.TryRemove(key, out var e))
                {
                    m.entries.Remove(e);
                    return JSBoolean.True;
                }
                return JSBoolean.False;
            }


            [Prototype("entries")]
            public static JSValue Entries(in Arguments a)
            {
                return new JSArray(ToMap(a.This).entries.Select(x => new JSArray(x.key, x.value)));
            }


            [Prototype("forEach")]
            public static JSValue ForEach(in Arguments a)
            {
                var fx = a.Get1();
                if (!fx.IsFunction)
                    throw JSContext.Current.NewTypeError($"Function parameter expected");
                var m = ToMap(a.This);
                foreach (var e in m.entries)
                {
                    fx.InvokeFunction(new Arguments(a.This, e.value, e.key, m));
                }
                return JSUndefined.Value;
            }

            [Prototype("get")]
            public static JSValue Get(in Arguments a)
            {
                var first = a.Get1();
                var m = ToMap(a.This);
                var key = first.ToUniqueID();
                if (m.cache.TryGetValue(key, out var e))
                    return e.Value.value;
                return JSUndefined.Value;
            }

            [Prototype("has")]
            public static JSValue Has(in Arguments a)
            {
                var first = a.Get1();
                var m = ToMap(a.This);
                var key = first.ToUniqueID();
                if (m.cache.TryGetValue(key, out var _))
                    return JSBoolean.True;
                return JSBoolean.False;
            }

            [Prototype("keys")]
            public static JSValue Keys(in Arguments a)
            {
                return new JSArray(ToMap(a.This).entries.Select(x => x.key));
            }


            [Prototype("set")]
            public static JSValue Set(in Arguments a)
            {
                var m = ToMap(a.This);
                var (first, second) = a.Get2();
                var key = first.ToUniqueID();
                if(m.cache.TryGetValue(key, out var entry))
                {
                    entry.Value = (first, second);
                    return m;
                }
                var index = m.entries.Count;
                m.cache.Save(key, m.entries.AddLast((first, second)));
                return m;
            }

            [Prototype("values")]
            public static JSValue Values(in Arguments a)
            {
                return new JSArray(ToMap(a.This).entries.Select(x => x.value));
            }
        }
    }
}
