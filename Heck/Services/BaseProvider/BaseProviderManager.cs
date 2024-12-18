﻿using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using Zenject;

namespace Heck.BaseProvider;

internal interface IBaseProviderData;

internal class BaseProviderManager
{
    private readonly Dictionary<string, IBaseProviderData> _baseProviders = new();

    [UsedImplicitly]
    private BaseProviderManager(
        [Inject(Optional = true, Source = InjectSources.Local)]
        IEnumerable<IBaseProvider> baseProviders)
    {
        Instance = this;
        foreach (IBaseProvider baseProvider in baseProviders)
        {
            foreach (PropertyInfo propertyInfo in baseProvider.GetType().GetProperties(AccessTools.allDeclared))
            {
                string name = $"base{propertyInfo.Name}";
                IBaseProviderData data = (IBaseProviderData)Activator.CreateInstance(
                    typeof(BaseProviderData<>).MakeGenericType(propertyInfo.GetUnderlyingType()),
                    baseProvider,
                    propertyInfo.GetMethod);
                _baseProviders.Add(name, data);
            }
        }
    }

    // I couldnt think of a way to di this thing
    internal static BaseProviderManager Instance { get; private set; } = null!;

    internal BaseProviderData<T> GetProviderData<T>(string key)
    {
        return (BaseProviderData<T>)_baseProviders[key];
    }
}

internal class BaseProviderData<T>(IBaseProvider baseProvider, MethodInfo getter) : IBaseProviderData
{
    private readonly Func<T> _getter = (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), baseProvider, getter);

    internal T GetValue()
    {
        return _getter();
    }
}
