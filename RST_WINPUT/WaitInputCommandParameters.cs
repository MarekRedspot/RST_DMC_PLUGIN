using System;
using System.Collections.Generic;
using Base;

namespace RST_WInputPlugin
{
    /// <summary>
    /// Parameters for <see cref="WaitInputCommand"/>. Holds up to
    /// <see cref="MAX_INPUTS"/> input slots - the user sets how many are
    /// actually active via <see cref="input_count"/>, and the slots beyond
    /// that index are hidden by <see cref="IsIParameterVisible"/>.
    /// </summary>
    public class WaitInputCommandParameters : MultiParameter, IGUIManagerParameter
    {
        public const string UN = "rst_winput_parameters";
        public const string FN = "RST Wait Input Parameters";

        // Max number of input slots rendered in the GUI. If more are needed,
        // raise this constant and rebuild - the per-slot logic is bounded by it.
        public const int MAX_INPUTS = 8;

        // ---- Global options ---------------------------------------------

        // How many input slots are active (1..MAX_INPUTS). Slots beyond this
        // count are hidden in the GUI and ignored at runtime.
        public IntParameter input_count =
            new IntParameter("input_count", "Number of Inputs", "How many inputs to wait for (1-" + MAX_INPUTS + ").", 1);

        // Combine all active inputs via logical AND (all must match) or OR (any match).
        // Hidden when input_count == 1, because in that case the logic is irrelevant.
        public BoolParameter logic_and =
            new BoolParameter("logic_and", "Logic", "How to combine the state of all active inputs.",
                              true, "AND (all match)", "OR (any match)");

        // ---- Timeout -----------------------------------------------------

        public BoolParameter use_timeout =
            new BoolParameter("use_timeout", "Use Timeout", "Abort the wait after a maximum time.",
                              false, "On", "Off");

        public DoubleParameter timeout_seconds =
            new DoubleParameter("timeout_seconds", "Timeout (s)", 10.0);

        public BoolParameter timeout_action_error =
            new BoolParameter("timeout_action_error", "Timeout Action", "What to do on timeout.",
                              true, "Error (abort recipe)", "Set variable (continue)");

        public StringParameter timeout_variable_name =
            new StringParameter("timeout_variable_name", "Timeout Variable Name",
                                "Recipe variable set to 1 on timeout (and 0 on success) when action is 'Set variable'.",
                                "winput_timeout");

        // ---- Per-input slots --------------------------------------------
        // Each slot has a preset dropdown and an expected-state toggle.
        // We keep plain arrays (fixed length) so the GUI binding is stable.

        public StringListParameter[] input_preset = new StringListParameter[MAX_INPUTS];
        public BoolParameter[] input_state_high = new BoolParameter[MAX_INPUTS];

        public WaitInputCommandParameters() : base(UN, FN, FN)
        {
            Add(input_count);
            Add(logic_and);
            Add(use_timeout);
            Add(timeout_seconds);
            Add(timeout_action_error);
            Add(timeout_variable_name);

            for (int i = 0; i < MAX_INPUTS; i++)
            {
                int n = i + 1;
                input_preset[i] = new StringListParameter(
                    "input_" + n, "Input " + n, "", null, true);
                input_state_high[i] = new BoolParameter(
                    "input_" + n + "_state_high", "Input " + n + " State",
                    "Digital level at which input " + n + " is considered matched.",
                    true, "High", "Low");

                Add(input_preset[i]);
                Add(input_state_high[i]);
            }
        }

        /// <summary>
        /// Refreshes the dropdown list of available IO presets.
        /// </summary>
        public void RefreshLists()
        {
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

            var arr = inputs.ToArray();
            for (int i = 0; i < MAX_INPUTS; i++)
                input_preset[i].list = arr;
        }

        /// <summary>
        /// Clamps <see cref="input_count"/> into [1..MAX_INPUTS]. Called from
        /// Compile() / Run() so we never index outside the slot arrays.
        /// </summary>
        public int ActiveCount()
        {
            int n = input_count.value;
            if (n < 1) n = 1;
            if (n > MAX_INPUTS) n = MAX_INPUTS;
            return n;
        }

        // ---- IGUIManagerParameter -----------------------------------------

        public bool IsIParameterEnabled(IParameter prm) { return true; }

        public bool IsIParameterVisible(IParameter prm)
        {
            int active = ActiveCount();

            // Logic mode only relevant when more than one input is active.
            if (prm == logic_and)
                return active > 1;

            // Timeout sub-parameters follow the master switch.
            if (prm == timeout_seconds || prm == timeout_action_error)
                return use_timeout.value;

            if (prm == timeout_variable_name)
                return use_timeout.value && !timeout_action_error.value;

            // Hide input slots beyond the active count.
            for (int i = 0; i < MAX_INPUTS; i++)
            {
                if (prm == input_preset[i] || prm == input_state_high[i])
                    return i < active;
            }

            return true;
        }

        public List<IParameter> GetDependencies(IParameter prm)
        {
            // Changing the input count re-renders all slot rows and the logic field.
            if (prm == input_count)
            {
                var list = new List<IParameter> { logic_and };
                for (int i = 0; i < MAX_INPUTS; i++)
                {
                    list.Add(input_preset[i]);
                    list.Add(input_state_high[i]);
                }
                return list;
            }

            if (prm == use_timeout)
                return new List<IParameter> { timeout_seconds, timeout_action_error, timeout_variable_name };

            if (prm == timeout_action_error)
                return new List<IParameter> { timeout_variable_name };

            return null;
        }

        // ================================================================
        // IOTool name resolution (mirrors RST_HomePlugin).
        //
        // The user-configured preset name lives on IOPreset.custom_name
        // (and can also be read via IOPreset.GetName()). The class-level
        // MultiParameter labels (unique_name="io_tool", friendly_name="IOPreset")
        // are identical across every instance and must not be treated as
        // preset identifiers. This helper probes the instance-level members
        // first and rejects the well-known class-level sentinels.
        // ================================================================

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

            // 3. Defensive fallbacks.
            foreach (var name in new[] { "preset_name", "instance_name", "user_name", "name" })
            {
                var s = TryReadStringMember(io, t, name, asProperty: false);
                if (!IsClassLevelSentinel(s)) return s;
            }
            foreach (var name in new[] { "PresetName", "InstanceName", "UserName", "CustomName", "Name" })
            {
                var s = TryReadStringMember(io, t, name, asProperty: true);
                if (!IsClassLevelSentinel(s)) return s;
            }

            // 4. Absolute last resort.
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
    }
}
