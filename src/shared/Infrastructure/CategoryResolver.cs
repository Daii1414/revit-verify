using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Resolves a category from a user-supplied name in a locale-independent way.
    ///
    /// Revit's <see cref="Category.Name"/> is localized: in a Russian or Japanese Revit
    /// it reads "Стены" / "壁", so an English request like "Walls" matches nothing.
    /// This resolver accepts, in order:
    ///   1. the localized display name ("Стены", "壁") — what the user sees in Revit;
    ///   2. the built-in enum name ("OST_Walls");
    ///   3. the built-in name without the OST_ prefix ("Walls") — locale independent.
    /// </summary>
    public static class CategoryResolver
    {
        /// <summary>Finds a category by localized name, enum name or OST_-stripped name.</summary>
        public static Category Resolve(Document doc, string name)
        {
            if (doc == null || string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();

            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null || string.IsNullOrEmpty(cat.Name)) continue;
                if (string.Equals(cat.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    return cat;
            }

            foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
            {
                var candidate = SafeGetCategory(doc, bic);
                if (candidate == null) continue;
                if (MatchesBuiltIn(bic, trimmed)) return candidate;
            }

            return null;
        }

        /// <summary>
        /// True when the category matches the given name. Accepts the same spellings as
        /// <see cref="Resolve"/> and additionally the OST_-stripped built-in name, so a
        /// filter written as "Walls" still matches in a localized Revit.
        /// </summary>
        public static bool Matches(Category category, string name)
        {
            if (category == null || string.IsNullOrWhiteSpace(name)) return false;
            var trimmed = name.Trim();

            if (!string.IsNullOrEmpty(category.Name) &&
                string.Equals(category.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                return true;

            if (category.Id != null)
            {
                try
                {
                    var bic = (BuiltInCategory)category.Id.Value;
                    if (MatchesBuiltIn(bic, trimmed)) return true;
                }
                catch { }
            }

            return false;
        }

        /// <summary>True when the built-in category matches the given spelling.</summary>
        public static bool MatchesBuiltIn(BuiltInCategory bic, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var trimmed = name.Trim();
            var enumName = bic.ToString();

            if (string.Equals(enumName, trimmed, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(enumName.Replace("OST_", ""), trimmed, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Resolves a category to its built-in id, or null when unknown.</summary>
        public static BuiltInCategory? ResolveBuiltIn(Document doc, string name)
        {
            if (doc == null || string.IsNullOrWhiteSpace(name)) return null;
            var trimmed = name.Trim();

            foreach (BuiltInCategory bic in Enum.GetValues(typeof(BuiltInCategory)))
            {
                if (MatchesBuiltIn(bic, trimmed)) return bic;
            }

            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null || string.IsNullOrEmpty(cat.Name)) continue;
                if (!string.Equals(cat.Name, trimmed, StringComparison.OrdinalIgnoreCase)) continue;
                try { return (BuiltInCategory)cat.Id.Value; } catch { return null; }
            }

            return null;
        }

        /// <summary>Every category in the document with the spellings a caller may use.</summary>
        public static IEnumerable<JObject> DescribeAll(Document doc)
        {
            if (doc == null) yield break;
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat == null || string.IsNullOrEmpty(cat.Name)) continue;
                string enumName = null;
                try { enumName = ((BuiltInCategory)cat.Id.Value).ToString(); } catch { }
                yield return new JObject
                {
                    ["name"] = cat.Name,
                    ["built_in"] = enumName,
                    ["id"] = cat.Id.Value
                };
            }
        }

        private static Category SafeGetCategory(Document doc, BuiltInCategory bic)
        {
            try { return Category.GetCategory(doc, bic); }
            catch { return null; }
        }
    }
}
