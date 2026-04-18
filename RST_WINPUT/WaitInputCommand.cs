using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using Base;
using Core;

namespace RST_WInputPlugin
{
    /// <summary>
    /// RST "Wait For Input" command.
    ///
    /// Blocks the recipe until a combination of digital inputs reaches the
    /// user-configured states. Up to <see cref="WaitInputCommandParameters.MAX_INPUTS"/>
    /// inputs may be selected and combined via logical AND (all must match)
    /// or OR (any one must match). An optional timeout aborts the wait - the
    /// user can choose between failing the recipe with an error or setting a
    /// recipe variable to 1 and continuing.
    /// </summary>
    public class WaitInputCommand : ICommand
    {
        public const string UN = "rst_winput_command";
        public const string FN = "RST Wait For Input";
        public const string DS = "Wait until one or more digital inputs match the configured states (AND/OR) with optional timeout.";

        public WaitInputCommandParameters parameters = new WaitInputCommandParameters();

        private volatile bool is_cancel = false;

        public WaitInputCommand() : base(UN, FN, DS)
        {
            Add(parameters);
        }

        public static ICommand Create() { return new WaitInputCommand(); }

        // Control command: runs at recipe time, not a geometry command.
        public override bool IsControlCommand { get { return true; } }

        public override string GetInfo()
        {
            int n = parameters.ActiveCount();
            string logic;
            if (n <= 1) logic = "";
            else logic = parameters.logic_and.value ? " AND" : " OR";

            string preview = "";
            for (int i = 0; i < n; i++)
            {
                string pn = parameters.input_preset[i].Value;
                string st = parameters.input_state_high[i].value ? "H" : "L";
                if (string.IsNullOrEmpty(pn)) pn = "?";
                if (i > 0) preview += ",";
                preview += pn + "=" + st;
            }

            string to = parameters.use_timeout.value ? (" t=" + parameters.timeout_seconds.value + "s") : "";
            return preview + logic + to;
        }

        public override ICommandGUI GetGUI()
        {
            parameters.RefreshLists();

            HeaderGroupBox header = new HeaderGroupBox();
            header.Text = FN;
            return ICommandGUI.GetGUIForm(parameters.parameters, new List<Control> { header }, parameters);
        }

        public override void Stop()
        {
            is_cancel = true;
        }

        // Validates parameters without touching hardware.
        public override bool Compile()
        {
            is_cancel = false;

            if (!ParseAll()) return false;

            int raw = parameters.input_count.value;
            if (raw < 1 || raw > WaitInputCommandParameters.MAX_INPUTS)
                return Functions.Error(this, "'" + parameters.input_count.title +
                                             "' must be between 1 and " +
                                             WaitInputCommandParameters.MAX_INPUTS + ".");

            int n = parameters.ActiveCount();

            // At least one slot must have a preset selected; every *active* slot
            // must be filled, otherwise the wait is ill-defined.
            for (int i = 0; i < n; i++)
            {
                if (string.IsNullOrEmpty(parameters.input_preset[i].Value))
                    return Functions.Error(this, "Input " + (i + 1) + " has no preset selected.");
            }

            if (parameters.use_timeout.value)
            {
                if (parameters.timeout_seconds.value <= 0)
                    return Functions.Error(this, "'" + parameters.timeout_seconds.title + "' must be > 0.");

                if (!parameters.timeout_action_error.value &&
                    string.IsNullOrEmpty(parameters.timeout_variable_name.Value))
                    return Functions.Error(this, "Timeout variable name must be set when action is 'Set variable'.");
            }

            // Pre-declare the timeout variable so later commands can reference it.
            if (parameters.use_timeout.value &&
                !parameters.timeout_action_error.value &&
                !string.IsNullOrEmpty(parameters.timeout_variable_name.Value))
            {
                Recipe.variables.Add(new Variable(parameters.timeout_variable_name.Value, 0));
            }

            return true;
        }

        public override bool Run()
        {
            is_cancel = false;

            if (!Compile()) return false;

            if (!State.is_connected_to_hardware)
                return Functions.Error(this, "Not connected to hardware.");

            int n = parameters.ActiveCount();
            var inputs = new IOTool[n];
            var want_high = new bool[n];

            for (int i = 0; i < n; i++)
            {
                string pn = parameters.input_preset[i].Value;
                var io = FindInputByName(pn);
                if (io == null)
                    return Functions.Error(this, "Input " + (i + 1) + " preset '" + pn + "' not found or is not an input.");
                inputs[i] = io;
                want_high[i] = parameters.input_state_high[i].value;
            }

            bool logic_and = parameters.logic_and.value || n == 1;
            bool use_timeout = parameters.use_timeout.value;
            double timeout_ms = parameters.timeout_seconds.value * 1000.0;

            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (is_cancel || State.IsCancel || State.is_exit)
                    return true;

                // Evaluate the combined condition.
                bool combined = logic_and;
                for (int i = 0; i < n; i++)
                {
                    bool match = IsTriggered(inputs[i], want_high[i]);
                    if (logic_and)
                    {
                        if (!match) { combined = false; break; }
                    }
                    else
                    {
                        if (match) { combined = true; break; }
                        combined = false;
                    }
                }

                if (combined)
                {
                    // Success: publish timeout variable = 0 if the user asked for it.
                    if (use_timeout &&
                        !parameters.timeout_action_error.value &&
                        !string.IsNullOrEmpty(parameters.timeout_variable_name.Value))
                    {
                        Recipe.variables.Add(new Variable(parameters.timeout_variable_name.Value, 0));
                    }

                    Base.StatusBar.Set("RST Wait For Input: matched after " + (sw.ElapsedMilliseconds / 1000.0).ToString("0.###") + " s", true);
                    return true;
                }

                // Timeout check.
                if (use_timeout && sw.Elapsed.TotalMilliseconds >= timeout_ms)
                {
                    return HandleTimeout();
                }

                Thread.Sleep(5);
            }
        }

        private bool HandleTimeout()
        {
            if (parameters.timeout_action_error.value)
            {
                return Functions.Error(this, "RST Wait For Input: timeout after " + parameters.timeout_seconds.value + " s.");
            }

            string var_name = parameters.timeout_variable_name.Value;
            if (!string.IsNullOrEmpty(var_name))
                Recipe.variables.Add(new Variable(var_name, 1));

            Base.StatusBar.Set("RST Wait For Input: timeout (continuing via variable '" + var_name + "').", true);
            return true;
        }

        // -----------------------------------------------------------------
        // Resolution helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Finds a digital input by its user-visible name. Tries the built-in
        /// <see cref="IOTools.GetInput"/> lookup first, then falls back to a
        /// scan of <see cref="Settings.IOTools.list"/> matching against the
        /// reflected preset name (see <see cref="WaitInputCommandParameters.GetIOToolName"/>).
        /// </summary>
        private static IOTool FindInputByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            try
            {
                var io = Settings.IOTools.GetInput(name);
                if (io != null) return io;
            }
            catch (Exception) { }

            if (Settings.IOTools != null && Settings.IOTools.list != null)
            {
                foreach (var io in Settings.IOTools.list)
                {
                    if (io == null) continue;
                    if (!io.IsInput) continue;
                    if (WaitInputCommandParameters.GetIOToolName(io) == name) return io;
                }
            }
            return null;
        }

        private static bool IsTriggered(IOTool io, bool active_high)
        {
            bool high = io.IsDigitalInputHigh();
            return active_high ? high : !high;
        }
    }
}
