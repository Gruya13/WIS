"""
ServisHub Test Suite
"""

import os
import sys

_SERVICEHUB_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if _SERVICEHUB_ROOT not in sys.path:
    sys.path.insert(0, _SERVICEHUB_ROOT)
