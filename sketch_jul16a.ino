/*
 * Codex status light for ESP32-C3
 * GPIO2 red, GPIO3 yellow, GPIO4 green; HIGH means on.
 * Serial protocol: 115200 8N1, one command per line.
 *
 * THINKING   one task: yellow solid
 * WORKING2   two tasks: two yellow flashes
 * WORKING3   three or more tasks: three yellow flashes
 * PERMISSION green blinking
 * COMPLETE   green solid
 * ERROR      red solid
 * OFF        all LEDs off
 * BRIGHTNESS set PWM brightness from 5 to 100 percent
 * PING       refresh heartbeat without changing state
 * IDENTIFY   return device signature
 */

#include <Arduino.h>

constexpr uint8_t RED_LED_PIN = 2;
constexpr uint8_t YELLOW_LED_PIN = 3;
constexpr uint8_t GREEN_LED_PIN = 4;
constexpr uint32_t PWM_FREQUENCY = 2000;
constexpr uint8_t PWM_RESOLUTION_BITS = 8;
constexpr unsigned long SERIAL_BAUD = 115200;
constexpr unsigned long HEARTBEAT_TIMEOUT_MS = 7000;
constexpr unsigned long PERMISSION_BLINK_MS = 400;
constexpr unsigned long TASK_FLASH_ON_MS = 180;
constexpr unsigned long TASK_FLASH_OFF_MS = 180;
constexpr unsigned long TASK_FLASH_PAUSE_MS = 900;
constexpr unsigned long SELF_TEST_STEP_MS = 220;
constexpr size_t COMMAND_BUFFER_SIZE = 32;

enum class DeviceState : uint8_t {
  WorkingOne,
  WorkingTwo,
  WorkingThreePlus,
  Permission,
  Complete,
  Error,
  Off
};

DeviceState currentState = DeviceState::Error;
unsigned long lastHeartbeatMs = 0;
unsigned long lastPatternMs = 0;
bool patternLedOn = false;
uint8_t completedTaskFlashes = 0;
char commandBuffer[COMMAND_BUFFER_SIZE];
size_t commandLength = 0;
uint8_t brightnessPercent = 100;

uint8_t brightnessDuty() {
  return static_cast<uint8_t>((static_cast<uint16_t>(brightnessPercent) * 255U + 50U) / 100U);
}

void writeLed(uint8_t pin, bool on) {
  ledcWrite(pin, on ? brightnessDuty() : 0);
}

void allLedsOff() {
  writeLed(RED_LED_PIN, false);
  writeLed(YELLOW_LED_PIN, false);
  writeLed(GREEN_LED_PIN, false);
}

void showSolid(uint8_t pin) {
  allLedsOff();
  writeLed(pin, true);
}

void applyState(DeviceState state) {
  currentState = state;
  patternLedOn = false;
  completedTaskFlashes = 0;
  lastPatternMs = millis();

  switch (state) {
    case DeviceState::WorkingOne:
      showSolid(YELLOW_LED_PIN);
      break;
    case DeviceState::WorkingTwo:
    case DeviceState::WorkingThreePlus:
      showSolid(YELLOW_LED_PIN);
      patternLedOn = true;
      completedTaskFlashes = 0;
      break;
    case DeviceState::Permission:
      showSolid(GREEN_LED_PIN);
      patternLedOn = true;
      break;
    case DeviceState::Complete:
      showSolid(GREEN_LED_PIN);
      break;
    case DeviceState::Error:
      showSolid(RED_LED_PIN);
      break;
    case DeviceState::Off:
      allLedsOff();
      break;
  }
}

void acknowledge(const char *command) {
  Serial.print("OK ");
  Serial.println(command);
}

void handleCommand(char *command) {
  while (*command == ' ' || *command == '\t') ++command;
  size_t length = strlen(command);
  while (length > 0 && (command[length - 1] == ' ' || command[length - 1] == '\t')) {
    command[--length] = '\0';
  }
  for (size_t i = 0; i < length; ++i) {
    if (command[i] >= 'a' && command[i] <= 'z') {
      command[i] = static_cast<char>(command[i] - 'a' + 'A');
    }
  }

  if (strcmp(command, "IDENTIFY") == 0) {
    lastHeartbeatMs = millis();
    Serial.println("CODEX_STATUS_LIGHT:4");
  } else if (strcmp(command, "PING") == 0) {
    lastHeartbeatMs = millis();
    acknowledge("PING");
  } else if (strcmp(command, "THINKING") == 0 ||
             strcmp(command, "WORKING") == 0 ||
             strcmp(command, "WORKING1") == 0) {
    lastHeartbeatMs = millis();
    applyState(DeviceState::WorkingOne);
    acknowledge("THINKING");
  } else if (strcmp(command, "WORKING2") == 0) {
    lastHeartbeatMs = millis();
    applyState(DeviceState::WorkingTwo);
    acknowledge("WORKING2");
  } else if (strcmp(command, "WORKING3") == 0 ||
             strcmp(command, "WORKING3PLUS") == 0) {
    lastHeartbeatMs = millis();
    applyState(DeviceState::WorkingThreePlus);
    acknowledge("WORKING3");
  } else if (strcmp(command, "PERMISSION") == 0 || strcmp(command, "WAITING") == 0) {
    lastHeartbeatMs = millis();
    applyState(DeviceState::Permission);
    acknowledge("PERMISSION");
  } else if (strcmp(command, "COMPLETE") == 0 || strcmp(command, "IDLE") == 0) {
    lastHeartbeatMs = millis();
    applyState(DeviceState::Complete);
    acknowledge("COMPLETE");
  } else if (strcmp(command, "ERROR") == 0) {
    lastHeartbeatMs = millis();
    applyState(DeviceState::Error);
    acknowledge("ERROR");
  } else if (strcmp(command, "OFF") == 0) {
    lastHeartbeatMs = millis();
    applyState(DeviceState::Off);
    acknowledge("OFF");
  } else if (strncmp(command, "BRIGHTNESS ", 11) == 0) {
    char *end = nullptr;
    const long requested = strtol(command + 11, &end, 10);
    while (end != nullptr && (*end == ' ' || *end == '\t')) ++end;
    if (requested < 5 || requested > 100 || end == command + 11 ||
        (end != nullptr && *end != '\0')) {
      Serial.println("ERR BRIGHTNESS_RANGE 5-100");
    } else {
      brightnessPercent = static_cast<uint8_t>(requested);
      lastHeartbeatMs = millis();
      applyState(currentState);
      Serial.print("OK BRIGHTNESS ");
      Serial.println(brightnessPercent);
    }
  } else if (strcmp(command, "BRIGHTNESS?") == 0) {
    lastHeartbeatMs = millis();
    Serial.print("BRIGHTNESS ");
    Serial.println(brightnessPercent);
  } else if (length > 0) {
    Serial.print("ERR UNKNOWN_COMMAND ");
    Serial.println(command);
  }
}

void readSerialCommands() {
  while (Serial.available() > 0) {
    const char incoming = static_cast<char>(Serial.read());
    if (incoming == '\n' || incoming == '\r') {
      if (commandLength > 0) {
        commandBuffer[commandLength] = '\0';
        handleCommand(commandBuffer);
        commandLength = 0;
      }
      continue;
    }
    if (commandLength < COMMAND_BUFFER_SIZE - 1) {
      commandBuffer[commandLength++] = incoming;
    } else {
      commandLength = 0;
      Serial.println("ERR COMMAND_TOO_LONG");
    }
  }
}

void updatePermissionBlink(unsigned long now) {
  if (currentState != DeviceState::Permission ||
      now - lastPatternMs < PERMISSION_BLINK_MS) {
    return;
  }
  lastPatternMs = now;
  patternLedOn = !patternLedOn;
  allLedsOff();
  writeLed(GREEN_LED_PIN, patternLedOn);
}

void updateTaskFlashPattern(unsigned long now) {
  uint8_t targetFlashes = 0;
  if (currentState == DeviceState::WorkingTwo) {
    targetFlashes = 2;
  } else if (currentState == DeviceState::WorkingThreePlus) {
    targetFlashes = 3;
  } else {
    return;
  }

  if (patternLedOn) {
    if (now - lastPatternMs < TASK_FLASH_ON_MS) return;
    lastPatternMs = now;
    patternLedOn = false;
    ++completedTaskFlashes;
    allLedsOff();
    return;
  }

  const unsigned long pause = completedTaskFlashes >= targetFlashes
      ? TASK_FLASH_PAUSE_MS
      : TASK_FLASH_OFF_MS;
  if (now - lastPatternMs < pause) return;

  if (completedTaskFlashes >= targetFlashes) {
    completedTaskFlashes = 0;
  }
  lastPatternMs = now;
  patternLedOn = true;
  allLedsOff();
  writeLed(YELLOW_LED_PIN, true);
}

void updatePatterns(unsigned long now) {
  updatePermissionBlink(now);
  updateTaskFlashPattern(now);
}

void runSelfTest() {
  showSolid(RED_LED_PIN);
  delay(SELF_TEST_STEP_MS);
  showSolid(YELLOW_LED_PIN);
  delay(SELF_TEST_STEP_MS);
  showSolid(GREEN_LED_PIN);
  delay(SELF_TEST_STEP_MS);
  allLedsOff();
}

void setup() {
  ledcAttach(RED_LED_PIN, PWM_FREQUENCY, PWM_RESOLUTION_BITS);
  ledcAttach(YELLOW_LED_PIN, PWM_FREQUENCY, PWM_RESOLUTION_BITS);
  ledcAttach(GREEN_LED_PIN, PWM_FREQUENCY, PWM_RESOLUTION_BITS);
  allLedsOff();
  Serial.begin(SERIAL_BAUD);
  runSelfTest();
  lastHeartbeatMs = millis();
  applyState(DeviceState::Error);
  Serial.println("CODEX_STATUS_LIGHT:4");
}

void loop() {
  readSerialCommands();
  const unsigned long now = millis();
  if (currentState != DeviceState::Error && now - lastHeartbeatMs >= HEARTBEAT_TIMEOUT_MS) {
    applyState(DeviceState::Error);
    Serial.println("ERR HEARTBEAT_TIMEOUT");
  }
  updatePatterns(now);
}
