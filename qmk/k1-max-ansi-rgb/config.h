#pragma once

/* Default RGB effect */
#ifdef RGB_MATRIX_DEFAULT_MODE
#    undef RGB_MATRIX_DEFAULT_MODE
#endif
#define RGB_MATRIX_DEFAULT_MODE RGB_MATRIX_TYPING_HEATMAP

/* Wireless auto-sleep timeout, seconds */
#ifdef CONNECTED_IDLE_TIME
#    undef CONNECTED_IDLE_TIME
#endif
#define CONNECTED_IDLE_TIME 3600
