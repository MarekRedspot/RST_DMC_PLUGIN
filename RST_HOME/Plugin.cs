using System;
using Base;
using Core;

namespace RST_HomePlugin
{
    // Plugin class. Class name is public, contains the word "Plugin",
    // implements IDevice and is not abstract, so DMC instantiates it during startup.
    public class RST_HomePlugin : IDevice
    {
        public const string UN = "rst_home";
        public const string FN = "RST Home";
        public const string DS = "RST homing command (sensor based axis referencing)";

        public static RST_HomePlugin Instance { get; private set; }

        public RST_HomePlugin()
        {
            Instance = this;

            // Register the homing command in the Home tab of DMC ribbon.
            // User can drag/drop or click the tool to add the command to the recipe.
            DMC.Helpers.AddTool(
                DMC.ToolLocation.HomeTab,
                Core.ICommand.AddCreator(typeof(HomeCommand), HomeCommand.UN, "Devices"),
                HomeCommand.FN);
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
