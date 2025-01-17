﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using YantraJS.Core.Core.Storage;
using YantraJS.Core.Storage;
using YantraJS.ExpHelper;
using YantraJS.LinqExpressions;

using Exp = YantraJS.Expressions.YExpression;
using Expression = YantraJS.Expressions.YExpression;
using ParameterExpression = YantraJS.Expressions.YParameterExpression;
using LambdaExpression = YantraJS.Expressions.YLambdaExpression;
using LabelTarget = YantraJS.Expressions.YLabelTarget;
using SwitchCase = YantraJS.Expressions.YSwitchCaseExpression;
using GotoExpression = YantraJS.Expressions.YGoToExpression;
using TryExpression = YantraJS.Expressions.YTryCatchFinallyExpression;
using YantraJS.Runtime;

namespace YantraJS.Core.Clr
{
    public class JSNameAttribute: Attribute
    {
        public readonly string Name;

        public JSNameAttribute(string name)
        {
            this.Name = name;
        }
    }


    /// <summary>
    /// We might improve statup time by moving reflection code (setting up methods/properties) to proxy.
    /// </summary>
    public class ClrType : JSFunction
    {

        private static ConcurrentUInt32Map<ClrType> cachedTypes = ConcurrentUInt32Map<ClrType>.Create();

        internal class ClrPrototype: JSObject
        {
            internal Func<object, uint, JSValue> GetElementAt;

            internal Func<object, uint, object, JSValue> SetElementAt;

        }


        public static ClrType From(Type type)
        {
            // need to create base type first...
            ClrType baseType = null;
            if(type.BaseType != null && type.BaseType != typeof(object))
            {
                baseType = From(type.BaseType);
            }
            var key = ConcurrentTypeCache.GetOrCreate(type);
            return cachedTypes.GetOrCreate(key, () => new ClrType(type, baseType));
        }


        public readonly Type Type;

        (ConstructorInfo method, ParameterInfo[] parameters)[] constructorCache;

        public override bool ConvertTo(Type type, out object value)
        {
            if(type == typeof(Type))
            {
                value = this.Type;
                return true;
            }
            return base.ConvertTo(type, out value);
        }

        internal static void Generate(JSObject target, Type type, bool isStatic)
        {
            if (type.IsGenericTypeDefinition)
                return;

            Func<object, uint, JSValue> indexGetter = null;
            Func<object, uint, object, JSValue> indexSetter = null; ;


            var flags = isStatic
                ? BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Static
                : BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance;

            var declaredFields = type.GetTypeInfo().DeclaredFields.Where(x => x.IsStatic == isStatic && x.IsPublic);

            foreach(var field in declaredFields)
            {
                var name = field.Name.ToCamelCase();
                JSFunction getter = GenerateFieldGetter(field);
                JSFunction setter = null;
                if (!(field.IsInitOnly || field.IsLiteral))
                {
                    // you can only read...
                    setter = GenerateFieldSetter(field);
                }
                target.FastAddProperty(name, getter, setter, JSPropertyAttributes.EnumerableConfigurableProperty);
            }

            var declaredProperties = isStatic
                ? type.GetProperties(flags)
                : type.GetTypeInfo()
                    .DeclaredProperties
                    .Where(x => 
                        x.GetMethod?.IsStatic == isStatic 
                        || x.SetMethod?.IsStatic == isStatic).ToArray();


            foreach (var property in declaredProperties
                .GroupBy(x => x.Name)) {
                // only indexer property can have more items...
                var list = property.ToList();
                if (list.Count > 1)
                {
                    throw new NotImplementedException();
                } else
                {
                    var f = property.First();
                    if (f.PropertyType.IsGenericTypeDefinition)
                        continue;

                    KeyString name = f.GetCustomAttribute<JSNameAttribute>()?.Name ?? f.Name.ToCamelCase();

                    var fgm = f.GetMethod;
                    var fsm = f.SetMethod;
                    if (fgm?.GetParameters().Length > 0)
                    {
                        // it is an index property...
                        name = "index";
                    }

                    if (f.GetMethod?.GetParameters().Length > 0)
                    {
                        var ip = fgm.GetParameters()[0];
                        if (ip.ParameterType == typeof(int))
                        {
                            indexGetter = GenerateIndexedGetter(f);
                            indexSetter = GenerateIndexedSetter(f);
                        } else
                        {
                            if (indexGetter != null)
                                continue;
                            indexGetter = GenerateIndexedGetter(f);
                            indexSetter = GenerateIndexedSetter(f);
                        }
                    } else
                    {
                        JSFunction getter = f.CanRead
                            ? f.GeneratePropertyGetter()
                            : null;
                        JSFunction setter = f.CanWrite
                            ? f.GeneratePropertySetter()
                            : null;

                        if (getter != null || setter != null)
                        {                            
                            target.FastAddProperty(name, getter, setter, JSPropertyAttributes.EnumerableConfigurableProperty);
                        }
                    }

                }
            }

            foreach (var methods in type.GetMethods(flags)
                .Where(x => !x.IsSpecialName)
                .GroupBy(x => x.Name)) {
                var name = methods.Key.ToCamelCase();
                var all = methods.ToPairs();
                var jsMethod = all.FirstOrDefault(x => 
                    x.parameters?.Length == 1 
                    && typeof(JSValue).IsAssignableFrom(x.method.ReturnType)
                    && x.parameters[0].ParameterType == typeof(Arguments).MakeByRefType());
                if (jsMethod.method != null)
                {
                    // call directly...
                    // do not worry about @this... 

                    //if (isStatic)
                    //{
                    //    var methodDelegate = (JSFunctionDelegate)jsMethod.method.CreateDelegate(typeof(JSFunctionDelegate));
                    //    target.DefineProperty(name,
                    //        JSProperty.Function(ToStaticDelegate(methodDelegate));
                    //    continue;
                    //}

                    target.FastAddValue(name,
                        ToInstanceDelegate(jsMethod.method), JSPropertyAttributes.EnumerableConfigurableValue);


                    continue;
                }
                target.FastAddValue(name, isStatic
                    ? new JSFunction((in Arguments a) => {
                        return StaticInvoke(name, all, a);
                    }, name)
                    : new JSFunction((in Arguments a) => {
                        return Invoke(name, type, all, a);
                        }, name)
                    , JSPropertyAttributes.EnumerableConfigurableValue);
            }

            if (isStatic)
                return;
            var clrPrototype = target as ClrPrototype;

            if (indexGetter != null)
                clrPrototype.GetElementAt = indexGetter;
            if (indexSetter != null)
                clrPrototype.SetElementAt = indexSetter;
        }

        private static JSFunction ToInstanceDelegate(MethodInfo method)
        {
            var args = Expression.Parameter(typeof(Arguments).MakeByRefType());
            var target = Expression.Parameter(method.DeclaringType);
            var convert = method.IsStatic
                ? null
                : JSValueBuilder.Coalesce(ArgumentsBuilder.This(args), method.DeclaringType, target, method.Name);

            var body = Expression.Block(target.AsSequence(),
                JSExceptionBuilder.Wrap(ClrProxyBuilder.Marshal(
                    Expression.Call(
                        convert, method, args))));
            var name = method.Name.ToCamelCase();
            var d = Expression.Lambda<JSFunctionDelegate>(method.Name, body, args).Compile();
            return new JSFunction(d, name);
        }

        private static JSValue Invoke(in KeyString name, Type type, (MethodInfo method, ParameterInfo[] parameters)[] methods, in Arguments a)
        {
            if (!a.This.ConvertTo(type, out var target))
                throw JSContext.Current.NewTypeError($"{type.Name}.prototype.{name} called with object not of type {type.Name}");
            try
            {
                var (method, args) = methods.Match(a, name);
                return ClrProxy.Marshal(method.Invoke(target, args));
            } catch (Exception ex)
            {
                throw JSException.From(ex);
            }
        }

        private static JSValue StaticInvoke(in KeyString name, (MethodInfo method, ParameterInfo[] parameters)[] methods, in Arguments a)
        {
            try
            {
                var (method, args) = methods.Match(a, name);
                return ClrProxy.Marshal(method.Invoke(null, args));
            }catch (Exception ex)
            {
                throw JSException.From(ex);
            }
        }

        private static JSFunction GenerateFieldGetter(FieldInfo field)
        {
            var args = Expression.Parameter(typeof(Arguments).MakeByRefType());
            Expression convertedThis = field.IsStatic
                ? null
                : JSValueBuilder.ForceConvert(ArgumentsBuilder.This(args), field.DeclaringType);
            var body = 
                ClrProxyBuilder.Marshal(
                    Expression.Field(
                        convertedThis, field));
            var name = $"get {field.Name.ToCamelCase()}";
            var lambda = Expression.Lambda<JSFunctionDelegate>(name, body, args);
            return new JSFunction(lambda.Compile(), name);

        }

        private static JSFunction GenerateFieldSetter(FieldInfo field)
        {
            var args = Expression.Parameter(typeof(Arguments).MakeByRefType());
            var a1 = ArgumentsBuilder.Get1(args);
            var convert = field.IsStatic
                ? null
                : JSValueBuilder.ForceConvert(ArgumentsBuilder.This(args), field.DeclaringType);

            var clrArg1 = JSValueBuilder.ForceConvert(a1, field.FieldType);


            var fieldExp = Expression.Field(convert, field);

            // todo
            // not working for `char`
            var assign = Expression.Assign(fieldExp, clrArg1).ToJSValue();

            var body = assign;
            var name = $"set {field.Name.ToCamelCase()}";
            var lambda = Expression.Lambda<JSFunctionDelegate>(name, body, args);
            return new JSFunction(lambda.Compile(), name);
        }

        private static Func<object,uint,JSValue> GenerateIndexedGetter(PropertyInfo property)
        {
            var @this = Expression.Parameter(typeof(object));
            var index = Expression.Parameter(typeof(uint));
            var indexParameter = property.GetMethod.GetParameters()[0];
            Expression indexAccess = index.Type != indexParameter.ParameterType
                ? Expression.Convert(index, indexParameter.ParameterType)
                : index as Expression;
            Expression indexExpression;
            Expression convertThis = Expression.TypeAs(@this, property.DeclaringType);
            if (property.DeclaringType.IsArray)
            {
                // this is direct array.. cast and get.. 
                indexExpression = Expression.ArrayIndex(convertThis, indexAccess);
            } else
            {
                indexExpression = Expression.MakeIndex(convertThis, property, new Expression[] { indexAccess });
            }
            Expression body = JSExceptionBuilder.Wrap(ClrProxyBuilder.Marshal(indexExpression));
            var lambda = Expression.Lambda<Func<object,uint,JSValue>>($"set {property.Name}", body, @this, index);
            return lambda.Compile();
        }

        private static Func<object, uint, object, JSValue> GenerateIndexedSetter(PropertyInfo property)
        {
            if (!property.CanWrite)
                return null;
            
            var type = property.DeclaringType;
            var elementType = type.GetElementTypeOrGeneric() ?? property.PropertyType;

            var @this = Expression.Parameter(typeof(object));
            var index = Expression.Parameter(typeof(uint));
            var value = Expression.Parameter(typeof(object));
            var indexParameter = property.SetMethod.GetParameters()[0];
            Expression indexAccess = index.Type != indexParameter.ParameterType
                ? Expression.Convert(index, indexParameter.ParameterType)
                : index as Expression;
            Expression indexExpression;
            Expression convertThis = Expression.TypeAs(@this, property.DeclaringType);
            if (property.DeclaringType.IsArray)
            {
                // this is direct array.. cast and get.. 
                indexExpression = Expression.ArrayIndex(convertThis, indexAccess);
            }
            else
            {
                indexExpression = Expression.MakeIndex(convertThis, property, new Expression[] { indexAccess });
            }


            Expression body = Expression.Block( 
                JSExceptionBuilder.Wrap( 
                    Expression.Assign(indexExpression , Expression.TypeAs(value, elementType)).ToJSValue()));
            var lambda = Expression.Lambda<Func<object, uint, object, JSValue>>("get " + property.Name, body, @this, index, value);
            return lambda.Compile();
        }

        private ClrType(
            Type type, 
            ClrType baseType = null) : 
            base(
                type.Name, 
                $"function {type.Name}() {{ [clr-native] }}", 
                new ClrPrototype())
        {
            this.f = Create;
            this.Type = type;

            Generate(this, type, true);

            Generate(this.prototype, type, false);

            this.constructorCache = type.GetConstructors()
                .Select(c => (method: c, parameters: c.GetParameters()))
                .OrderByDescending(x => x.parameters.RequiredCount())
                .ToArray();

            foreach (var (method, parameters) in constructorCache)
            {
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Arguments).MakeByRefType())
                {
                    var cx = method as ConstructorInfo;
                    f = (in Arguments a) => Create2(cx, a);
                }
            }

            if (type.IsGenericTypeDefinition)
            {
                // make generic type..

                this.FastAddValue(
                    "makeGenericType",
                    new JSFunction(MakeGenericType, "makeGenericType"), JSPropertyAttributes.EnumerableConfigurableValue);
            }
            else
            {
                // getMethod... name and types...
                this.FastAddValue("getMethod",
                    new JSFunction(GetMethod, "getMethod"), JSPropertyAttributes.EnumerableConfigurableValue);
                this.FastAddValue("getConstructor",
                    new JSFunction(GetConstructor, "getConstructor"),
                    JSPropertyAttributes.EnumerableConfigurableValue);
            }

            if(baseType != null)
            {
                BasePrototypeObject = baseType;
                prototype.BasePrototypeObject = baseType.prototype;

                // set indexer... for int/uint
                
                if (prototype is ClrPrototype p)
                {
                    if (baseType.prototype is ClrPrototype bp)
                    {
                        if (p.GetElementAt == null)
                        {
                            // add converter here...
                            p.GetElementAt = (a1, a2) =>
                            {
                                return bp.GetElementAt(a1, a2);
                            };
                            var et = type.GetElementTypeOrGeneric();
                            if (et != null)
                            {
                                p.SetElementAt = (a1, a2, a3) =>
                                {
                                    if (a3 is JSValue j3)
                                    {
                                        a3 = j3.ForceConvert(et);
                                    }
                                    return bp.SetElementAt(a1, a2, a3);
                                };

                            }
                            else
                            {
                                p.SetElementAt = (a1, a2, a3) =>
                                {
                                    return bp.SetElementAt(a1, a2, a3);
                                };
                            }
                        }
                    } else
                    {
                        var old = p.SetElementAt;
                        if (old != null)
                        {
                            var et = type.GetElementTypeOrGeneric();
                            if (et != null)
                            {
                                p.SetElementAt = (a1, a2, a3) => {
                                    if (a3 is JSValue j3)
                                        a3 = j3.ForceConvert(et);
                                    return old(a1, a2, a3);
                                };
                            }
                        }
                    }
                }
            }

        }

        private JSValue Create2(ConstructorInfo c, in Arguments a)
        {
            // improve later...
            return new ClrProxy(c.Invoke(new object[] { a }), prototype);
        }


        public JSValue Create(in Arguments a)
        {
            var (c, values) = constructorCache.Match(a, KeyStrings.constructor);
            return new ClrProxy(c.Invoke(values), prototype);
        }

        public JSValue GetMethod(in Arguments a)
        {
            var a1 = a.Get1();
            if (a1.IsNullOrUndefined)
                throw JSContext.Current.NewTypeError($"Name is required");
            var name = a1.ToString();
            MethodInfo method;
            Type[] types = null;
            var flags = BindingFlags.IgnoreCase 
                | BindingFlags.Default 
                | BindingFlags.Public 
                | BindingFlags.FlattenHierarchy
                | BindingFlags.Instance
                | BindingFlags.Static;
            if (a.Length == 1)
            {
                method = Type.GetMethod(name, flags);
            } else {
                types = new Type[a.Length - 1];
                for (int i = 1; i < a.Length; i++)
                {
                    var v = a.GetAt(i);
                    types[i-1] = (Type)v.ForceConvert(typeof(Type));
                }
                method = Type.GetMethod(name, flags, null, types, null);
            }
            if (method == null)
                throw new JSException($"Method {name} not found on {Type.Name}");
            return new JSFunction(GenerateMethod(method), name, "native");
        }

        private JSFunctionDelegate GenerateMethod(MethodInfo m)
        {
            var args = Expression.Parameter(typeof(Arguments).MakeByRefType());
            var @this = ArgumentsBuilder.This(args);

            var convertedThis = m.IsStatic
                ? null
                : JSValueBuilder.ForceConvert(@this, m.DeclaringType);
            var parameters = new List<Expression>();
            var pList = m.GetParameters();
            for (int i = 0; i < pList.Length; i++)
            {
                var ai = ArgumentsBuilder.GetAt(args, i);
                var pi = pList[i];
                var defValue = pi.HasDefaultValue
                    ? Expression.Constant((object)pi.DefaultValue, typeof(object))
                    : (pi.ParameterType.IsValueType
                        ? Expression.Constant((object)Activator.CreateInstance(pi.ParameterType),typeof(object))
                        : Expression.Constant(null, pi.ParameterType));
                parameters.Add(JSValueBuilder.Convert(ai, pi.ParameterType, defValue));
            }
            var call = Expression.Call(convertedThis, m, parameters);
            var marshal = ClrProxyBuilder.Marshal(call);
            var wrapTryCatch = JSExceptionBuilder.Wrap(marshal);

            var lambda = Expression.Lambda<JSFunctionDelegate>(m.Name, wrapTryCatch, args);
            return lambda.Compile();
        }

        public JSValue GetConstructor(in Arguments a)
        {
            ConstructorInfo method;
            Type[] types = null;
            if (a.Length == 0)
            {
                method = Type.GetConstructor(new Type[] { });
            }
            else
            {
                types = new Type[a.Length];
                for (int i = 0; i < a.Length; i++)
                {
                    var v = a.GetAt(i);
                    types[i] = (Type)v.ForceConvert(typeof(Type));
                }
                method = Type.GetConstructor(types);
            }
            if (method == null) 
                throw new JSException($"Constructor({string.Join(",", types.Select(x => x.Name))}) not found on {Type.Name}");
            return new JSFunction(GenerateConstructor(method, this.prototype), this);
        }

        public delegate object JSValueFactory(in Arguments a);

        public static JSFunctionDelegate JSValueFactoryDelegate(JSValueFactory fx, JSObject prototype)
        {
            JSValue Factory(in Arguments a)
            {
                var r = fx(in a);
                return new ClrProxy(r, prototype);
            }
            return Factory;
        }

        private JSFunctionDelegate GenerateConstructor(ConstructorInfo m, JSObject prototype)
        {
            var args = Expression.Parameter(typeof(Arguments).MakeByRefType());

            var parameters = new List<Expression>();
            var pList = m.GetParameters();
            for (int i = 0; i < pList.Length; i++)
            {
                var ai = ArgumentsBuilder.GetAt(args, i);
                var pi = pList[i];
                var defValue = pi.HasDefaultValue
                    ? Expression.Constant(pi.DefaultValue,typeof(object))
                    : (pi.ParameterType.IsValueType
                        ? Expression.Constant(Activator.CreateInstance(pi.ParameterType),typeof(object))
                        : Expression.Constant(null, pi.ParameterType));
                parameters.Add(JSValueBuilder.Convert(ai, pi.ParameterType, defValue));
            }
            var call = Expression.TypeAs( Expression.New(m, parameters), typeof(object));
            var lambda = Expression.Lambda<JSValueFactory>(m.DeclaringType.Name, call, args);
            var factory = lambda.Compile();
            return JSValueFactoryDelegate(factory, prototype);
        }


        public JSValue MakeGenericType(in Arguments a)
        {
            var types = new Type[a.Length];
            for (int i = 0; i < a.Length; i++)
            {
                var v = a.GetAt(i);
                types[i] = (Type)v.ForceConvert(typeof(Type));
            }
            return ClrType.From(Type.MakeGenericType(types));
        }

        

    }
}
