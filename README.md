Mach3 Z axis plugin

Used hardware: 

-Arduino R3 ch340 board, 

-Usb cnc motion controller RNR 2, 4 axis(10$)

-3 dm556 step motor drivers and 3 nema 23 step motors from cnc router

-one joystick ky-023

Software Requirements: windows 10(versions 7,8,11 not tested), .net 4.8, mach3(i tested version mach3.41). IDE development: vs2022 

This plugin allow add new axis with arduino commands or replace existing cnc axis(x,y,z,s,etc) to arduino commands in gcode under mach3 gcode engine. Replaced gcode commands – g1,g0,move, on arduino these commands executed like linear move without any interpolation, z axis better suit for this .  

All axis defined in settings.xml file. Description for each xml field located in file /GCodeConverter/settings-description.xml

Project consist from 2 parts - GCode converter which replace mach3 gcode to arduino commands and Activex control which mediate between mach3 and arduino controller, allowing run arduino code in mach3 macroses m3, m4, m5. Thesese macroses generated automatically, their content depends on xml configuration file. Activex control also allow execute modbus commands via serial port usb rj45.

Workflow usage:

1)Put program GcodeConverter.exe with activex dll and settings.xml in  Mach3 subfolder GCConverter, for example C:\Mach3\GCConverter

2)Register activex, on windows 10 it with commandline:

cd "C:\Mach3\GCConverter"
C:\Windows\Microsoft.NET\Framework\v4.0.30319\regasm.exe /codebase /tlb SerialPortAxControl.dll

Replace path in command line if they differ. Command line must be launched with admin rights

3)Change xml file which described all replaced or new axis , it can be any axis , but Z axis better suit for this, because in many cases it don’t need interpolation or co-ordinated movement with other axis.

4)Backup mach3 subfolder macros, for rollback changes,if some goes wrong. It is important if folder contain rewritten m3,m4,m5 macros

5)Run program GcodeConverter.exe, generate macros file and add them in Mach3 macros folder, macros path depends on mach3 profile, for Mach3Mill profile it will be  \macros\Mach3Mill. In this case old M3/M4/M5 macroses will be overwritten. They can be restored from backup from p. 4. Macroses generated only once, minor changes like com port in M4 can be changed manually.There are used 3 macroses

M3 – most used macros for linear move axis, each m3 call has 1-3 prefix commands. For example Gcode Z2.300F100 will be converted to 

#15=0

#16=2.300

#17=100

M3
 
 theses prefix # commands needed for parametrize m3 macros:

#15=0 - setup current moved axis (this line not need if xml file define only single replaced axis), 

#16=2.300 - setup new location where to move

#17=100 - setup new feed speed of this movement 

Prefix commands can operate with only numeric values, Converted gcode can has plenty of # commands but they work very fast and don’t influence on performance

M4-macros where initialization code located. Must executed in begin of gcode. This macros perform next steps

-Create activex control

-Open connection with arduino board

-Disable joystick

-Initialize mach3 variables # with index 1-30+

Sample of use M4 in gcode:

1)

#22=1

M5

#1=0

M4

#22=1 mean that this plugin switch on, after this macroses m3m4m5 run plugin code. Switch #22=1 needed to be toggled once during mach3 session
#1=0 mean that first axis with index=0 has zero start position. 

2)

#21=1

M5

#21=1 mean that next M5 commands will execute it default code, ie stop spindle.

M5 – stop spindle

In mach3 diagnostics window macros m4 used for enable/disable joystick with next commands:

Enable joystick with first axis:

#22=1 – if plugin not was enabled earlier in mach3 session

#19=1 : #19=1 – joystick enabled, #19=2 – joystick disabled

M4  - enable joystick

Disable joystick:

#19=2

M4

Switch to 3-rd axis moved by joystick:

#19=1 

#20=2

M4

axis index vary from 0 to axis count - 1 

Show current axis coord in popup window:

#15=0 – mean that current axis is first axis. It command is unnecessary if axis only single

#18=1 – mean that next m4 command show popup with current axis coord

M4

Board Arduino R3 ch340 can has problems with setup connection in Windows 10, so in m4 i added popup 'Ready to move' when connection was setup succesfully. this popup can be removed from macros m4 manually, if there no problems with connection

M5 - close serial port connection, remove activex cache

If there problems with serial port connection then each time before use plugin in mach3 diagnostics window needed to run 2 commands : 

#22=1

m4

If all worked then popup 'Ready to move' will be shown. Possible problems with runnng these 2 commands:

1) Activex registration failed. for registration activex in windows 10 run next commandline with admin rights

cd "C:\Mach3\GCConverter"
C:\Windows\Microsoft.NET\Framework\v4.0.30319\regasm.exe /codebase /tlb SerialPortAxControl.dll

2)Cant setup connection to serial port:

Install last driver ch341 for arduino board. Url to manufacturer site: 

https://www.wch-ic.com/downloads/CH341SER_ZIP.html

In mach3 diagnostics window attempt use m5 and then m4. if it is not worked then restart mach3, attempt connect to arduino via arduino ide, serial port monitor or other  program. Also possible that windows can busy arduino serial port in this case needed OS restart.

6)Convert original gcode file to gcode with arduino commands. 

Start command like 

#22=1

#1=0

M5

M4

must be located  in begin gcode.

Restore axis after Mach3 Estop 

 If Estop occurred then remember last line number and plugin axis current value, for example -3.5mm. If Estop occurred in plugin code on M3 line then last line number must be first prefix command for this M3.  Current plugin axis can be obtained with 3 methods:

-M4 command in diagnostics window with 2 prefixes #15=0  #18=1 

-DRO window which configured in settings.xml

-Log file GCCInfo.txt. 

First method not always worked.

Then restart mach3 and in diagnostics window input next commands

#22=1

#1=-3.5

M4

OR set axis position with joystick to some calibrated value for example to zero.And run code in diagnostics:

#22=1

M4

After popup ‘ready to move’ run gcode from last line number

Joystick ky-23

Arduino controller located in project GCodeConverter.

For diagnostics joystick in arduino controller needed add line in begin of .ino file:

#define DEBUG

after diagnostics joystick ready to rotate motors. In this case line #define DEBUG must be removed and last parameter(trace timeout) in joystick command needed to be set to zero, ie arduino joystick command will be :"SETJS;true;Z;1;true;10;10;false;0;"


Possible modifications:

Add for z axis touch sensor. Instead of use joystick

Add servo motor axis, for example for embedding 4dof robotarm in gcode

Create very simple Arduino cnc gcode controller, which working with mach3 without usb cnc controller

Laser axis

Etc






