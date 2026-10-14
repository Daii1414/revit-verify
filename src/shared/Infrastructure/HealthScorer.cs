using System;
using System.Collections.Generic;

namespace RvtMcp.Plugin
{
    /// <summary>Pure model-health scoring (no Revit API): unit-testable core behind
    /// check_model_health. Starts at 100 and subtracts capped deductions so one bad
    /// category cannot zero the whole model.</summary>
    public static class HealthScorer
    {
        public sealed class Deduction
        {
            public string Rule { get; set; }
            public int Points { get; set; }
            public string Detail { get; set; }
        }

        public sealed class HealthScore
        {
            public int Score { get; set; }
            public string Grade { get; set; }
            public List<Deduction> Deductions { get; set; }
            public List<string> Recommendations { get; set; }
        }

        public static HealthScore Score(int errors, int warnings, int cadImports,
            int rvtLinks, int inPlaceFamilies, bool truncated)
        {
            var deductions = new List<Deduction>();
            if (errors > 0)
                deductions.Add(new Deduction
                {
                    Rule = "errors",
                    Points = Math.Min(30, errors * 5),
                    Detail = errors + " error-level warnings"
                });
            var warningPoints = Math.Min(20, warnings / 10);
            if (warningPoints > 0)
                deductions.Add(new Deduction
                {
                    Rule = "warnings",
                    Points = warningPoints,
                    Detail = warnings + " warnings"
                });
            if (cadImports > 0)
                deductions.Add(new Deduction
                {
                    Rule = "cad_imports",
                    Points = Math.Min(10, cadImports * 2),
                    Detail = cadImports + " CAD import/link instances"
                });
            if (rvtLinks > 0)
                deductions.Add(new Deduction
                {
                    Rule = "rvt_links",
                    Points = Math.Min(5, rvtLinks),
                    Detail = rvtLinks + " linked Revit models"
                });
            if (inPlaceFamilies > 0)
                deductions.Add(new Deduction
                {
                    Rule = "in_place_families",
                    Points = Math.Min(10, inPlaceFamilies),
                    Detail = inPlaceFamilies + " in-place family instances"
                });

            var total = 0;
            foreach (var d in deductions) total += d.Points;
            var score = Math.Max(0, 100 - total);
            var grade = score >= 90 ? "A" : score >= 75 ? "B" : score >= 60 ? "C" : score >= 40 ? "D" : "F";

            var recommendations = new List<string>();
            foreach (var d in deductions)
            {
                switch (d.Rule)
                {
                    case "errors":
                        recommendations.Add("Fix error-level warnings first (" + d.Detail + ").");
                        break;
                    case "warnings":
                        recommendations.Add("Review and resolve warnings (" + d.Detail + ").");
                        break;
                    case "cad_imports":
                        recommendations.Add("Replace CAD imports with native geometry or link instead of import (" + d.Detail + ").");
                        break;
                    case "rvt_links":
                        recommendations.Add("Confirm linked models are still needed (" + d.Detail + ").");
                        break;
                    case "in_place_families":
                        recommendations.Add("Convert repeated in-place families to loadable families (" + d.Detail + ").");
                        break;
                }
            }
            if (truncated)
                recommendations.Add("Counters were truncated on a large model; raise maxElements for full accuracy.");
            if (recommendations.Count == 0)
                recommendations.Add("Model looks healthy; keep warnings at zero.");

            return new HealthScore
            {
                Score = score,
                Grade = grade,
                Deductions = deductions,
                Recommendations = recommendations
            };
        }
    }
}
