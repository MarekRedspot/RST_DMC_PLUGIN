using System;
using System.Collections.Generic;
using Base;

namespace RST_HomePlugin
{
    /// <summary>
    /// Parameters for <see cref="HomeCommand"/>. Wrapped in a MultiParameter so that the
    /// automatic GUI generation (Core.ICommandGUI.GetGUIForm) can render the form and use
    /// the IGUIManagerParameter interface to show/hide dependent parameters.
    /// </summary>
    public class HomeCommandParameters : MultiParameter, IGUIManagerParameter
    {
        public const string UN = "rst_home_parameters";
        public const string FN = "RST Home Parameters";

        // ---- Axis selection -----------------------------------------------
        // Populated dynamically from Base.Settings.Axes when GUI is opened.
        public StringListParameter axis_name =
            new StringListParameter("axis_name", "Axis", "", null, true);

        // Direction of initial search motion.
        public BoolParameter direction_positive =
            new BoolParameter("direction_positive", "Search Direction", "Direction of the search motion along the selected axis.",
                              false, "Positive", "Negative");

        // Max allowed travel (safety limit). If sensor is not detected within this distance, command fails.
        public DoubleParameter max_travel =
            new DoubleParameter("max_travel", "Max Travel (mm/deg)", 50.0);

        public DoubleParameter search_speed =
            new DoubleParameter("search_speed", "Search Speed (units/s)", 10.0);

        // ---- Precise two-phase mode ---------------------------------------
        public BoolParameter use_precise_mode =
            new BoolParameter("use_precise_mode", "Precise Mode", "Enable two-speed precise approach.",
                              false, "On", "Off");

        public DoubleParameter precise_speed =
            new DoubleParameter("precise_speed", "Precise Speed (units/s)", 1.0);

        public DoubleParameter backoff_distance =
            new DoubleParameter("backoff_distance", "Back-off Distance (mm/deg)", 1.0);

        // ---- Main homing sensor -------------------------------------------
        // Populated dynamically from Base.Settings.IOTools.list (inputs only) when GUI is opened.
        public StringListParameter sensor_input =
            new StringListParameter("sensor_input", "Homing Sensor", "", null, true);

        public BoolParameter sensor_active_high =
            new BoolParameter("sensor_active_high", "Sensor Active State", "Digital level at which the sensor is considered triggered.",
                              true, "High", "Low");

        // ---- Optional emergency (safety) sensor ---------------------------
        public BoolParameter use_safety_sensor =
            new BoolParameter("use_safety_sensor", "Use Emergency Sensor", "Enable a second sensor that must not be triggered before the main sensor.",
                              false, "On", "Off");

        public StringListParameter safety_input =
            new StringListParameter("safety_input", "Emergency Sensor", "", null, true);

        public BoolParameter safety_active_high =
            new BoolParameter("safety_active_high", "Emergency Active State", "Digital level at which the emergency sensor is considered triggered.",
                              true, "High", "Low");

        // Action taken when the emergency sensor triggers before the main sensor.
        public BoolParameter safety_action_error =
            new BoolParameter("safety_action_error", "Emergency Action", "What to do if the emergency sensor triggers first.",
                              true, "Error (abort recipe)", "Set variable (continue)");

        public StringParameter safety_variable_name =
            new StringParameter("safety_variable_name", "Emergency Variable Name", "Recipe variable that will be set to 1 on emergency (and 0 on success).",
                                "home_error");

        // ---- Result ----------------------------------------------------------
        public StringParameter result_position_variable =
            new StringParameter("result_position_variable", "Result Position Variable",
                                "Optional: recipe variable where the axis position at the moment of the final sensor hit will be stored.",
                                "");

        public HomeCommandParameters() : base(UN, FN, FN)
        {
            Add(axis_name);
            Add(direction_positive);
            Add(max_travel);
            Add(search_speed);

            Add(use_precise_mode);
            Add(precise_speed);
            Add(backoff_distance);

            Add(sensor_input);
            Add(sensor_active_high);

            Add(use_safety_sensor);
            Add(safety_input);
            Add(safety_active_high);
            Add(safety_action_error);
            Add(safety_variable_name);

            Add(result_position_variable);
        }

        /// <summary>
        /// Refreshes dynamic dropdown lists from current DMC settings. Called from the
        /// command's GetGUI() so that the user always sees the up-to-date list of
        /// enabled axes and configured digital inputs.
        /// </summary>
        public void RefreshLists()
        {
            // Axes: take enabled axes only.
            var axes = new List<string>();
            for (int i = 0; i < Base.Settings.Axes.Count; i++)
            {
                var a = Base.Settings.Axes[i];
                if (a == null || !a.Enabled) continue;
                axes.Add(a.GetFullName());
            }
            axis_name.list = axes.ToArray();

            // Digital inputs: take IO tools that are inputs. IOTool does not expose
            // a public .Name / .FriendlyName / .UniqueName that holds the user-configured
            // preset name (the one visible in Settings -> IO Tools -> Name, e.g. "test home"),
            // and its .unique_name field is a class-type identifier common to all IOTool
            // instances ("io_tool"). Use reflection to find the right field/property.
            var inputs = new List<string>();
            if (Base.Settings.IOTools != null && Base.Settings.IOTools.list != null)
            {
                foreach (var io in Base.Settings.IOTools.list)
                {
                    if (io == null) continue;
                    if (!io.IsInput) continue;
                    inputs.Add(GetIOToolName(io));
                }
            }
            sensor_input.list = inputs.ToArray();
            safety_input.list = inputs.ToArray();
        }

        /// <summary>
        /// Returns the user-visible name of an IOTool preset (e.g. "test home"
        /// configured in File -> Settings -> IO Tools).
        ///
        /// DMC's actual data model (confirmed against Base.dll metadata):
        ///   <c>Base.IOTool : Base.IOPreset : Base.MultiParameter</c>
        /// and <c>IOPreset</c> declares a <c>custom_name</c> parameter field plus
        /// a <c>GetName()</c> instance method. The user-configured preset name
        /// lives in <c>custom_name.Value</c> - NOT in any inherited MultiParameter
        /// label (<c>unique_name</c>="io_tool", <c>friendly_name</c>="IOPreset"
        /// are class-level and identical across every IOTool instance).
        ///
        /// Reflection is used so the plugin still compiles against any DMC build
        /// that keeps the <c>custom_name</c> member internal or renames it.
        /// </summary>
        public static string GetIOToolName(IOTool io)
        {
            if (io == null) return "";
            var t = io.GetType();

            // 1. IOPreset.custom_name - the per-instance user preset name.
            var v = TryReadStringMember(io, t, "custom_name", asProperty: false);
            if (!IsClassLevelSentinel(v)) return v;

            // 2. IOPreset.GetName() - public instance method defined on IOPreset.
            try
            {
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;
                var m = t.GetMethod("GetName", flags, null, Type.EmptyTypes, null);
                if (m != null && m.ReturnType == typeof(string))
                {
                    var s = m.Invoke(io, null) as string;
                    if (!IsClassLevelSentinel(s)) return s;
                }
            }
            catch (Exception) { }

            // 3. Other plausible per-instance members (defensive, in case DMC
            //    renames custom_name in a future version).
            string[] fallbackFields = { "preset_name", "instance_name", "user_name", "name" };
            foreach (var name in fallbackFields)
            {
                var s = TryReadStringMember(io, t, name, asProperty: false);
                if (!IsClassLevelSentinel(s)) return s;
            }
            string[] fallbackProps = { "PresetName", "InstanceName", "UserName", "CustomName", "Name" };
            foreach (var name in fallbackProps)
            {
                var s = TryReadStringMember(io, t, name, asProperty: true);
                if (!IsClassLevelSentinel(s)) return s;
            }

            // 4. Absolute last resort - a class-level label so we render something.
            foreach (var name in new[] { "friendly_name", "title" })
            {
                var s = TryReadStringMember(io, t, name, asProperty: false);
                if (!string.IsNullOrEmpty(s)) return s;
            }

            try { return io.ToString() ?? ""; } catch (Exception) { return ""; }
        }

        private static bool IsClassLevelSentinel(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            // Class-level labels that the DMC framework sets in IOTool's
            // base constructor - they are identical for every instance and
            // must never be used to identify one preset vs. another.
            return s == "io_tool" || s == "IOPreset" || s == "IOInputPreset" ||
                   s == "IOOutputPreset" || s == "IOTool";
        }

        private static string TryReadStringMember(object obj, Type t, string memberName, bool asProperty)
        {
            if (obj == null || t == null) return "";
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.FlattenHierarchy;

            Type walker = t;
            while (walker != null)
            {
                try
                {
                    object raw = null;
                    if (asProperty)
                    {
                        var p = walker.GetProperty(memberName, flags);
                        if (p != null && p.CanRead) raw = p.GetValue(obj, null);
                    }
                    else
                    {
                        var f = walker.GetField(memberName, flags);
                        if (f != null) raw = f.GetValue(obj);
                    }

                    var s = UnwrapToString(raw);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                catch (Exception) { }
                walker = walker.BaseType;
            }
            return "";
        }

        /// <summary>
        /// Returns the raw object as a string if possible. If the object is a
        /// parameter wrapper (has a <c>.Value</c> or <c>.value</c> member),
        /// the wrapped string is returned instead.
        /// </summary>
        private static string UnwrapToString(object raw)
        {
            if (raw == null) return "";
            if (raw is string direct) return direct;

            var vt = raw.GetType();
            try
            {
                var prop = vt.GetProperty("Value");
                if (prop != null && prop.PropertyType == typeof(string))
                {
                    var sv = prop.GetValue(raw, null) as string;
                    if (!string.IsNullOrEmpty(sv)) return sv;
                }
            }
            catch (Exception) { }
            try
            {
                var f = vt.GetField("value");
                if (f != null && f.FieldType == typeof(string))
                {
                    var sv = f.GetValue(raw) as string;
                    if (!string.IsNullOrEmpty(sv)) return sv;
                }
            }
            catch (Exception) { }
            return "";
        }

        // ---- IGUIManagerParameter -----------------------------------------
        public bool IsIParameterEnabled(IParameter prm) { return true; }

        public bool IsIParameterVisible(IParameter prm)
        {
            // Precise mode controls visibility of precise_speed and backoff_distance.
            if (prm == precise_speed || prm == backoff_distance)
                return use_precise_mode.value;

            // Safety sensor controls visibility of related parameters.
            if (prm == safety_input || prm == safety_active_high || prm == safety_action_error)
                return use_safety_sensor.value;

            // Safety variable name is only relevant when using safety sensor in "Set variable" mode.
            if (prm == safety_variable_name)
                return use_safety_sensor.value && !safety_action_error.value;

            return true;
        }

        public List<IParameter> GetDependencies(IParameter prm)
        {
            if (prm == use_precise_mode)
                return new List<IParameter> { precise_speed, backoff_distance };

            if (prm == use_safety_sensor)
                return new List<IParameter> { safety_input, safety_active_high, safety_action_error, safety_variable_name };

            if (prm == safety_action_error)
                return new List<IParameter> { safety_variable_name };

            return null;
        }
    }
}
