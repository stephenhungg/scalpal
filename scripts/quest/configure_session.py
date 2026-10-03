#!/usr/bin/env python3
"""Pair a development Quest player with local services; never print invitation credentials."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
from urllib.parse import urlparse

PACKAGE = "com.scalpal.nativeworkbench"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    args = parser.parse_args()
    adb = shutil.which("adb")
    if not adb:
        parser.error("adb is missing")
    config = json.loads(args.config.read_text())
    required = {"uri", "database", "joinCode", "preferredSessionId", "coachBaseUrl"}
    if set(config) != required or any(not isinstance(config[key], str) or not config[key].strip() for key in required):
        parser.error("configuration must contain the five nonempty development connection fields")
    for key, schemes in (("uri", {"ws", "wss"}), ("coachBaseUrl", {"http", "https"})):
        url = urlparse(config[key])
        if url.scheme not in schemes or not url.hostname or url.username or url.password:
            parser.error("configuration has an invalid service URL")
        if url.hostname in {"localhost", "127.0.0.1"} and url.port:
            subprocess.run([adb, "-d", "reverse", f"tcp:{url.port}", f"tcp:{url.port}"],
                           check=True, capture_output=True, timeout=20)
    destination = "files"
    # Fixed command text; private JSON travels only through stdin, never command arguments/logs.
    command = f"run-as {PACKAGE} sh -c 'mkdir -p {destination} && cat > {destination}/session-config.json'"
    result = subprocess.run([adb, "-d", "shell", command], input=json.dumps(config).encode(),
                            capture_output=True, timeout=20)
    if result.returncode:
        parser.error("could not write the development configuration; install the development APK first")
    print("Development Quest paired configuration written; USB service routes ready.")


if __name__ == "__main__":
    main()
