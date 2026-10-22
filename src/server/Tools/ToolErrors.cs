using System;
using RvtMcp.Plugin;

namespace RvtMcp.Server
{
    /// <summary>
    /// One place that turns an exception into a message the agent may see.
    ///
    /// Tool methods used to answer with a raw <c>ex.Message</c>, which could carry an
    /// absolute path (for example <c>C:\Users\...</c>) straight to the client. The
    /// plugin side already masked those with <see cref="ErrorSanitizer"/>; the server
    /// side now does the same, so no error text leaves the machine with a path in it.
    /// </summary>
    internal static class ToolErrors
    {
        /// <summary>Formats an exception for a tool response, with paths masked.</summary>
        public static string Format(Exception ex)
        {
            if (ex == null) return "Error: unknown failure.";
            return "Error: " + Sanitize(ex.Message);
        }

        /// <summary>Formats an exception with a short prefix naming the failing step.</summary>
        public static string Format(Exception ex, string context)
        {
            if (ex == null) return "Error: unknown failure.";
            var prefix = string.IsNullOrWhiteSpace(context) ? string.Empty : context + ": ";
            return "Error: " + prefix + Sanitize(ex.Message);
        }

        /// <summary>Masks absolute paths in a message.</summary>
        public static string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message)) return message ?? string.Empty;
            try { return ErrorSanitizer.Sanitize(message); }
            catch { return message; }
        }
    }
}
