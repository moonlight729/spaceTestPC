# Indicator LED Hardware Framework

Current LED brightness paths:

- `/sys/class/leds/ledb/brightness`
- `/sys/class/leds/ledg/brightness`

This module only controls the output level for now.

The final production test should connect the voltage tester after each output
change and let the manage layer decide whether the measured voltage matches the
expected LED board state.
