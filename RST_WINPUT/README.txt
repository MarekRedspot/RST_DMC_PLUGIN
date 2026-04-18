RST_WInputPlugin
================

"Wait For Input" recipe command for DMC.

The plugin adds one recipe command, "RST Wait For Input", in the Control
tab under the "Flow" group. The command blocks recipe execution until the
configured combination of digital inputs reaches the expected states.

What it does
------------
1. Reads the state of up to 8 digital-input presets (configured in
   File -> Settings -> IO Tools).
2. Each input has its own expected state (High / Low).
3. When more than one input is used, the user chooses how to combine them:
     - AND: wait until every input matches its expected state.
     - OR:  wait until at least one input matches.
4. An optional timeout aborts the wait:
     - Error (default): recipe fails with an error.
     - Set Variable: a user-named recipe variable is set to 1 and the
       recipe continues. On success, the same variable is set to 0.

Parameters
----------
* Number of Inputs          - how many input slots to use (1-8).
* Logic                     - AND / OR (hidden when only one input).
* Use Timeout               - enable the abort-on-timeout branch.
* Timeout (s)               - seconds after which the wait gives up.
* Timeout Action            - Error / Set variable.
* Timeout Variable Name     - recipe variable written on timeout / success
                              when action is "Set variable".
* Input N                   - digital-input preset for slot N.
* Input N State             - High / Low level that "matches" for slot N.

Build
-----
1. Remove the example references to Base.dll, Core.dll, GUI.dll and re-add
   them from your installed DMC directory
   (e.g. C:\Program Files (x86)\DMC\DMC 1.2.31).
2. In the project properties -> Build tab set Output Path to the DMC
   "Plugins" directory.
3. Build. The DLL name MUST contain the word "Plugin" (RST_WInputPlugin.dll),
   otherwise DMC will not load it.
