#!/bin/sh
# Unique marker process name for H3 smoke (must not appear on Windows tasklist)
cp -f /bin/sleep /tmp/hv_sub_marker 2>/dev/null || cp -f "$(command -v sleep)" /tmp/hv_sub_marker
/tmp/hv_sub_marker 86400 &
exec python -u /opt/hvchan/channel_server.py
