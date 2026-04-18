using Base;
using Core;

namespace RST_WInputPlugin
{
    // Plugin class. Class name is public, contains the word "Plugin",
    // implements IDevice and is not abstract, so DMC instantiates it during startup.
    public class RST_WInputPlugin : IDevice
    {
        public const string UN = "rst_winput";
        public const string FN = "RST Wait For Input";
        public const string DS = "RST Wait For Input (multi-input AND/OR wait with optional timeout)";

        public static RST_WInputPlugin Instance { get; private set; }

        public RST_WInputPlugin()
        {
            Instance = this;

            // Register the command in the Control tab of the DMC ribbon - that is
            // where flow-control commands (waits, conditionals) typically live.
            DMC.Helpers.AddTool(
                DMC.ToolLocation.ControlTab,
                Core.ICommand.AddCreator(typeof(WaitInputCommand), WaitInputCommand.UN, "Flow"),
                WaitInputCommand.FN);
        }

        // IDevice members ---------------------------------------------------

        public bool Connect() { return true; }
        public void Disconnect() { }
        public void Stop() { }
        public string GetName() { return FN; }
        public bool ApplySettings() { return true; }
        public bool IsConnected() { return true; }
        public bool IsEnabled() { return true; }
        public bool OnRecipeStart() { return true; }
        public void OnRecipeFinish() { }
        public IDeviceSettings GetSettings() { return null; }
        public string GetErrorMessage() { return Functions.GetLastErrorMessage(); }
    }
}
