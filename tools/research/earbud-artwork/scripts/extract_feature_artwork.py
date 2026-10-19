#!/usr/bin/env python3
"""Historical evidence replay only; current acquisition supports HeyMelody."""
from pathlib import Path
import sys

if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
    from historical_replay import run
    run('tools/research/earbud-artwork/scripts/extract_feature_artwork.py')
