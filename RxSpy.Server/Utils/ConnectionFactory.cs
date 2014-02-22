﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RxSpy.Observables;

namespace RxSpy.Utils
{
    public static class ConnectionFactory
    {
        readonly static ConcurrentDictionary<Type, Lazy<Func<object, OperatorInfo, object>>> _connectionFactoryCache =
            new ConcurrentDictionary<Type, Lazy<Func<object, OperatorInfo, object>>>();

        public static bool TryCreateConnection(Type type, object value, OperatorInfo operatorInfo, out object connectionObject)
        {
            var factory = _connectionFactoryCache.GetOrAdd(
                type,
                _ => new Lazy<Func<object, OperatorInfo, object>>(
                    () => CreateConnectionFactory(type),
                    LazyThreadSafetyMode.ExecutionAndPublication)
            );

            if (factory.Value == null)
            {
                connectionObject = null;
                return false;
            }

            connectionObject = factory.Value(value, operatorInfo);
            return true;
        }

        static Func<object, OperatorInfo, object> CreateConnectionFactory(Type pt)
        {
            if (IsGenericTypeDefinition(pt, typeof(IObservable<>)))
            {
                var signalType = pt.GetGenericArguments()[0];

                return (value, operatorInfo) => CreateObservableConnection(value, signalType, operatorInfo);
            }
            else if (pt.IsArray)
            {
                var observableType = pt.GetElementType();

                if (!IsGenericTypeDefinition(observableType, typeof(IObservable<>)))
                {
                    return null;
                }

                var signalType = observableType.GetGenericArguments()[0];

                return (value, operatorInfo) =>
                {
                    var argArray = (Array)value;
                    var newArray = Array.CreateInstance(observableType, argArray.Length);

                    for (int i = 0; i < argArray.Length; i++)
                    {
                        newArray.SetValue(CreateObservableConnection(argArray.GetValue(i), signalType, operatorInfo), i);
                    }

                    return newArray;
                };
            }
            else if (IsGenericTypeDefinition(pt, typeof(IEnumerable<>)) &&
                IsGenericTypeDefinition(pt.GetGenericArguments()[0], typeof(IObservable<>)))
            {
                var observableType = pt.GetGenericArguments()[0];
                var signalType = observableType.GetGenericArguments()[0];

                var enumerableConnectionType = typeof(DeferredOperatorConnectionEnumerable<>)
                        .MakeGenericType(observableType);

                return (value, operatorInfo) =>
                {
                    return Activator.CreateInstance(
                        enumerableConnectionType,
                        new object[] { 
                            value, 
                            new Func<object, object>(o => CreateObservableConnection(o, signalType, operatorInfo)) 
                        });
                };
            }

            return null;
        }

        static IConnection CreateObservableConnection(object source, Type signalType, OperatorInfo operatorInfo)
        {
            var operatorObservable = typeof(OperatorConnection<>).MakeGenericType(signalType);

            var instance = operatorObservable.GetConstructor(new[] { typeof(RxSpySession), typeof(IObservable<>).MakeGenericType(signalType), typeof(OperatorInfo) })
                .Invoke(new object[] { RxSpySession.Current, source, operatorInfo });

            return (IConnection)instance;
        }

        static bool IsGenericTypeDefinition(Type source, Type genericTypeComparand)
        {
            return source.IsGenericType && source.GetGenericTypeDefinition() == genericTypeComparand;
        }

        class DeferredOperatorConnectionEnumerable<T> : IEnumerable<T>
        {
            readonly IEnumerable<T> _source;
            readonly Func<object, object> _selector;

            public DeferredOperatorConnectionEnumerable(IEnumerable<T> source, Func<object, object> selector)
            {
                _source = source;
                _selector = selector;
            }

            public IEnumerator<T> GetEnumerator()
            {
                foreach (var item in _source)
                    yield return (T)_selector(item);
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }
    }
}
