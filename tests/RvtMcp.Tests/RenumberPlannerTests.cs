using System.Collections.Generic;
using System.Linq;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public sealed class RenumberPlannerTests
    {
        [Fact]
        public void Orders_by_level_then_plan_position()
        {
            var items = new List<RenumberPlanner.Item>
            {
                new RenumberPlanner.Item { Id = 3, LevelMm = 4000, XMm = 0, YMm = 0 },
                new RenumberPlanner.Item { Id = 1, LevelMm = 0, XMm = 5000, YMm = 0 },
                new RenumberPlanner.Item { Id = 2, LevelMm = 0, XMm = 1000, YMm = 0 },
            };

            var plan = RenumberPlanner.Plan(items, "D-", 1, 1);

            Assert.Equal(new[] { 2L, 1L, 3L }, plan.Select(p => p.Item1).ToArray());
            Assert.Equal(new[] { "D-1", "D-2", "D-3" }, plan.Select(p => p.Item2).ToArray());
        }

        [Fact]
        public void Honors_start_step_and_empty_prefix()
        {
            var items = new List<RenumberPlanner.Item>
            {
                new RenumberPlanner.Item { Id = 1, LevelMm = 0, XMm = 0, YMm = 0 },
                new RenumberPlanner.Item { Id = 2, LevelMm = 0, XMm = 1, YMm = 0 },
            };

            var plan = RenumberPlanner.Plan(items, "", 10, 5);

            Assert.Equal(new[] { "10", "15" }, plan.Select(p => p.Item2).ToArray());
        }

        [Fact]
        public void Rejects_bad_start_and_step()
        {
            var items = new List<RenumberPlanner.Item>
            {
                new RenumberPlanner.Item { Id = 1 },
            };

            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => RenumberPlanner.Plan(items, "", -1, 1));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => RenumberPlanner.Plan(items, "", 1, 0));
        }
    }
}
