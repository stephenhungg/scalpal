#!/usr/bin/env python3
"""Configure the separate development office player; does not start voice or capture."""
import argparse
import json
import shutil
import subprocess
from urllib.parse import urlparse

PACKAGE = "com.scalpal.encounteroffice"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--service-url", required=True, help="Preop/encounter service HTTP(S) root URL")
    args = parser.parse_args()
    url = urlparse(args.service_url)
    if (url.scheme not in {"http", "https"} or not url.hostname or url.username or url.password
            or url.query or url.fragment or url.path not in {"", "/"}):
        parser.error("use an HTTP(S) service root URL without credentials, query or fragment")
    try:
        port = url.port
    except ValueError:
        parser.error("invalid service port")
    adb = shutil.which("adb")
    if not adb:
        parser.error("adb is missing")
    if url.hostname in {"localhost", "127.0.0.1"}:
        port = port or (80 if url.scheme == "http" else 443)
        subprocess.run([adb, "-d", "reverse", f"tcp:{port}", f"tcp:{port}"],
                       check=True, capture_output=True, timeout=20)
    command = f"run-as {PACKAGE} sh -c 'mkdir -p files && cat > files/session-config.json'"
    payload = json.dumps({"encounterBaseUrl": args.service_url.rstrip("/")}).encode()
    result = subprocess.run([adb, "-d", "shell", command], input=payload, capture_output=True, timeout=20)
    if result.returncode:
        parser.error("could not configure office; install its development APK first")
    print("Office endpoint configured. Restart the office app to apply it.")


if __name__ == "__main__":
    main()
