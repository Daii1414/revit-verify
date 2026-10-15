using System;
using System.Collections.Generic;
using System.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>Pure renumbering plan (no Revit API): deterministic order plus
    /// sequential numbers. Unit-testable core behind renumber_elements.</summary>
    internal static class RenumberPlanner
    {
        public sealed class Item
        {
            public long Id { get; set; }
            public double LevelMm { get; set; }
            public double XMm { get; set; }
            public double YMm { get; set; }
        }

        public static List<Tuple<long, string>> Plan(IEnumerable<Item> items, string prefix, int start, int step)
        {
            if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
            if (step < 1) throw new ArgumentOutOfRangeException(nameof(step));
            var ordered = items
                .OrderBy(i => i.LevelMm)
                .ThenBy(i => i.XMm)
                .ThenBy(i => i.YMm)
                .ThenBy(i => i.Id)
                .ToList();
            var result = new List<Tuple<long, string>>(ordered.Count);
            var number = start;
            foreach (var item in ordered)
            {
                result.Add(Tuple.Create(item.Id, (prefix ?? "") + number));
                number += step;
            }
            return result;
        }
    }
}
