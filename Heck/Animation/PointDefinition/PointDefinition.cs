﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ModestTree;

namespace Heck.Animation;

public interface IPointDefinition
{
    public int Count { get; }

    public bool HasBaseProvider { get; }
}

public abstract class PointDefinition<T> : IPointDefinition
    where T : struct
{
    private readonly List<IPointData> _points = [];

    [SuppressMessage("ReSharper", "VirtualMemberCallInConstructor", Justification = "No instance variables used.")]
    protected PointDefinition(IReadOnlyCollection<object> list)
    {
        IEnumerable<List<object>> points = list.FirstOrDefault() is List<object>
            ? list.Cast<List<object>>()
            : new[] { list.Append(0).ToList() };
        foreach (List<object> rawPoint in points)
        {
            Functions easing = Functions.easeLinear;
            Modifier<T>[]? modifiers = null;
            string[]? flags = null;
            IValues[]? values = null;
            foreach (IGrouping<GroupType, object> grouping in Group(rawPoint))
            {
                object[] groupList = grouping.ToArray();
                switch (grouping.Key)
                {
                    case GroupType.Value:
                        values = groupList.DeserializeValues();
                        break;

                    case GroupType.Flag:
                        flags = groupList.Cast<string>().ToArray();
                        string? easingString = flags.FirstOrDefault(n => n.StartsWith("ease"));
                        if (easingString != null)
                        {
                            easing = (Functions)Enum.Parse(typeof(Functions), easingString);
                        }

                        break;

                    case GroupType.Modifier:
                        modifiers = groupList.Cast<List<object>>().Select(DeserializeModifier).ToArray();
                        break;
                }
            }

            if (values == null)
            {
                throw new InvalidOperationException("No points found.");
            }

            _points.Add(CreatePointData(values, flags ?? [], modifiers ?? [], easing));
        }

        HasBaseProvider = _points.Any(n => n.HasBaseProvider);
    }

    private enum GroupType
    {
        Value,
        Flag,
        Modifier
    }

    protected interface IPointData
    {
        public Functions Easing { get; }

        public bool HasBaseProvider { get; }

        public T Point { get; }

        public float Time { get; }
    }

    public int Count => _points.Count;

    public bool HasBaseProvider { get; }

    public T Interpolate(float time)
    {
        return Interpolate(time, out _);
    }

    public T Interpolate(float time, out bool last)
    {
        last = false;
        if (Count == 0)
        {
            return default;
        }

        IPointData lastPoint = _points[Count - 1];
        if (lastPoint.Time <= time)
        {
            last = true;
            return lastPoint.Point;
        }

        IPointData firstPoint = _points[0];
        if (firstPoint.Time >= time)
        {
            return firstPoint.Point;
        }

        SearchIndex(time, out int l, out int r);
        IPointData pointL = _points[l];
        IPointData pointR = _points[r];

        float normalTime;
        float divisor = pointR.Time - pointL.Time;
        if (divisor != 0)
        {
            normalTime = (time - pointL.Time) / divisor;
        }
        else
        {
            normalTime = 0;
        }

        normalTime = Easings.Interpolate(normalTime, pointR.Easing);

        return InterpolatePoints(_points, l, r, normalTime);
    }

    public override string ToString()
    {
        return "{" +
               string.Join(
                   ", ",
                   _points.Select(
                       n =>
                       {
                           string result = n.ToString();
                           string added = $", {n.Time}" +
                                          (n.Easing != Functions.easeLinear ? ", " + n.Easing : string.Empty);
                           return result.Insert(result.Length - 1, added);
                       })) +
               "}";
    }

    protected abstract T InterpolatePoints(List<IPointData> points, int l, int r, float time);

    private protected abstract Modifier<T> CreateModifier(
        IValues[] values,
        Modifier<T>[] modifiers,
        Operation operation);

    private protected abstract IPointData CreatePointData(
        IValues[] values,
        string[] flags,
        Modifier<T>[] modifiers,
        Functions easing);

    private static IEnumerable<IGrouping<GroupType, object>> Group(IEnumerable<object> list)
    {
        return list.GroupBy(
            n =>
            {
                return n switch
                {
                    string s when !s.StartsWith("base") => GroupType.Flag,
                    List<object> => GroupType.Modifier,
                    _ => GroupType.Value
                };
            });
    }

    private Modifier<T> DeserializeModifier(List<object> list)
    {
        Modifier<T>[]? modifiers = null;
        Operation? operation = null;
        IValues[]? values = null;
        foreach (IGrouping<GroupType, object> grouping in Group(list))
        {
            object[] groupList = grouping.ToArray();
            switch (grouping.Key)
            {
                case GroupType.Value:
                    values = groupList.DeserializeValues();
                    break;

                case GroupType.Flag:
                    Assert.IsEqual(1, groupList.Length, "Modifier must have one operation");
                    operation = (Operation)Enum.Parse(typeof(Operation), (string)groupList.First());
                    break;

                case GroupType.Modifier:
                    modifiers = groupList.Cast<List<object>>().Select(DeserializeModifier).ToArray();
                    break;
            }
        }

        if (values == null)
        {
            throw new InvalidOperationException("No points found.");
        }

        if (operation == null)
        {
            throw new InvalidOperationException("No operation found.");
        }

        return CreateModifier(values, modifiers ?? [], operation.Value);
    }

    // Use binary search instead of linear search.
    private void SearchIndex(float time, out int l, out int r)
    {
        l = 0;
        r = Count;

        while (l < r - 1)
        {
            int m = (l + r) / 2;
            float pointTime = _points[m].Time;

            if (pointTime < time)
            {
                l = m;
            }
            else
            {
                r = m;
            }
        }
    }
}
