using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RvtMcp.Plugin
{
    /// <summary>
    /// Auto-answers the Revit overlap/join dialog ("walls are overlapping, join them?")
    /// while a command that came from MCP is running, so automation is not blocked by a
    /// modal dialog. Outside MCP commands the dialog is left untouched for manual use.
    /// Revit's <see cref="DialogBoxShowingEventArgs"/> exposes no HelpId/text, so the
    /// trigger is narrow by design: only during <see cref="Engage"/>.
    /// </summary>
    public sealed class OverlapDialogResponder
    {
        private static volatile bool _engaged;
        private readonly Action<string> _log;
        private readonly Func<bool> _enabled;
        private UIControlledApplication _application;
        private int _attached;

        public OverlapDialogResponder(Action<string> log, Func<bool> enabled = null)
        {
            _log = log;
            _enabled = enabled;
        }

        public bool IsEnabled => _enabled == null || _enabled();

        public void Attach(UIControlledApplication application)
        {
            if (application == null) return;
            lock (this)
            {
                if (_attached > 0) { _attached++; return; }
                application.DialogBoxShowing += OnDialogBoxShowing;
                _application = application;
                _attached = 1;
            }
            Log("OverlapDialogResponder attached (auto-answer " + (IsEnabled ? "on" : "off") + ").");
        }

        public void Detach()
        {
            lock (this)
            {
                if (_attached <= 0) return;
                _attached--;
                if (_attached != 0) return;
                if (_application != null)
                    _application.DialogBoxShowing -= OnDialogBoxShowing;
                _application = null;
            }
        }

        /// <summary>Run a Revit command with overlap dialogs auto-answered.</summary>
        public static CommandResult RunEngaged(Func<CommandResult> body, Func<bool> enabled, Action<string> log)
        {
            _engaged = true;
            try
            {
                return body();
            }
            finally
            {
                _engaged = false;
            }
        }

        private void OnDialogBoxShowing(object sender, DialogBoxShowingEventArgs e)
        {
            try
            {
                if (!_engaged || !IsEnabled) return;
                e.OverrideResult((int)Autodesk.Revit.UI.TaskDialogResult.Ok);
                Log("Auto-answered Revit dialog with Ok during MCP command.");
            }
            catch (Exception ex)
            {
                Log("Overlap dialog auto-answer failed: " + ex.Message);
            }
        }

        private void Log(string message)
        {
            try { _log?.Invoke("[RvtMcp] " + message); } catch { }
        }
    }
}
