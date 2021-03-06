﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using TupacAmaru.Yacep.Exceptions;

namespace TupacAmaru.Yacep.Utils
{
    public static class ReflectionHelper
    {
        private static readonly ConcurrentDictionary<MethodInfo, Func<object, object[], object>> methodCache
            = new ConcurrentDictionary<MethodInfo, Func<object, object[], object>>();
        private static readonly ConcurrentDictionary<FieldInfo, Func<object, object>> fieldReaderCache
            = new ConcurrentDictionary<FieldInfo, Func<object, object>>();
        private static readonly ConcurrentDictionary<PropertyInfo, Func<object, object>> getterCache
            = new ConcurrentDictionary<PropertyInfo, Func<object, object>>();
        private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<Type, Func<object, object, object>>> indexerCache
            = new ConcurrentDictionary<Type, ConcurrentDictionary<Type, Func<object, object, object>>>();
        private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, Func<object, object>>> memberCache
            = new ConcurrentDictionary<Type, ConcurrentDictionary<string, Func<object, object>>>();

        private static Func<object, object[], object> BuildFunction(MethodInfo methodInfo)
        {
            Func<object, object[], object> callable;
            var instance = Expression.Parameter(typeof(object), "instance");
            var parameter = Expression.Parameter(typeof(object[]), "parameters");
            var args = methodInfo.GetParameters().Select((p, i) =>
            {
                var conditional = Expression.Condition(
                   Expression.IsTrue(Expression.And(Expression.NotEqual(parameter, Expression.Constant(null)),
                      Expression.GreaterThan(Expression.ArrayLength(parameter), Expression.Constant(i)))),
                     Expression.Convert(Expression.ArrayIndex(parameter, Expression.Constant(i)), p.ParameterType),
                    Expression.Default(p.ParameterType));
                return conditional;
            });
            if (methodInfo.ReturnType == typeof(void))
            {
                var type = methodInfo.IsStatic ? null : methodInfo.DeclaringType;
                var call = type == null ? Expression.Call(methodInfo, args) : Expression.Call(Expression.Convert(instance, type), methodInfo, args);
                var block = Expression.Block(call, Expression.Constant(null));
                var lambda = Expression.Lambda<Func<object, object[], object>>(block, "CallMethod", new[] { instance, parameter });
                callable = lambda.Compile();
            }
            else
            {
                var type = methodInfo.IsStatic ? null : methodInfo.DeclaringType;
                Expression getValue = type == null ? Expression.Call(methodInfo, args) : Expression.Call(Expression.Convert(instance, type), methodInfo, args);
                if (methodInfo.ReturnType.IsValueType)
                    getValue = Expression.Convert(getValue, typeof(object));
                var lambda = Expression.Lambda<Func<object, object[], object>>(getValue, "CallMethod", new[] { instance, parameter });
                callable = lambda.Compile();
            }
            return callable;
        }
        public static Func<object, object[], object> AsFunction(this MethodInfo methodInfo, bool addToCache = true)
            => addToCache ? methodCache.GetOrAdd(methodInfo, BuildFunction) : BuildFunction(methodInfo);
        public static Func<object, object[], object> GetFunction(this Type type, string methodName, bool addToCache = true)
        {
            var methodInfo = type.GetMethod(methodName);
            if (methodInfo == null) throw new MethodNotFoundException(type, methodName);
            return AsFunction(methodInfo, addToCache);
        }

        private static Func<object, object> BuildFieldReader(FieldInfo field)
        {
            var type = field.IsStatic ? null : field.DeclaringType;
            var obj = Expression.Parameter(typeof(object), "obj");
            Expression getValue = Expression.Field(type == null ? null : Expression.Convert(obj, type), field);
            var fieldType = field.FieldType;
            if (fieldType.IsValueType)
            {
                getValue = Expression.Convert(getValue, typeof(object));
                return Expression.Lambda<Func<object, object>>(getValue, "ReadObjectField", new[] { obj }).Compile();
            }
            var readObjectField = Expression.Lambda<Func<object, object>>(getValue, "ReadObjectField", new[] { obj }).Compile();
            return readObjectField;
        }
        public static Func<object, object> AsReader(this FieldInfo fieldInfo, bool addToCache = true)
            => addToCache ? fieldReaderCache.GetOrAdd(fieldInfo, BuildFieldReader) : BuildFieldReader(fieldInfo);
        public static Func<object, object> GetFieldReader(this Type type, string fieldName, bool addToCache = true)
        {
            var fieldInfo = type.GetField(fieldName);
            if (fieldInfo == null) throw new FieldNotFoundException(type, fieldName);
            return AsReader(fieldInfo, addToCache);
        }

        private static Func<object, object> BuildPropertyGetter(PropertyInfo propertyInfo)
        {
            var obj = Expression.Parameter(typeof(object), "obj");
            var method = propertyInfo.GetGetMethod();
            if (method == null) throw new PropertyNotFoundException(propertyInfo.DeclaringType, propertyInfo.Name);
            var type = method.IsStatic ? null : method.DeclaringType;
            Expression getValue = type == null ? Expression.Call(method) : Expression.Call(Expression.Convert(obj, type), method);
            var propertyType = propertyInfo.PropertyType;
            if (propertyType.IsValueType)
            {
                getValue = Expression.Convert(getValue, typeof(object));
                return Expression.Lambda<Func<object, object>>(getValue, "GetObjectProperty", new[] { obj }).Compile();
            }
            var getObjectProperty = Expression.Lambda<Func<object, object>>(getValue, "GetObjectProperty", new[] { obj }).Compile();
            return getObjectProperty;
        }
        public static Func<object, object> AsGetter(this PropertyInfo propertyInfo, bool addToCache = true)
            => addToCache ? getterCache.GetOrAdd(propertyInfo, BuildPropertyGetter) : BuildPropertyGetter(propertyInfo);
        public static Func<object, object> GetPropertyGetter(this Type type, string propertyName, bool addToCache = true)
        {
            var propertyInfo = type.GetProperty(propertyName);
            if (propertyInfo == null || !propertyInfo.CanRead) throw new PropertyNotFoundException(type, propertyName);
            return AsGetter(propertyInfo, addToCache);
        }

        private static Func<object, object, object> BuildIndexer(Type collectionType, Type indexerType, MethodInfo method)
        {
            var obj = Expression.Parameter(typeof(object), "collection");
            var value = Expression.Parameter(typeof(object), "indexerValue");
            Expression getValue = Expression.Call(Expression.Convert(obj, collectionType), method, Expression.Convert(value, indexerType));
            var valueType = method.ReturnType;
            if (valueType.IsValueType)
            {
                getValue = Expression.Convert(getValue, typeof(object));
                return Expression.Lambda<Func<object, object, object>>(getValue, "GetValueReader", new[] { obj, value }).Compile();
            }
            var getIndexerValue = Expression.Lambda<Func<object, object, object>>(getValue, "GetValueReader", new[] { obj, value }).Compile();
            return getIndexerValue;
        }
        private static Func<object, object, object> BuildIndexer(Type collectionType, Type indexerType)
        {
            var method = collectionType.GetMethod("Get", new[] { indexerType }) ?? collectionType.GetMethod("get_Item", new[] { indexerType });
            if (method == null) throw new UnsupportedObjectIndexerException(collectionType, indexerType);
            return BuildIndexer(collectionType, indexerType, method);
        }
        public static Func<object, object, object> CreateIndexer(this Type collectionType, Type indexerType, bool addToCache = true)
            => addToCache ? indexerCache
                .GetOrAdd(collectionType, t => new ConcurrentDictionary<Type, Func<object, object, object>>())
                .GetOrAdd(indexerType, m => BuildIndexer(collectionType, m))
                : BuildIndexer(collectionType, indexerType);

        private static Func<object, object> BuildMemberAccessor(Type type, string memberName)
        {
            var propertyInfo = type.GetProperty(memberName);
            if (propertyInfo != null && propertyInfo.CanRead)
            {
                var getter = propertyInfo.GetGetMethod();
                if (getter != null) return BuildPropertyGetter(propertyInfo);
            }

            var fieldInfo = type.GetField(memberName);
            if (fieldInfo != null) return BuildFieldReader(fieldInfo);

            var methodInfo = type.GetMethod(memberName);
            if (methodInfo != null)
            {
                var callable = BuildFunction(methodInfo);
                return obj => new Func<object[], object>(arguments => callable(obj, arguments));
            }
            var method = type.GetMethod("get_Item", new[] { typeof(string) }) ?? type.GetMethod("Get", new[] { typeof(string) }) ??
                         type.GetInterfaces().FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                                                                  && x.GetGenericArguments()[0] == typeof(string))?.GetMethod("get_Item");
            if (method != null)
            {
                var indexer = BuildIndexer(type, typeof(string), method);
                return obj => indexer(obj, memberName);
            }
            throw new MemberNotFoundException(type, memberName);
        }
        public static Func<object, object> GetValueReader(this Type type, string memberName)
            => memberCache.GetOrAdd(type, t => new ConcurrentDictionary<string, Func<object, object>>())
                .GetOrAdd(memberName, m => BuildMemberAccessor(type, m));
        public static Func<object, object> GetValueReaderWithNoCache(this Type type, string memberName)
            => BuildMemberAccessor(type, memberName);
    }
}

