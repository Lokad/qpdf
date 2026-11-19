"""Expected qpdf source and binary identities for development tools."""

import json
from pathlib import Path


REFERENCE_PIN = json.loads(Path(__file__).with_name("reference-pin.json").read_text(encoding="utf-8"))
