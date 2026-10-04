#!/usr/bin/env python3
"""Real office -> OR Editor lifecycle, synthetic input, fresh local DB, no voice provider."""
import os
import hashlib
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading
import time
import uuid
import urllib.request

repo = Path(__file__).resolve().parents[3]
cli = shutil.which('spacetime')
if not cli or '2.10.2' not in subprocess.check_output([cli, '--version'], text=True):
    raise RuntimeError('Office Play Mode requires pinned SpacetimeDB CLI 2.10.2 on PATH')
with urllib.request.urlopen('http://127.0.0.1:3000/v1/ping', timeout=3) as reply:
    if reply.status != 200:
        raise RuntimeError('Start local SpacetimeDB 2.10.2 on port 3000')
with tempfile.TemporaryDirectory(prefix='scalpal-office-playmode-') as temporary:
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(('ELEVENLABS_', 'JARVIS_', 'SPACETIMEDB_', 'TEST_SPACETIME'))
           and key != 'PATIENT_AGENT_ID'}
    env.update(TEST_SPACETIME_SERVER='http://127.0.0.1:3000', TEST_SPACETIMEDB_URI='ws://127.0.0.1:3000',
               TEST_SPACETIMEDB_DB='scalpal-test-office-' + uuid.uuid4().hex,
               SCALPAL_PLAYMODE_CONFIG=str(Path(temporary) / 'config.json'))
    server = subprocess.Popen([shutil.which('node'), '--import', str(repo / 'services/preop/node_modules/tsx/dist/loader.mjs'),
                               str(Path(__file__).parent / 'server.mts')], cwd=repo, env=env,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    try:
        lines = queue.Queue()
        def drain():
            for line in server.stdout:
                lines.put(line.rstrip())
            lines.put(None)
        threading.Thread(target=drain, daemon=True).start()
        deadline = time.monotonic() + 60
        startup = []
        while True:
            try:
                line = lines.get(timeout=max(0.01, deadline - time.monotonic()))
            except queue.Empty:
                raise RuntimeError('Office fixture did not become ready\n' + '\n'.join(startup))
            if line == 'SCALPAL_OFFICE_PLAYMODE_FIXTURE_READY':
                break
            if line is None:
                raise RuntimeError('Office fixture failed before readiness\n' + '\n'.join(startup))
            startup.append(line)
        log = Path(tempfile.gettempdir()) / ('scalpal-office-playmode-' + uuid.uuid4().hex + '.log')
        unity = env.get('SCALPAL_UNITY', '/Applications/Unity/Hub/Editor/6000.0.66f2/Unity.app/Contents/MacOS/Unity')
        result = subprocess.run([unity, '-batchmode', '-nographics', '-projectPath', str(repo / 'apps/quest'),
                                 '-buildTarget', 'Android', '-executeMethod', 'Scalpal.EncounterOffice.Editor.EncounterOfficePlayModeValidation.Run',
                                 '-logFile', str(log)], cwd=repo, env=env, timeout=240)
        output = log.read_text(errors='replace') if log.exists() else ''
        for line in output.splitlines():
            if line.startswith('SCALPAL_') or 'error CS' in line:
                print(line)
        print('Office Play Mode diagnostic log:', log)
        if result.returncode or 'SCALPAL_OFFICE_PLAYMODE_OK' not in output or 'Leak Detected : Persistent allocates' in output:
            raise SystemExit(1)
    finally:
        server.terminate()
        try:
            server.wait(timeout=8)
        except subprocess.TimeoutExpired:
            server.kill()
            server.wait(timeout=5)
        marker = Path(temporary) / 'native-token-path.txt'
        if marker.exists():
            token = Path(marker.read_text().strip())
            key = hashlib.sha256((env['TEST_SPACETIMEDB_URI'] + '\n' + env['TEST_SPACETIMEDB_DB']).encode()).hexdigest().upper()
            if token.name == 'scalpal-session-' + key + '.token':
                token.unlink(missing_ok=True)
