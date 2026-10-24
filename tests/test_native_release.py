"""Checks for the native-only build; no game process is started."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile

from test_release import EXE


class NativeReleaseTests(unittest.TestCase):
    def run_launcher(self, exe, *args):
        return subprocess.run([str(exe), *map(str, args)], capture_output=True, timeout=30)

    def test_bundle_and_runtime(self):
        with zipfile.ZipFile(EXE.parent / 'LocalData.zip') as bundle:
            self.assertFalse(any(name.startswith('tools/') or name.endswith('.js')
                                 for name in bundle.namelist()))
            manifest = json.loads(bundle.read('release/manifest.json'))
        runtime = EXE.parent / 'runtime/KdLocalNative.dll'
        self.assertEqual(hashlib.sha256(runtime.read_bytes()).hexdigest(), manifest['nativeSha256'])
        self.assertFalse(any('frida' in path.name.lower() for path in (EXE.parent / 'runtime').iterdir()))

    def test_self_check_and_launch_contract(self):
        with tempfile.TemporaryDirectory(prefix='KartDrift native ') as folder:
            report = Path(folder) / 'self-test.json'
            result = self.run_launcher(EXE, '--self-test', report)
            self.assertEqual(result.returncode, 0, result.stderr.decode('utf8'))
            self.assertTrue(json.loads(report.read_text(encoding='utf8'))['ok'])
            report = Path(folder) / 'launch-state.json'
            result = self.run_launcher(EXE, '--launch-contract', report)
            self.assertEqual(result.returncode, 0, result.stderr.decode('utf8'))
            self.assertEqual(json.loads(report.read_text(encoding='utf8'))['cwd'], folder)

    def test_missing_or_changed_native_library_is_refused(self):
        with tempfile.TemporaryDirectory(prefix='KartDrift native ') as folder:
            root = Path(folder)
            exe = root / EXE.name
            shutil.copyfile(EXE, exe)
            for corrupted in (False, True):
                if corrupted:
                    (root / 'runtime').mkdir()
                    (root / 'runtime/KdLocalNative.dll').write_bytes(b'invalid native library')
                report = root / 'self-test.json'
                result = self.run_launcher(exe, '--self-test', report)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(report.exists())


if __name__ == '__main__':
    unittest.main()
