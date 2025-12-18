/*
  this code provide motor move in mach3 z axis
  Usage samples:
  init z axis with 240 steps per mm, no inverse move, init position=0 steps, motor speed 150mm/min, min value = -15mm(z axis plunged), max value = 15mm(z axis most top position)
  SETAXIS;Z;240;false;150;0;200;-15;15;
  move to 1200 steps:
  MOVE;1200;;Z;
  get cur position in steps:
  GETPOS;Z;

  Wire Schema for step motor with dm556 controller:
  PUL- to GND
  PUL+ to Digital Pin 8
  DIR+ to Digital Pin 9
*/
#include <AccelStepper.h>
const int pulsePin = 8; // Connect to PUL+
const int dirPin = 9;   // Connect to DIR+
const int pulsePin2 = 6;
const int dirPin2 = 7;  
const int pulsePin3 = 4;
const int dirPin3 = 5;  
//const int enablePin = 10; // Connect to ENA+, ENA pin always HIGH

#define STATE_WAITSERIALPORT 0
#define STATE_ROTATESTEPPER 1
#define STATE_OTHER 2
#define MIN_DISTANCENOTIFY 1//filter cur position notifications by min distance in steps

#define DEBOUNCE_DELAY 100   // the debounce time; increase if the output flickers
#define JS_CHECKJOYSTICKINTERVAL 100 //time interval for checking joystick status(other time used for generate move step pulses)
#define JS_NOTMOVESTATE 512//
#define VRX_PIN A2 // Arduino pin connected to VRX pin of joystick
#define VRY_PIN A3 // Arduino pin connected to VRY pin of joystick
#define SW_PIN 3  // Arduino pin connected to SW  pin of joystick

class Mach3Axis
{
  public:
    Mach3Axis()
    {
      this->curPos = 0;
      this->disabled = true;
      this->curMotorAccel = 0;
      this->distanceCounter = 0;
      this->curPosNotifyPeriod = 0;
      this->reportCurPosLastTime = 0;
      this->stepper = NULL;
    }

  public:
    char axis;
    bool disabled;
    //int axisIndex;
    int stepsPerMm;
    bool inverseMove;
    int curPos;//current stepper position in steps;
    int curMotorAccel;
    int curMotorSpeed;
    int curMotorSpeed2;
    int minAxisValue;
    int maxAxisValue;
    unsigned int distanceCounter;
    int curPosNotifyPeriod;//period of new position notification in move command
    unsigned long reportCurPosLastTime;
    AccelStepper* stepper;
};

//2 axis joystick kj-23 not always worked properly on full scale 0..1023. some value ranges will be ignored
struct JoystickExcludedRange
{
  char axis;    //'x' or 'y'
  int axisexcludedstart;//excluded range start
  int axisexcludedend;//excluded range end
};

class Joystick
{
  public:
    Joystick(int vrxPin, int vryPin, int btnPin)
    {
      this->vrxPin = vrxPin;
      this->vryPin = vryPin;
      this->btnPin = btnPin;
      enableJoystick = false;
      enableJoystickPushButton = false;
      joystickLoop = false;
      excludeZeroValue = true;
      useOnlyX = useOnlyY = false;
      moveSpeed = 15;
      moveSpeedMultiplier = 1;
      maxMoveSpeedMultiplier = 8;
      switchMode = false;
      curMoveDirection = 0;
      axisDelta = 100;
      excludedAxisRangeCount = 0;
      lastCheckJoystick = 0;
      axisTraceTimeout = 0;
    }

    void SetMovedAxis(Mach3Axis* axis)
    {
      movedAxis = axis;
    }

    void TraceJoystick()
    {
#ifdef DEBUG
      //print x,y axis if joystick is active
      if ((millis() - axisTraceStart2) > 500/* ||  xValueActive || yValueActive*/)
      {
        Serial.print("pushbutton=disabled");
        //Serial.print(currentState);
        if (xValueActive)
          Serial.print(", move x axis, ");
        if (yValueActive)
          Serial.print(", move y axis, ");
        Serial.print("x = ");
        Serial.print(xValue);
        Serial.print(", y = ");
        Serial.println(yValue);
        axisTraceStart2 = millis();
      }
#endif
    }

    void RotateMotorByJoystick(int endLocation, int motorSpeedInSteps, Mach3Axis* axisSet)
    {
      Mach3Axis* axis = axisSet == NULL ? movedAxis : axisSet;
      int stepsPos = axis->stepper->currentPosition();

      if (stepsPos != endLocation)
      {
#ifdef DEBUG
        Serial.print("axis ");
        Serial.print(axis->axis);
        Serial.print(", cur pos=");
        Serial.println(axis->stepper->currentPosition());
        Serial.print(", speed ");
        Serial.println(motorSpeedInSteps);
        Serial.print(", location= ");
        Serial.println(endLocation);
        Serial.print(", dir=");
        Serial.println(curMoveDirection);
#endif
        axis->stepper->moveTo(endLocation);
        axis->stepper->setSpeed(motorSpeedInSteps);
        axis->stepper->runSpeedToPosition();
      }
    }

    //read joystick X,Y and filter these values by rules
    void GetJoyStickXYValues()
    {
      int x, y;
      x = vrxPin == -1 ? JS_NOTMOVESTATE : analogRead(vrxPin);
      y = vryPin == -1 ? JS_NOTMOVESTATE : analogRead(vryPin);

      xValue = useOnlyY ? JS_NOTMOVESTATE : x;
      yValue = useOnlyX ? JS_NOTMOVESTATE : y;

      if (excludeZeroValue && xValue == 0)
      {
        xValue = JS_NOTMOVESTATE;
      }
      if (excludeZeroValue && yValue == 0)
      {
        yValue = JS_NOTMOVESTATE;
      }

      for (int i = 0 ; i < excludedAxisRangeCount; i++)
      {
        JoystickExcludedRange jer = excludedAxis[i];
        if (jer.axis == 'x' && (xValue > jer.axisexcludedstart && xValue < jer.axisexcludedend))
          xValue = JS_NOTMOVESTATE;
        else if (jer.axis == 'y' && (yValue > jer.axisexcludedstart && yValue < jer.axisexcludedend))
          yValue = JS_NOTMOVESTATE;
      }
      xValueActive =  xValue  < (JS_NOTMOVESTATE - axisDelta) || xValue > (JS_NOTMOVESTATE + axisDelta);
      yValueActive =  yValue  < (JS_NOTMOVESTATE - axisDelta) || yValue > (JS_NOTMOVESTATE + axisDelta);
#ifdef DEBUG
      //print x,y axis for diagnostics
      if (axisTraceTimeout > 0 && (millis() - axisTraceStart) < axisTraceTimeout) //||  xValueActive || yValueActive)
      {
        TraceJoystick();
      }
#endif

      if (xValueActive && yValueActive)
      {
        xValueActive = abs(JS_NOTMOVESTATE - xValue) > abs(JS_NOTMOVESTATE - yValue);
        yValueActive = !xValueActive;
      }
    }

    void JoystickLoop()
    {
      if (enableJoystick && movedAxis != NULL)
      {
        unsigned long moveTime = 0;
        moveSpeedMultiplier = 1;
        float baseMoveSpeed = moveSpeed * (movedAxis->stepsPerMm / 60);
        float curMoveSpeed = baseMoveSpeed;
        int newMoveDirection;
        int axisValue;
        curMoveDirection = newMoveDirection = 0;
        int startLocation = movedAxis->minAxisValue;
        int endLocation = movedAxis->maxAxisValue;
        int curLocation = startLocation;
        int countRunSpeed = 1, i;
        bool rotated = false;
        joystickLoop = true;
        
        while (joystickLoop)
        {
          //handle joystick push button
          // read analog X and Y analog values
          GetJoyStickXYValues();
          lastCheckJoystick = millis();

          //rotate motor by joystick
          if (xValueActive || yValueActive)
          {
            int dir = axisValue <= JS_NOTMOVESTATE ? -1 : 1;
            axisValue = xValueActive ? xValue : yValue;
            newMoveDirection = axisValue > JS_NOTMOVESTATE ? 1 : -1;

            curMoveSpeed = (float)(baseMoveSpeed * moveSpeedMultiplier) * abs((float)(axisValue - JS_NOTMOVESTATE) / (float)JS_NOTMOVESTATE);
            if (curMoveDirection != newMoveDirection)
            {
              moveTime = millis();
              curMoveDirection = newMoveDirection;
              moveSpeedMultiplier = 1;
              float speedPercentage = switchMode ? 1 : abs((float)(axisValue - JS_NOTMOVESTATE) / (float)JS_NOTMOVESTATE);
              curMoveSpeed = (float)(baseMoveSpeed * moveSpeedMultiplier) * speedPercentage;
              curLocation = curMoveDirection == 1 ? endLocation : startLocation;
              RotateMotorByJoystick(curLocation, curMoveSpeed, NULL);
              rotated = true;
            }
            else if ((millis() - moveTime) > 2000)
            {
              moveTime = millis();
              moveSpeedMultiplier = min(8, moveSpeedMultiplier + 1);
              curMoveSpeed = (float)(baseMoveSpeed * moveSpeedMultiplier) * abs((float)(axisValue - JS_NOTMOVESTATE) / (float)JS_NOTMOVESTATE);
              RotateMotorByJoystick(curLocation, curMoveSpeed, NULL);
            }
            if (!rotated)
            {
              moveTime = millis();
              curMoveDirection = newMoveDirection;
              moveSpeedMultiplier = 1;
              curMoveSpeed = (float)(baseMoveSpeed * moveSpeedMultiplier) * abs((float)(axisValue - JS_NOTMOVESTATE) / (float)JS_NOTMOVESTATE);
              curLocation = curMoveDirection == 1 ? endLocation : startLocation;
              RotateMotorByJoystick(curLocation, curMoveSpeed, NULL);
              rotated = true;
            }
            if (axisTraceTimeout == 0)//in joystick diagnostics mode motor not rotated
            {
              for (int i = 0; i < 10; i++)
              {
                if (abs(movedAxis->stepper->currentPosition()) >= abs(curLocation))
                {
                  joystickLoop = false;
                  i = 10;
                }
                else if (i == 9)
                {
                  if  ((millis() - lastCheckJoystick) > JS_CHECKJOYSTICKINTERVAL)
                  {
                    i = 10;
                  }
                  else
                    i = 0;
                }
                movedAxis->stepper->runSpeed();
              }
            }
          }
          else//joystick in iddle state
          {
            joystickLoop = false;
          }
        }
      }
    }

  public:
    int axisTraceTimeout = 30000;//set trace duration for joystick, cheap 2 axis ks-23 joysticks always needed diagnostic/calibration before use
    unsigned long axisTraceStart = 0;
    unsigned long axisTraceStart2 = 0;
    bool enableJoystick;//enable joystick
    bool enableJoystickPushButton;//enable joystick push button
    bool joystickLoop;
    unsigned long lastCheckJoystick;
    //joystick 2 axis potentiometer settings:
    bool excludeZeroValue;//if joystick has broken contact with arduino then arduino board read joystick axis like 0 analogue value
    bool useOnlyX, useOnlyY;//true if use only one axis, other axis can be broken or worked with problems. One joystick move one axis only
    float moveSpeed;//motor speed with 100% joystick axis value(greater value for more fast motor rotate). measured in mm(stepper) or micros(servo)
    float moveSpeedMultiplier;//move speed increased by time passed when axis pressed in one direction
    float maxMoveSpeedMultiplier;    
    int curMoveDirection;
    bool switchMode;//true when joystick use only direction of axis with always 100% value, axis value percentage is igonored
    int axisDelta;//in motionless state axis show changes between 485 and 522(because potentiometer voltage not constant)
    int xValue, yValue;//x,y axis for two joysticks stored like 0-1023
    bool xValueActive, yValueActive;
    int excludedAxisRangeCount = 1;
    int vrxPin, vryPin, btnPin;
    JoystickExcludedRange excludedAxis[1] = { JoystickExcludedRange { 'x', -1, -1 } };
    Mach3Axis* movedAxis;
    // joystick push button variables:
    //    int btnLastSteadyState = LOW;       // the previous steady state from the input pin
    //    int btnLastFlickerableState = LOW;  // the previous flickerable state from the input pin
    //    int currentState;
    //    unsigned long btnLastDebounceTime = 0;  // the last time the output pin was toggled
    //    unsigned long btnLastPressBtnTime = 0;  // the last time the button was pressed
    //    unsigned long btnLastReleaseBtnTime = 0;  // the last time the button was released
    //    unsigned long btnLastReleaseBtnTime2 = 0;  // the last time the button was released
    //    unsigned long btnLastReleasedButtonTimeSpan = 0;//button pressing time
    //    int btnCcountClicks = 0;//count first joystick push button clicks
    //    unsigned long lastTime = 0;
};

AccelStepper stepper1(AccelStepper::DRIVER, pulsePin, dirPin);
Mach3Axis as1 = Mach3Axis();
Joystick joystick(VRX_PIN, VRY_PIN, SW_PIN);

Mach3Axis* axises[1] = { &as1 };
int axisCount = 1;//there used only z axis
Mach3Axis* curAxis;
String startCommands[] = { "SETAXIS;Z;240;false;150;0;200;-10;30;", "SETJS;true;Z;1;true;10;10;false;15;" };
int startCommandsCount = 2;
bool notWriteResponse;

String curCommand;
int current_appState = STATE_WAITSERIALPORT;
unsigned long lastCheckSerialPort = 0;
unsigned long curTime = 0;

void setup() {
  Serial.begin(115200);
  axises[0]->axis = 'Z';
  axises[0]->disabled = false;
  axises[0]->stepsPerMm = 240;
  axises[0]->curMotorSpeed = 150;
  axises[0]->curMotorSpeed2 = 600;
  axises[0]->stepper = &stepper1;
  axises[0]->stepper->setMaxSpeed(1000.0);
  axises[0]->stepper->setSpeed(600);
  curAxis = axises[0];

  joystick.enableJoystick = false;
  joystick.SetMovedAxis(axises[2]);

  pinMode(pulsePin, OUTPUT);
  pinMode(dirPin, OUTPUT);
  pinMode(pulsePin2, OUTPUT);
  pinMode(dirPin2, OUTPUT);
  pinMode(pulsePin3, OUTPUT);
  pinMode(dirPin3, OUTPUT);

  notWriteResponse = true;

  for (int i = 0; i < startCommandsCount; i++)
  {
    curCommand = startCommands[i];
    HandleCommand();
  }

  notWriteResponse = false;
}

int GetAxisIndex(char axis)
{
  for (int i = 0; i < axisCount; i++)
  {
    if (axises[i]->axis == axis)
      return i;
  }

  return -1;
}

String splitString(String data, char separator, int index)
{
  int found = 0;
  int strIndex[] = {0, -1};
  int maxIndex = data.length() - 1;

  for (int i = 0, j = 0; i <= maxIndex; i++)
  {
    bool isSeparator = data.charAt(i) == separator;
    if (isSeparator)
    {
      if (j == index)
      {
        strIndex[1] = i;
        if (strIndex[0] < strIndex[1])
        {
          return data.substring(strIndex[0], strIndex[1]);
        }
        else
          return "";
      }

      j++;
      strIndex[0] = i + 1;
    }
    if (i == maxIndex)
    {
      if (j == index)
      {
        strIndex[1] = i;

        if (strIndex[0] < strIndex[1])
        {
          return data.substring(strIndex[0], strIndex[1] + 1);
        }
        else
          return "";
      }
    }
  }

  return "";
}

void HandleCommand()
{
  String commandName = splitString(curCommand, ';', 0);
  if (commandName == "STOP")
  {
    String axis = splitString(curCommand, ';', 1);
    if (axis != "")
    {
      axises[GetAxisIndex(axis[0])]->stepper->stop();
    }
    else
    {
      for (int i = 0; i < axisCount; i++)
      {
        axises[i]->stepper->stop();
      }
    }

    current_appState = STATE_WAITSERIALPORT;
    if (!notWriteResponse)
      Serial.println("OK");
  }
  else if (current_appState == STATE_WAITSERIALPORT)
  {
    if (commandName == "MOVE")
    {
      String axis = splitString(curCommand, ';', 3);
      if (axis != "")
      {
        curAxis = axises[GetAxisIndex(axis[0])];
      }

      int newPos = splitString(curCommand, ';', 1).toInt();
      String moveSpeed = splitString(curCommand, ';', 2);
      if (newPos == curAxis->stepper->currentPosition())
      {
        if (!notWriteResponse)
          Serial.println("OK");
      }
      else
      {
        int motorSpeedInSteps = curAxis->curMotorSpeed2;
        if (moveSpeed != "" && moveSpeed != "0")
        {
          int imoveSpeed = moveSpeed.toInt();
          curAxis->curMotorSpeed = imoveSpeed;
          motorSpeedInSteps = imoveSpeed * (curAxis->stepsPerMm / 60);//braces there for prevent int overflow from multiple imoveSpeed * curAxis
          curAxis->curMotorSpeed2 = motorSpeedInSteps;
        }

        curAxis->distanceCounter = 0;
        curAxis->reportCurPosLastTime = 0;
        curAxis->stepper->moveTo(newPos);
        curAxis->stepper->setSpeed(motorSpeedInSteps);
        curAxis->stepper->runSpeedToPosition();//SET CW/CCW direction
        current_appState = STATE_ROTATESTEPPER;
      }
    }
    else if (commandName == "GETPOS")
    {
      int curPos;
      String axis = splitString(curCommand, ';', 1);
      if (axis != "")
      {
        curPos = axises[GetAxisIndex(axis[0])]->stepper->currentPosition();
      }
      else
        curPos = curAxis->stepper->currentPosition();

      String response = "CURPOS=";
      response += curPos;
      if (!notWriteResponse)
        Serial.println(response);
    }
    else if (commandName == "SETAXIS")
    {
      String axis = splitString(curCommand, ';', 1);
      if (axis == "")
      {
        if (!notWriteResponse)
          Serial.println("AXISNOTSET");
      }
      else
      {
        //SETAXIS;Z;240;false;150;0;200;-15;15;
        curAxis = axises[GetAxisIndex(axis[0])];
        if (splitString(curCommand, ';', 2) != "")
        {
          int stepsPerMm = splitString(curCommand, ';', 2).toInt();
          bool inverseMove = splitString(curCommand, ';', 3) == "true";
          int curMotorSpeed = splitString(curCommand, ';', 4).toInt();
          int curMotorSpeed2 = (int)(curMotorSpeed * (stepsPerMm / 60));//braces there for prevent overflow from multiple curMotorSpeed * stepsPerMm
          int curPosInSteps = splitString(curCommand, ';', 5).toInt();
          int curPosNotifyPeriod = splitString(curCommand, ';', 6).toInt();
          curAxis->stepsPerMm = stepsPerMm;
          curAxis->inverseMove = inverseMove;
          curAxis->curMotorSpeed = curMotorSpeed;
          curAxis->curMotorSpeed2 = curMotorSpeed2;
          curAxis->curPos = curPosInSteps;
          curAxis->curPosNotifyPeriod = curPosNotifyPeriod;
          curAxis->stepper->setCurrentPosition(curPosInSteps);
          curAxis->stepper->setPinsInverted(inverseMove, false, false);
          if (splitString(curCommand, ';', 7) != "")
          {
            curAxis->minAxisValue = splitString(curCommand, ';', 7).toFloat() * curAxis->stepsPerMm;
            curAxis->maxAxisValue = splitString(curCommand, ';', 8).toFloat() * curAxis->stepsPerMm;
          }
        }

        if (!notWriteResponse)
          Serial.println("OK");
      }
    }
    else if (commandName == "SETJS")
    { //SETJS;Z;true;2;true;10;10;true;15;- settings for joystick
      joystick.enableJoystick = splitString(curCommand, ';', 1) == "true";
      if (joystick.enableJoystick)
      {
        String defaultJoystickAxis = splitString(curCommand, ';', 2);
        joystick.SetMovedAxis(defaultJoystickAxis != "" ? axises[GetAxisIndex(defaultJoystickAxis[0])] : NULL);
        joystick.useOnlyX = splitString(curCommand, ';', 3) == "1";
        joystick.useOnlyY = splitString(curCommand, ';', 3) == "2";
        joystick.useOnlyX = !joystick.useOnlyX && !joystick.useOnlyY ? true : joystick.useOnlyX;
        joystick.excludeZeroValue = splitString(curCommand, ';', 4) == "true";
        joystick.moveSpeed = splitString(curCommand, ';', 5).toFloat();
        joystick.maxMoveSpeedMultiplier = splitString(curCommand, ';', 6).toFloat();
        joystick.switchMode = splitString(curCommand, ';', 7) == "true";
#ifdef DEBUG
        String traceTimeout = splitString(curCommand, ';', 8);
        joystick.axisTraceTimeout = traceTimeout == "" ? 0 : traceTimeout.toInt() * 1000;
        if (joystick.axisTraceTimeout != 0)
        {
          joystick.axisTraceStart = joystick.axisTraceStart2 = millis();
        }
#endif
      }

      if (!notWriteResponse)
        Serial.println("OK");
    }
  }
}

void loop()
{
  if (current_appState == STATE_ROTATESTEPPER && (millis() - lastCheckSerialPort) < 100)
  {
    curAxis->stepper->runSpeed();//only one motor rotated at same time
    int distance = curAxis->stepper->distanceToGo();
    if (distance == 0)
    {
      Serial.println("OK");
      current_appState = STATE_WAITSERIALPORT;
    }
    else
    {
      curTime = millis();

      if (curAxis->curPosNotifyPeriod > 0)
      {
        if (curAxis->distanceCounter == 0)
        {
          curAxis->distanceCounter = distance;
        }
        else if ((max(curAxis->distanceCounter, distance) - min(curAxis->distanceCounter, distance)) > (MIN_DISTANCENOTIFY - 1))
        {
          curAxis->distanceCounter = distance;
          if ((curTime - curAxis->reportCurPosLastTime) > curAxis->curPosNotifyPeriod)
          {
            curAxis->curPos = curAxis->stepper->currentPosition();
            String response = "CURPOS=";
            response += curAxis->curPos;
            Serial.println(response);
            curAxis->reportCurPosLastTime = millis();
          }
        }
      }
    }
  }
  else
  {
    if (Serial.available()) // if there is data comming
    {
      curCommand = Serial.readStringUntil('\n'); // read string until meet newline character
      HandleCommand();
    }

    if (current_appState != STATE_ROTATESTEPPER)
    {
      joystick.JoystickLoop();
    }

    lastCheckSerialPort = millis();
  }
}