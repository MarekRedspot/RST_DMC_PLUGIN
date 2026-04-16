using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using Base;
using Core;

namespace RST_HomePlugin
{
    /// <summary>
    /// RST sensor-based homing command.
    ///
    /// Behaviour:
    ///  - Drives the selected axis in the chosen direction at the search speed.
    ///  - Stops when the configured digital-input "homing sensor" becomes active.
    ///  - If "Precise Mode" is on, it then backs off by <c>backoff_distance</c>, slows to
    ///    <c>precise_speed</c> and approaches the sensor again, stopping at the precise
    ///    trigger edge.
    ///  - If "Use Emergency Sensor" is on and the emergency sensor triggers before the
    ///    main sensor, motion is stopped and either the recipe fails with an error or a
    ///    recipe variable is set to 1, depending on the chosen action.
    ///  - Travel is limited by <c>max_travel</c>; exceeding this distance fails the command.
    /// </summary>
    public class HomeCommand : ICommand
    {
        public const string UN = "rst_home_command";
        public const string FN = "RST Home";
        public const string DS = "Sensor based axis homing (RST)";

        public HomeCommandParameters parameters = new HomeCommandParameters();

        private volatile bool is_cancel = false;

        public HomeCommand() : base(UN, FN, DS)
        {
            Add(parameters);
        }

        // Creator used by DMC when the command is deserialized from a recipe / dragged from the ribbon.
        public static ICommand Create() { return new HomeCommand(); }

        // Control command: it executes an action at runtime, it is not a geometry command.
        public override bool IsControlCommand { get { return true; } }

        // Short info shown in the recipe tree next to the command title.
        public override string GetInfo()
        {
            string axis = string.IsNullOrEmpty(parameters.axis_name.Value) ? "?" : parameters.axis_name.Value;
            string dir = parameters.direction_positive.value ? "+" : "-";
            string sensor = string.IsNullOrEmpty(parameters.sensor_input.Value) ? "?" : parameters.sensor_input.Value;
            string precise = parameters.use_precise_mode.value ? " precise" : "";
            string safety = parameters.use_safety_sensor.value ? " +safety" : "";
            return axis + " " + dir + " @ " + sensor + precise + safety;
        }

        // Auto-generated GUI form built from the parameter list. The parameter wrapper
        // implements IGUIManagerParameter so dependent fields hide/show correctly.
        public override ICommandGUI GetGUI()
        {
            parameters.RefreshLists(); // refresh axes / IO dropdowns before rendering

            HeaderGroupBox header = new HeaderGroupBox();
            header.Text = FN;
            return ICommandGUI.GetGUIForm(parameters.parameters, new List<Control> { header }, parameters);
        }

        public override void Stop()
        {
            is_cancel = true;
            try
            {
                var axis_settings = ResolveAxisSettings(false);
                if (axis_settings != null && axis_settings.Axis != null)
                {
                    // Best effort: stop jog and any ongoing motion.
                    var free = axis_settings.Axis as IAxisFreemove;
                    if (free != null)
                    {
                        try { free.StopFreemove(); } catch (Exception) { }
                    }
                    try { axis_settings.Axis.Stop(); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        // Compile validates parameters without touching hardware.
        public override bool Compile()
        {
            is_cancel = false;
            if (!ParseAll()) return false;

            if (string.IsNullOrEmpty(parameters.axis_name.Value))
                return Functions.Error(this, "Axis is not selected.");

            if (string.IsNullOrEmpty(parameters.sensor_input.Value))
                return Functions.Error(this, "Homing sensor (digital input) is not selected.");

            if (parameters.max_travel.value <= 0)
                return Functions.Error(this, "'" + parameters.max_travel.title + "' must be > 0.");

            if (parameters.search_speed.value <= 0)
                return Functions.Error(this, "'" + parameters.search_speed.title + "' must be > 0.");

            if (parameters.use_precise_mode.value)
            {
                if (parameters.precise_speed.value <= 0)
                    return Functions.Error(this, "'" + parameters.precise_speed.title + "' must be > 0.");
                if (parameters.precise_speed.value > parameters.search_speed.value)
                    return Functions.Error(this, "Precise speed should be <= search speed.");
                if (parameters.backoff_distance.value <= 0)
                    return Functions.Error(this, "'" + parameters.backoff_distance.title + "' must be > 0.");
            }

            if (parameters.use_safety_sensor.value)
            {
                if (string.IsNullOrEmpty(parameters.safety_input.Value))
                    return Functions.Error(this, "Emergency sensor input is not selected.");

                if (parameters.safety_input.Value == parameters.sensor_input.Value)
                    return Functions.Error(this, "Emergency sensor must be different from the homing sensor.");

                if (!parameters.safety_action_error.value &&
                    string.IsNullOrEmpty(parameters.safety_variable_name.Value))
                    return Functions.Error(this, "Emergency variable name must be set when action is 'Set variable'.");
            }

            // Early declaration of the emergency / result variables so later commands can reference them.
            if (parameters.use_safety_sensor.value &&
                !parameters.safety_action_error.value &&
                !string.IsNullOrEmpty(parameters.safety_variable_name.Value))
            {
                Recipe.variables.Add(new Variable(parameters.safety_variable_name.Value, 0));
            }

            if (!string.IsNullOrEmpty(parameters.result_position_variable.Value))
                Recipe.variables.Add(new Variable(parameters.result_position_variable.Value, 0));

            return true;
        }

        public override bool Run()
        {
            is_cancel = false;

            if (!Compile()) return false;

            if (!State.is_connected_to_hardware)
                return Functions.Error(this, "Not connected to hardware.");

            // Resolve axis, sensor and (optional) safety sensor.
            var axis_settings = ResolveAxisSettings(true);
            if (axis_settings == null) return false;

            var axis = axis_settings.Axis;
            if (axis == null)
                return Functions.Error(this, "Selected axis '" + axis_settings.GetFullName() + "' has no hardware driver.");

            IOTool sensor = FindInputByName(parameters.sensor_input.Value);
            if (sensor == null)
                return Functions.Error(this, "Digital input '" + parameters.sensor_input.Value + "' not found or is not an input.");

            IOTool safety = null;
            if (parameters.use_safety_sensor.value)
            {
                safety = FindInputByName(parameters.safety_input.Value);
                if (safety == null)
                    return Functions.Error(this, "Emergency input '" + parameters.safety_input.Value + "' not found or is not an input.");
            }

            double dir = parameters.direction_positive.value ? 1.0 : -1.0;

            // Before starting, check that we are not already in emergency state.
            if (safety != null && IsTriggered(safety, parameters.safety_active_high.value))
            {
                return HandleEmergency("Emergency sensor is already active before motion starts.");
            }

            // Phase 1: coarse search at search_speed.
            double hit_position = 0;
            var phase1_result = MoveUntilSensor(
                axis_settings,
                dir,
                parameters.search_speed.value,
                parameters.max_travel.value,
                sensor,
                parameters.sensor_active_high.value,
                safety,
                parameters.safety_active_high.value,
                out hit_position);

            if (phase1_result == Result.Cancel) return true;
            if (phase1_result == Result.Emergency)
                return HandleEmergency("Emergency sensor triggered before homing sensor.");
            if (phase1_result == Result.TravelExceeded)
                return Functions.Error(this, "Homing sensor not detected within '" + parameters.max_travel.title + "' range.");
            if (phase1_result == Result.Error)
                return false;

            // Phase 2: optional precise approach.
            if (parameters.use_precise_mode.value)
            {
                // Back off in the opposite direction so the sensor is guaranteed to be released.
                double backoff_target = hit_position - dir * parameters.backoff_distance.value;
                backoff_target = Clamp(backoff_target, axis_settings.MinPosition, axis_settings.MaxPosition);

                if (!axis.Move(backoff_target, parameters.search_speed.value, true))
                    return Functions.Error(this, "Unable to back off from sensor.");

                if (is_cancel || State.IsCancel) return true;

                // Safety-sensor sanity check while released from the main sensor.
                if (safety != null && IsTriggered(safety, parameters.safety_active_high.value))
                    return HandleEmergency("Emergency sensor active during back-off.");

                // Slow approach: limit max travel to backoff + small margin.
                double precise_max_travel = parameters.backoff_distance.value * 2.0 + 0.5;

                var phase2_result = MoveUntilSensor(
                    axis_settings,
                    dir,
                    parameters.precise_speed.value,
                    precise_max_travel,
                    sensor,
                    parameters.sensor_active_high.value,
                    safety,
                    parameters.safety_active_high.value,
                    out hit_position);

                if (phase2_result == Result.Cancel) return true;
                if (phase2_result == Result.Emergency)
                    return HandleEmergency("Emergency sensor triggered during precise approach.");
                if (phase2_result == Result.TravelExceeded)
                    return Functions.Error(this, "Homing sensor not re-detected during precise approach.");
                if (phase2_result == Result.Error)
                    return false;
            }

            // Success: publish result variables.
            if (parameters.use_safety_sensor.value &&
                !parameters.safety_action_error.value &&
                !string.IsNullOrEmpty(parameters.safety_variable_name.Value))
            {
                Recipe.variables.Add(new Variable(parameters.safety_variable_name.Value, 0));
            }

            if (!string.IsNullOrEmpty(parameters.result_position_variable.Value))
                Recipe.variables.Add(new Variable(parameters.result_position_variable.Value, hit_position));

            Base.StatusBar.Set("RST Home done on '" + axis_settings.GetFullName() + "' at " + hit_position.ToString("0.###"), true);
            return true;
        }

        // -----------------------------------------------------------------
        // Motion helpers
        // -----------------------------------------------------------------

        private enum Result { Ok, Cancel, TravelExceeded, Emergency, Error }

        /// <summary>
        /// Drives the axis in <paramref name="dir"/> direction at <paramref name="speed"/>
        /// and polls the sensor. Stops as soon as the sensor triggers, cancel is raised,
        /// travel exceeds <paramref name="max_travel"/>, or the optional safety sensor triggers.
        /// </summary>
        private Result MoveUntilSensor(
            IAxisSettings axis_settings,
            double dir,
            double speed,
            double max_travel,
            IOTool sensor, bool sensor_active_high,
            IOTool safety, bool safety_active_high,
            out double hit_position)
        {
            hit_position = 0;
            var axis = axis_settings.Axis;
            double start_position = axis.GetPosition();
            hit_position = start_position;

            // If the sensor is already active at start, treat it as immediate hit.
            if (IsTriggered(sensor, sensor_active_high))
            {
                hit_position = start_position;
                return Result.Ok;
            }

            // Compute a fallback target for axes that don't support freemove.
            double target = start_position + dir * max_travel;
            target = Clamp(target, axis_settings.MinPosition, axis_settings.MaxPosition);

            // Validate the search speed against axis limits.
            double real_speed = Math.Min(Math.Abs(speed), axis_settings.MaxSpeed);
            if (real_speed <= 0)
            {
                Functions.Error(this, "Axis '" + axis_settings.GetFullName() + "' max speed is 0.");
                return Result.Error;
            }

            // Start motion. Prefer IAxisFreemove (JOG) because it runs until we stop it.
            IAxisFreemove free = axis as IAxisFreemove;
            bool used_freemove = false;
            try
            {
                if (free != null)
                {
                    if (!free.StartFreemove(dir * real_speed))
                    {
                        Functions.Error(this, "Unable to start free-move on axis '" + axis_settings.GetFullName() + "'.");
                        return Result.Error;
                    }
                    used_freemove = true;
                }
                else
                {
                    // Non-blocking Move towards the allowed travel limit.
                    if (!axis.Move(target, real_speed, false))
                    {
                        Functions.Error(this, "Unable to start motion on axis '" + axis_settings.GetFullName() + "'.");
                        return Result.Error;
                    }
                }

                // Poll loop.
                var sw = Stopwatch.StartNew();
                double traveled = 0;
                while (true)
                {
                    if (is_cancel || State.IsCancel || State.is_exit)
                    {
                        StopMotion(axis, free, used_freemove);
                        return Result.Cancel;
                    }

                    // Emergency sensor check comes FIRST so we react to a fault before acknowledging a hit.
                    if (safety != null && IsTriggered(safety, safety_active_high))
                    {
                        StopMotion(axis, free, used_freemove);
                        hit_position = axis.GetPosition();
                        return Result.Emergency;
                    }

                    if (IsTriggered(sensor, sensor_active_high))
                    {
                        StopMotion(axis, free, used_freemove);
                        hit_position = axis.GetPosition();
                        return Result.Ok;
                    }

                    // Travel limit.
                    double current = axis.GetPosition();
                    traveled = Math.Abs(current - start_position);
                    if (traveled > max_travel + 1e-6)
                    {
                        StopMotion(axis, free, used_freemove);
                        hit_position = current;
                        return Result.TravelExceeded;
                    }

                    // Hard safety: if Move was issued and the axis reached the target without a hit.
                    if (!used_freemove)
                    {
                        var state = axis.GetAxisState();
                        if (!state.HasFlag(AxisState.Moving) && sw.ElapsedMilliseconds > 50)
                        {
                            // Motion finished but sensor not detected.
                            hit_position = current;
                            return Result.TravelExceeded;
                        }
                    }

                    Thread.Sleep(5);
                }
            }
            catch (Exception ex)
            {
                StopMotion(axis, free, used_freemove);
                Functions.Error(this, "Homing motion failed. ", ex);
                return Result.Error;
            }
        }

        private static void StopMotion(IAxis axis, IAxisFreemove free, bool used_freemove)
        {
            try
            {
                if (used_freemove && free != null) free.StopFreemove();
            }
            catch (Exception) { }
            try { axis.Stop(); } catch (Exception) { }
            // Small delay to let the motion controller settle before we read the final position.
            try { Thread.Sleep(50); } catch (Exception) { }
        }

        // -----------------------------------------------------------------
        // Emergency handling
        // -----------------------------------------------------------------

        private bool HandleEmergency(string reason)
        {
            if (parameters.safety_action_error.value)
            {
                // Abort the recipe with an error.
                return Functions.Error(this, "RST Home emergency: " + reason);
            }

            // Continue execution but flag the error via a recipe variable.
            string var_name = parameters.safety_variable_name.Value;
            if (!string.IsNullOrEmpty(var_name))
                Recipe.variables.Add(new Variable(var_name, 1));

            Base.StatusBar.Set("RST Home emergency (continuing via variable): " + reason, true);
            return true;
        }

        // -----------------------------------------------------------------
        // Resolution helpers
        // -----------------------------------------------------------------

        private IAxisSettings ResolveAxisSettings(bool report_errors)
        {
            string name = parameters.axis_name.Value;
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < Base.Settings.Axes.Count; i++)
            {
                var a = Base.Settings.Axes[i];
                if (a != null && a.GetFullName() == name)
                {
                    if (!a.Enabled && report_errors)
                    {
                        Functions.Error(this, "Axis '" + name + "' is disabled in settings.");
                        return null;
                    }
                    return a;
                }
            }
            if (report_errors) Functions.Error(this, "Axis '" + name + "' not found.");
            return null;
        }

        /// <summary>
        /// Finds a digital input by its user-visible name. Tries the built-in
        /// <see cref="IOTools.GetInput"/> lookup first, then falls back to a
        /// scan of <see cref="Settings.IOTools.list"/> matching against the
        /// reflected preset name (see <see cref="HomeCommandParameters.GetIOToolName"/>).
        /// This is needed because what the GUI shows is the user-configured
        /// preset name (e.g. "test home"), not the internal class-type
        /// <c>unique_name</c> that <see cref="IOTools.GetInput"/> matches on.
        /// </summary>
        private static IOTool FindInputByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            // Standard lookup (works if the name happens to match unique_name).
            try
            {
                var io = Settings.IOTools.GetInput(name);
                if (io != null) return io;
            }
            catch (Exception) { }

            // Fallback: scan the raw list and match by the user-visible label.
            if (Settings.IOTools != null && Settings.IOTools.list != null)
            {
                foreach (var io in Settings.IOTools.list)
                {
                    if (io == null) continue;
                    if (!io.IsInput) continue;
                    if (HomeCommandParameters.GetIOToolName(io) == name) return io;
                }
            }
            return null;
        }

        private static bool IsTriggered(IOTool io, bool active_high)
        {
            bool high = io.IsDigitalInputHigh();
            return active_high ? high : !high;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
