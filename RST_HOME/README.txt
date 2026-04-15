RST_HomePlugin
==============

Sensor-based homing command for DMC.

The plugin adds one recipe command, "RST Home", in the Home tab under the
"Devices" group. The command can be placed anywhere in a recipe and will be
executed in order, like any other recipe command.

What it does
------------
1. Drives a selectable linear (or rotary) axis in the chosen direction at the
   configured search speed.
2. Stops as soon as the configured digital-input sensor becomes active (the
   active state - High or Low - is configurable).
3. If "Precise Mode" is ON, the axis is then backed off by a user-defined
   distance and re-approached at a slower "precise speed" so the final stop
   position is repeatable.
4. If "Use Emergency Sensor" is ON, a second digital input is monitored
   during the whole motion. If the emergency input triggers before the main
   homing sensor, the motion is stopped and the user can choose:
     - Error: the recipe fails with an error (default).
     - Set Variable: a user-named recipe variable is set to 1 and the
       recipe continues executing.
5. Travel is limited by a user-defined "Max Travel" to prevent crashes if
   the sensor is never reached.

Parameters
----------
* Axis                       - axis selected from the axes enabled in DMC settings.
* Search Direction           - Positive / Negative.
* Max Travel                 - safety distance (mm or deg).
* Search Speed               - coarse speed used to find the sensor.
* Precise Mode               - enables two-speed precise approach.
* Precise Speed              - slow approach speed (only when Precise Mode is ON).
* Back-off Distance          - distance to retreat before the slow approach.
* Homing Sensor              - digital input (from File -> Settings -> IO Tools).
* Sensor Active State        - High / Low digital level that means "triggered".
* Use Emergency Sensor       - turns on the optional second sensor.
* Emergency Sensor           - digital input used as emergency.
* Emergency Active State     - High / Low digital level that means "triggered".
* Emergency Action           - Error / Set variable.
* Emergency Variable Name    - recipe variable written on emergency when
                               action is "Set variable" (0 = ok, 1 = error).
* Result Position Variable   - optional: recipe variable where the axis position
                               at the final sensor hit is stored.

Build
-----
1. Remove the example references to Base.dll, Core.dll, GUI.dll and re-add
   them from your installed DMC directory
   (e.g. C:\Program Files (x86)\DMC\DMC 1.2.31).
2. In the project properties -> Build tab set Output Path to the DMC
   "Plugins" directory.
3. Build. The DLL name MUST contain the word "Plugin" (RST_HomePlugin.dll),
   otherwise DMC will not load it.
