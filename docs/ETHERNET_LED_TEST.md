# Ethernet LED Test

## Purpose

Verify that the Ethernet port LEDs can indicate both link modes.

## Procedure

The board switches `end0` between 100M and 1000M modes. The operator only needs to observe the Ethernet port LEDs.

## Pass Criteria

PASS if both the yellow LED and the green LED light up during the test.

FAIL if either the yellow LED or the green LED does not light up.
