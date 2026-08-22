#include QMK_KEYBOARD_H
#include "battery.h"
#include "via.h"

#define MICMUTE_BATTERY_VALUE_ID 0xB1

void via_custom_value_command_kb(uint8_t *data, uint8_t length) {
    if (length >= 4 &&
        data[0] == id_custom_get_value &&
        data[1] == id_custom_channel &&
        data[2] == MICMUTE_BATTERY_VALUE_ID) {
        data[3] = battery_get_percentage();
        return;
    }

    data[0] = id_unhandled;
}
